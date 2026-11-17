"use strict";
// Fixed atlas tiles already form a spatial grid; no quadtree rebuild is needed.
// Weak keys let the index leave memory with its decoded world/LOD.
const tileIndices = new WeakMap();
function tileIndex(tiles) {
    let index = tileIndices.get(tiles);
    if (!index) {
        index = new Map();
        for (let i = 0; i < tiles.length; i += 2) index.set(tiles[i] + ":" + tiles[i + 1], i);
        tileIndices.set(tiles, index);
    }
    return index;
}
// A local stack owns its floor controls. Tile coverage is only a fallback for
// an unresolved position or an older atlas without local group identities.
function nearbyFloors(atlas, camera, width, height, currentLayer) {
    const group = atlas?.layers[currentLayer]?.group;
    if (group && atlas.groups?.has(group)) {
        const storey = i => atlas.layers[i].kind === "under" ? -atlas.layers[i].depth : atlas.layers[i].depth;
        return atlas.groups.get(group).layers.slice().sort((a, b) => storey(b) - storey(a));
    }
    const span = (atlas?.levels?.[0] || 0) * (atlas?.tilePixels || 0);
    const canLocate = span > 0 && Number.isFinite(camera?.x) && Number.isFinite(camera?.y);
    const cm = camera?.cm || 50;
    const minX = canLocate ? Math.floor((camera.x - width * cm / 2) / span) : 0;
    const maxX = canLocate ? Math.floor((camera.x + width * cm / 2) / span) : 0;
    const minY = canLocate ? Math.floor((camera.y - height * cm / 2) / span) : 0;
    const maxY = canLocate ? Math.floor((camera.y + height * cm / 2) / span) : 0;
    const floors = [];
    for (let i = 0; i < (atlas?.layers.length || 0); i++) {
        if (i === currentLayer) { floors.push(i); continue; }
        const tiles = canLocate ? atlas.layers[i].tiles?.[0] : undefined;
        if (!tiles?.length) continue;
        let present = false;
        if ((maxX - minX + 1) * (maxY - minY + 1) < tiles.length / 2) {
            const index = tileIndex(tiles);
            for (let x = minX; x <= maxX && !present; x++)
                for (let y = minY; y <= maxY && !present; y++) present = index.has(x + ":" + y);
        } else {
            for (let n = 0; n < tiles.length && !present; n += 2)
                present = tiles[n] >= minX && tiles[n] <= maxX && tiles[n + 1] >= minY && tiles[n + 1] <= maxY;
        }
        if (present) floors.push(i);
    }
    const storey = i => atlas.layers[i].kind === "under" ? -atlas.layers[i].depth : atlas.layers[i].depth;
    return floors.sort((a, b) => storey(b) - storey(a));
}
// Current-floor patches keep their full budget. Context uses a separate small
// pool and cannot evict the floor the player is examining.
class FloorContext {
    constructor(limit) {this.limit=limit;this.retained=new Set();this.selectedLayers=new Map();this.atlas=undefined;}
    selection(source, layer) {
        if (!source) return new Set();
        const current = source.layers[layer];
        if (current) this.selectedLayers.set(source.world + ":" + (current.group || "legacy"), layer);
        return new Set([...(source.groups || new Map()).entries()].map(([id, group]) =>
            this.selectedLayers.get(source.world + ":" + id) ?? group.base));
    }
    shows(source, layer) {
        const floor = source?.layers[layer];
        if (!floor) return layer < 0;
        const group = floor.group || "legacy";
        return (this.selectedLayers.get(source.world + ":" + group) ?? source.groups?.get(group)?.base) === layer;
    }
    patches(atlas,layer,camera,width,height) {
        if(this.atlas!==atlas || this.mapID!==atlas?.mapID){this.retained.clear();this.atlas=atlas;this.mapID=atlas?.mapID;}
        if(!atlas?.layers.length)return [];
        const selected = this.selection(atlas, layer);
        let level=0;
        while(level+1<atlas.levels.length && atlas.levels[level+1]<=camera.cm*1.5)level++;
        const patches=[];
        const screen=(x,y)=>({x:width/2+(x-camera.x)/camera.cm,y:height/2+(y-camera.y)/camera.cm});
        const inView=(x,y,size,padding=0)=>{const p=screen(x,y),s=size/camera.cm;return p.x<=width+padding && p.y<=height+padding && p.x+s>=-padding && p.y+s>=-padding;};
        const queries = new Map();
        const nearby=(source,floor,padding=0,lod=level)=>{
            const queryKey=source.world+":"+floor+":"+lod+":"+padding;
            if(queries.has(queryKey))return queries.get(queryKey);
            const tiles=(floor===-1?source.terrain:source.layers[floor])?.tiles?.[lod],span=atlas.levels[lod]*atlas.tilePixels;
            if(!tiles)return [];
            const result=[];
            const append=i=>{
                const x=tiles[i]*span,y=tiles[i+1]*span;
                const id=source.world+":"+floor+":"+lod+":"+tiles[i]+":"+tiles[i+1];
                if(inView(x,y,span,padding))result.push({id,world:source.world,x,y,tx:tiles[i],ty:tiles[i+1],floor,lod,span,preload:!inView(x,y,span),
                    distance:Math.hypot(x+span/2-camera.x,y+span/2-camera.y)-(this.retained.has(id)?span*.12:0)});
            };
            // Include the tile touching the left/top boundary, just as inView does.
            const minX=Math.ceil((camera.x-(width/2+padding)*camera.cm)/span)-1;
            const maxX=Math.floor((camera.x+(width/2+padding)*camera.cm)/span);
            const minY=Math.ceil((camera.y-(height/2+padding)*camera.cm)/span)-1;
            const maxY=Math.floor((camera.y+(height/2+padding)*camera.cm)/span);
            if((maxX-minX+1)*(maxY-minY+1)<tiles.length/2) {
                const index=tileIndex(tiles);
                for(let x=minX;x<=maxX;x++)for(let y=minY;y<=maxY;y++) {
                    const i=index.get(x+":"+y);
                    if(i!==undefined)append(i);
                }
            } else for(let i=0;i<tiles.length;i+=2)append(i);
            result.sort((a,b)=>Number(a.preload)-Number(b.preload) || a.distance-b.distance || a.tx-b.tx || a.ty-b.ty);
            queries.set(queryKey,result);
            return result;
        };
        const add=(tile,budgetEnd,opacity,zOrder)=>{
            if(patches.length>=budgetEnd)return;
            const ghost=tile.world!==atlas.world || tile.floor!==layer,name="T_"+(tile.tx+"_"+tile.ty).replace(/-/g,"m"),span=tile.span;
            const path="/Game/PhantomBladeMap/Atlas/"+tile.world+"/L"+tile.floor+"/S"+atlas.levels[tile.lod]+"/"+name+"."+name;
            // Terrain is available before exploration, including a fresh cycle.
            // Exploration filtering belongs to markers, not floor textures.
            patches.push({x:tile.x,y:tile.y,size:span,tile,span,path,ghost,opacity,zOrder,key:path});
        };
        let primary=layer>=0?nearby(atlas,layer):[];
        // Prefer a complete coarser floor over holes from truncating fine tiles.
        while(primary.length>this.limit && level<atlas.levels.length-1)primary=nearby(atlas,layer,0,++level);
        // Keep a complete coarse base while detail tiles stream in. A failed
        // or delayed fine load must not leave a rectangular hole in the map.
        const coarse=atlas.levels.length-1;
        const backdrop=layer>=0 && level<coarse?nearby(atlas,layer,0,coarse):[];
        if(backdrop.length<=8)for(const tile of backdrop)add(tile,8,.65,3);
        const primaryEnd=patches.length+this.limit;
        for(const tile of primary)add(tile,primaryEnd,1,4);
        // Small local floors leave most of the primary pool free. Reuse that
        // space for independent places without increasing the total tile cap.
        const contextLimit=primaryEnd-patches.length+Math.min(48,Math.ceil(this.limit/2)),contextEnd=patches.length+contextLimit;
        // Every place keeps its own selection (ground until first selected).
        // Never apply this place's floor number to another stack or overlay all
        // storeys of the active stack just because their depth labels match.
        const contexts=[];
        for(const floor of selected)if(floor!==layer)
            contexts.push({source:atlas,floor,opacity:.65,zOrder:2,priority:2});
        for(const sibling of atlas.siblings || []) {
            for(const floor of this.selection(sibling))contexts.push({source:sibling,floor,opacity:.5,zOrder:1,priority:1});
        }
        const queues=contexts.map(context=>({...context,lod:level,items:nearby(context.source,context.floor,0,level),cursor:0})).filter(q=>q.items.length);
        // Reduce only the layer that frees the most budget for the least loss
        // of useful context. One crowded upper floor must not blur all streets.
        while(queues.reduce((sum,q)=>sum+q.items.length,0)>contextLimit) {
            let choice,items,lod,best=0;
            for(const queue of queues)for(let next=queue.lod+1;next<atlas.levels.length;next++) {
                const candidate=nearby(queue.source,queue.floor,0,next);
                // Some local floors have no visible tiles at a coarser LOD.
                // Keep their current tiles until a nonempty replacement fits.
                if(!candidate.length)continue;
                const saving=(queue.items.length-candidate.length)/(queue.priority*(atlas.levels[next]/atlas.levels[level]));
                if(saving>best){best=saving;choice=queue;items=candidate;lod=next;}
            }
            if(!choice)break;
            choice.items=items;choice.lod=lod;
        }
        queues.sort((a,b)=>b.priority-a.priority || a.items[0].distance-b.items[0].distance);
        // If even the coarsest context exceeds the pool, omit a whole distant
        // floor. Truncating its tile list would create rectangular map holes.
        while(queues.reduce((sum,q)=>sum+q.items.length,0)>contextLimit)queues.pop();
        for(const queue of queues)for(const tile of queue.items)add(tile,contextEnd,queue.opacity,queue.zOrder);
        // AI navigation ends at authored bounds even when landscape continues.
        // Draw the decoded landscape below every floor, with its own small pool.
        // Coarsen a whole backdrop rather than truncating it into rectangles.
        const terrainEnd=patches.length+24;
        for(const source of [atlas,...(atlas.siblings || [])]) {
            if(!source.terrain)continue;
            let lod=level,tiles=nearby(source,-1,0,lod),base=lod<coarse?nearby(source,-1,0,coarse):[];
            while(tiles.length+base.length>terrainEnd-patches.length && lod<coarse) {
                tiles=nearby(source,-1,0,++lod);
                if(lod===coarse)base=[];
            }
            if(tiles.length+base.length<=terrainEnd-patches.length) {
                for(const tile of base)add(tile,terrainEnd,1,-1);
                for(const tile of tiles)add(tile,terrainEnd,1,0);
            }
        }
        this.retained=new Set(patches.map(p=>p.tile.id));
        return patches;
    }
    clear(preserveSelections=false){this.retained.clear();if(!preserveSelections)this.selectedLayers.clear();this.atlas=this.mapID=this.level=undefined;}
}
module.exports={FloorContext,nearbyFloors};
