"use strict";
const {loadModule}=require("puerts");
let nextOwner=0;
// Only original numeric surfaces enter this cache. No engine navigation query,
// nearest-node fallback or inferred connection can assign a floor in empty space.
class FloorSurfaces {
    constructor(world,revision,decode) {
        this.world=world;this.decode=decode;this.cache=new Map();this.points=new Map();this.pending=new Set();this.bytes=0;this.revision=0;
        this.owner=++nextOwner;this.inflight=new Set();this.failed=new Set();this.closed=false;
        this.index=require("./surfaces/"+world);
        this.valid=this.index.schema===2 && this.index.encoding==="pbs-zlib-1" && this.index.navigationRevision===revision;
        this.tiles=new Set();
        for(let i=0;i<this.index.tiles.length;i+=2)this.tiles.add(this.index.tiles[i]+"_"+this.index.tiles[i+1]);
    }
    tile(x,y) {
        const key=x+"_"+y;if(this.closed || !this.valid || !this.tiles.has(key) || this.failed.has(key))return;
        let tile=this.cache.get(key);
        if(tile){this.cache.delete(key);this.cache.set(key,tile);return tile;}
        this.pending.add(key);this.incomplete=true;
    }
    step() {
        if(this.closed || !this.valid)return false;
        const begin=Date.now();let loaded=false;
        // The native worker owns disk reads, inflate and checksum verification.
        // Poll at most four chunks and accept at most one completed chunk per tick.
        let polled=0;
        for(const key of this.pending) {
            if(polled>=4 || Date.now()-begin>=2)break;
            if(!this.inflight.has(key) && this.inflight.size>=4)continue;
            polled++;
            try {
                const raw=loadModule("pbz-map-surface-v1/"+this.owner+"/"+this.world+"/"+key);
                if(raw==="~"){this.inflight.add(key);continue;}
                if(typeof raw!=="string" || raw.startsWith("!"))throw new Error(raw || "SURFACE_BRIDGE");
                this.load(key,raw);
            } catch(error) {
                this.failed.add(key);
                if(!this.warned){this.warned=true;console.warn("[PhantomBladeMap] surface chunk unavailable: "+String(error));}
            }
            this.pending.delete(key);this.inflight.delete(key);loaded=true;
            break;
        }
        if(loaded)this.revision++;
        return loaded;
    }
    load(key,raw) {
        const buffer=this.decode(raw).buffer,header=new DataView(buffer);
        if(buffer.byteLength<12 || buffer.byteLength>1024*1024)throw new Error("SURFACE_SIZE");
        const polygons=header.getUint32(0,true),offsets=header.getUint32(4,true),members=header.getUint32(8,true);
        if(offsets!==this.index.tileCells*this.index.tileCells+1 || 12+4*(polygons+offsets+members)!==buffer.byteLength)
            throw new Error("SURFACE_ARRAYS");
        const tile={polygons:new Float32Array(buffer,12,polygons),offsets:new Int32Array(buffer,12+polygons*4,offsets),
            members:new Int32Array(buffer,12+(polygons+offsets)*4,members)};
        tile.bytes=buffer.byteLength;
        this.cache.set(key,tile);this.bytes+=tile.bytes;
        while(this.cache.size>1 && (this.cache.size>32 || this.bytes>8*1024*1024)) {
            const oldest=this.cache.keys().next().value;this.bytes-=this.cache.get(oldest).bytes;this.cache.delete(oldest);
        }
        return tile;
    }
    locate(point,layer) {
        const key=[point.x,point.y,point.groundZ ?? point.z,layer,Number.isFinite(point.groundZ)].join(":");
        if(this.points.has(key))return this.points.get(key);
        this.incomplete=false;
        const result=this.query(point,layer);
        if(this.incomplete)return;
        if(this.points.size>=4096)this.points.delete(this.points.keys().next().value);
        this.points.set(key,result);return result;
    }
    query(point,layer) {
        if(!this.valid || !Number.isFinite(point.x) || !Number.isFinite(point.y))return;
        const z=point.groundZ ?? point.z,hasZ=Number.isFinite(z);
        if(!hasZ && layer===undefined)return;
        const {cellSize,tileCells}=this.index;
        // Nav surfaces are inset from walls. Items may sit beside that inset,
        // while player feet need a tighter match. Project to real polygon edges,
        // never to a cell's height interval or a remote representative node.
        const radius=layer!==undefined?0:Number.isFinite(point.groundZ)?100:200;
        const candidates=new Map(),seen=new Map();
        for(let cx=Math.floor((point.x-radius)/cellSize);cx<=Math.floor((point.x+radius)/cellSize);cx++)
            for(let cy=Math.floor((point.y-radius)/cellSize);cy<=Math.floor((point.y+radius)/cellSize);cy++) {
                const tx=Math.floor(cx/tileCells),ty=Math.floor(cy/tileCells),tile=this.tile(tx,ty);
                if(!tile)continue;
                if(!seen.has(tile))seen.set(tile,new Set());
                const visited=seen.get(tile),cell=(cy-ty*tileCells)*tileCells+cx-tx*tileCells,v=tile.polygons;
                for(let n=tile.offsets[cell];n<tile.offsets[cell+1];n++) {
                    const offset=tile.members[n];if(visited.has(offset))continue;visited.add(offset);
                    const floor=v[offset],count=v[offset+1],a=offset+(this.index.schema===2?3:2);
                    const space=this.index.schema===2?(["unknown","underpass","caveOrRoom"][v[offset+2]] || "unknown"):"unknown";
                    if(layer!==undefined && floor!==layer)continue;
                    let height,horizontal=Infinity;
                    for(let i=1;i<count-1;i++) {
                        const b=a+i*3,c=b+3,ux=v[b]-v[a],uy=v[b+1]-v[a+1],vx=v[c]-v[a],vy=v[c+1]-v[a+1];
                        const det=ux*vy-uy*vx;if(Math.abs(det)<.001)continue;
                        const dx=point.x-v[a],dy=point.y-v[a+1],u=(dx*vy-dy*vx)/det,w=(ux*dy-uy*dx)/det;
                        if(u<-.00001 || w<-.00001 || u+w>1.00001)continue;
                        height=v[a+2]+u*(v[b+2]-v[a+2])+w*(v[c+2]-v[a+2]);horizontal=0;break;
                    }
                    if(horizontal>0 && radius>0)for(let i=0;i<count;i++) {
                        const b=a+i*3,c=a+(i+1)%count*3,dx=v[c]-v[b],dy=v[c+1]-v[b+1],length=dx*dx+dy*dy;
                        const t=length?Math.max(0,Math.min(1,((point.x-v[b])*dx+(point.y-v[b+1])*dy)/length)):0;
                        const distance=Math.hypot(point.x-v[b]-t*dx,point.y-v[b+1]-t*dy);
                        if(distance<horizontal){horizontal=distance;height=v[b+2]+t*(v[c+2]-v[b+2]);}
                    }
                    if(horizontal>radius || !Number.isFinite(height))continue;
                    const vertical=hasZ?Math.abs(z-height):0;
                    if(layer===undefined && vertical>250)continue;
                    const score=Math.hypot(horizontal*2,vertical),previous=candidates.get(floor);
                    if(!previous || score<previous.score)candidates.set(floor,{layer:floor,z:height,score,space});
                }
            }
        const ordered=[...candidates.values()].sort((a,b)=>a.score-b.score);
        return ordered.length>1 && ordered[1].score-ordered[0].score<1 ? undefined : ordered[0];
    }
    pause(){
        try{loadModule("pbz-map-surface-v1/"+this.owner+"/cancel");}catch(_){}
        this.pending.clear();this.inflight.clear();
        // A transient chunk failure may recover on the next visit. Successful
        // numeric chunks remain reusable; cached misses must be queried again.
        if(this.failed.size){this.failed.clear();this.points.clear();this.warned=false;}
        // Cancelled worker completions cannot belong to a later acquisition.
        this.owner=++nextOwner;
    }
    dispose(){
        if(this.closed)return;
        this.closed=true;
        this.pause();
        this.cache.clear();this.points.clear();this.pending.clear();this.inflight.clear();this.failed.clear();this.bytes=0;
    }
}
module.exports={FloorSurfaces};
