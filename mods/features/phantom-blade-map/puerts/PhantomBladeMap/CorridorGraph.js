"use strict";
const alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/",codes=new Int16Array(128);
for(let i=0;i<alphabet.length;i++)codes[alphabet.charCodeAt(i)]=i;
let cached;
function open(world) {
    if(cached?.world===world)return cached;
    const index=require("./corridors/"+world),fields=Object.entries(index.fields),arrays={},cells=new Map();
    let field=0,chunk=0,encoded,offset=0,out=0,bits=0,value=0,word=0,byte=0,cellCursor=0;
    const graph={world,ready:false,navigationRevision:index.navigationRevision,
        step() {
            const begin=Date.now();
            while(Date.now()-begin<2) {
                if(field<fields.length) {
                    const [key,meta]=fields[field];
                    if(!arrays[key])arrays[key]=new Int32Array(meta.length);
                    if(chunk===meta.chunks){field++;chunk=out=0;continue;}
                    if(encoded===undefined){encoded=require("./corridors/"+world+"/"+key+"-"+chunk);offset=bits=value=word=byte=0;}
                    const end=Math.min(encoded.length,offset+2048);
                    for(;offset<end;offset++) {
                        const c=encoded.charCodeAt(offset);if(c===61)continue;
                        value=(value<<6)|codes[c];bits+=6;
                        if(bits>=8){bits-=8;word|=((value>>>bits)&255)<<(byte++*8);
                            if(byte===4){arrays[key][out++]=word;byte=word=0;}}
                    }
                    if(offset===encoded.length){encoded=undefined;chunk++;}
                } else {
                    const end=Math.min(arrays.cells.length,cellCursor+512);
                    for(;cellCursor<end;cellCursor+=4)cells.set(arrays.cells[cellCursor]+":"+arrays.cells[cellCursor+1],[arrays.cells[cellCursor+2],arrays.cells[cellCursor+3]]);
                    if(cellCursor===arrays.cells.length){Object.assign(graph,arrays);graph.ready=true;return true;}
                }
            }
            return false;
        },
        point(id){return {x:arrays.nodes[id*4],y:arrays.nodes[id*4+1],z:arrays.nodes[id*4+2],floor:arrays.nodes[id*4+3]};},
        component(id){return arrays.components[id];},
        portal(edge,from) {
            const offset=edge/5*8,v=arrays.portals,to=arrays.edges[edge];
            const a={x:v[offset],y:v[offset+1],z:v[offset+2],exitZ:v[offset+3]},
                b={x:v[offset+4],y:v[offset+5],z:v[offset+6],exitZ:v[offset+7]};
            const p=graph.point(from),q=graph.point(to),cross=(q.x-p.x)*(a.y-b.y)-(q.y-p.y)*(a.x-b.x);
            return {left:cross>=0?a:b,right:cross>=0?b:a,from:p.floor,to:q.floor};
        },
        height(id,x,y) {
            const start=arrays.polys[id*2]*3,count=arrays.polys[id*2+1],v=arrays.vertices;
            for(let i=1;i<count-1;i++) {
                const b=start+i*3,c=b+3,ux=v[b]-v[start],uy=v[b+1]-v[start+1],vx=v[c]-v[start],vy=v[c+1]-v[start+1];
                const det=ux*vy-uy*vx;if(Math.abs(det)<1)continue;
                const dx=x-v[start],dy=y-v[start+1],u=(dx*vy-dy*vx)/det,w=(ux*dy-uy*dx)/det;
                if(u>=-.001 && w>=-.001 && u+w<=1.001)return v[start+2]+u*(v[b+2]-v[start+2])+w*(v[c+2]-v[start+2]);
            }
            return undefined;
        },
        project(id,p) {
            const v=arrays.vertices;let x=p.x,y=p.y,z=graph.height(id,x,y),horizontal=0;
            if(z===undefined) {
                horizontal=Infinity;
                const start=arrays.polys[id*2],count=arrays.polys[id*2+1];
                for(let i=0;i<count;i++) {
                    const a=(start+i)*3,b=(start+(i+1)%count)*3,dx=v[b]-v[a],dy=v[b+1]-v[a+1],length=dx*dx+dy*dy;
                    const t=length?Math.max(0,Math.min(1,((p.x-v[a])*dx+(p.y-v[a+1])*dy)/length)):0;
                    const qx=v[a]+t*dx,qy=v[a+1]+t*dy,d=Math.hypot(qx-p.x,qy-p.y);
                    if(d<horizontal){horizontal=d;x=qx;y=qy;z=v[a+2]+t*(v[b+2]-v[a+2]);}
                }
            }
            return {id,x,y,z,floor:graph.point(id).floor,horizontal,height:Math.abs(z-p.z)};
        },
        locate(p,maxHeight=300,radius=0) {
            let found,best=Infinity;const seen=new Set();
            for(let cx=Math.floor((p.x-radius)/index.cellSize);cx<=Math.floor((p.x+radius)/index.cellSize);cx++)
                for(let cy=Math.floor((p.y-radius)/index.cellSize);cy<=Math.floor((p.y+radius)/index.cellSize);cy++) {
                    const cell=cells.get(cx+":"+cy);if(!cell)continue;
                    for(let n=cell[0];n<cell[0]+cell[1];n++) {
                        const id=arrays.members[n];if(seen.has(id))continue;seen.add(id);
                        const q=graph.project(id,p),score=q.horizontal+q.height*2;
                        if(q.horizontal>radius || q.height>maxHeight || score>=best)continue;
                        best=score;found=q;
                    }
                }
            return found;
        }
    };
    cached=graph;return graph;
}
function release(){cached=undefined;}
module.exports={open,release};
