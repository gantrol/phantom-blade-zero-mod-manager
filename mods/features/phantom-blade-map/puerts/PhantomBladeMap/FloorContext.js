"use strict";
// Current-floor patches keep their full budget. Context uses a separate small
// pool and cannot evict the floor the player is examining.
class FloorContext {
    constructor(limit) {this.limit=limit;this.retained=new Set();this.atlas=undefined;}
    patches(atlas,layer,camera,width,height) {
        if(this.atlas!==atlas || this.mapID!==atlas?.mapID){this.clear();this.atlas=atlas;this.mapID=atlas?.mapID;}
        if(!atlas?.layers.length)return [];
        let level=atlas.levels.findIndex(cm=>cm/camera.cm>=.7);
        if(level<0)level=atlas.levels.length-1;
        if(this.level!==level){this.retained.clear();this.level=level;}
        const span=atlas.levels[level]*atlas.tilePixels,patches=[];
        const screen=(x,y)=>({x:width/2+(x-camera.x)/camera.cm,y:height/2+(y-camera.y)/camera.cm});
        const inView=(x,y,size,padding=0)=>{const p=screen(x,y),s=size/camera.cm;return p.x<=width+padding && p.y<=height+padding && p.x+s>=-padding && p.y+s>=-padding;};
        const nearby=floor=>{
            const tiles=atlas.layers[floor].tiles[level],result=[];
            for(let i=0;i<tiles.length;i+=2) {
                const x=tiles[i]*span,y=tiles[i+1]*span;
                const id=floor+":"+tiles[i]+":"+tiles[i+1];
                if(inView(x,y,span,floor===layer?48:0))result.push({id,x,y,tx:tiles[i],ty:tiles[i+1],floor,preload:!inView(x,y,span),
                    distance:Math.hypot(x+span/2-camera.x,y+span/2-camera.y)-(this.retained.has(id)?span*.12:0)});
            }
            return result.sort((a,b)=>Number(a.preload)-Number(b.preload) || a.distance-b.distance || a.tx-b.tx || a.ty-b.ty);
        };
        const add=(tile,budgetEnd)=>{
            if(patches.length>=budgetEnd)return;
            const ghost=tile.floor!==layer,name="T_"+(tile.tx+"_"+tile.ty).replace(/-/g,"m");
            const path="/Game/PhantomBladeMap/Atlas/"+atlas.world+"/L"+tile.floor+"/S"+atlas.levels[level]+"/"+name+"."+name;
            // Terrain is available before exploration, including a fresh cycle.
            // Exploration filtering belongs to markers, not floor textures.
            patches.push({x:tile.x,y:tile.y,size:span,tile,span,path,ghost,opacity:ghost ? .08 : 1,key:path});
        };
        const hasFloor=!!atlas.layers[layer];
        if(hasFloor)for(const tile of nearby(layer))add(tile,this.limit);
        // Water has no walking surface. Keep surrounding shores as context
        // while leaving the player's floor unknown.
        const contextEnd=patches.length+(hasFloor?Math.min(32,Math.ceil(this.limit/2)):this.limit);
        const queues=atlas.layers.map((_,index)=>index).filter(index=>index!==layer)
            .sort((a,b)=>Math.abs(a-layer)-Math.abs(b-layer)).map(nearby).filter(q=>q.length);
        // Round robin gives each overlapping floor a share of the context pool.
        while(queues.some(q=>q.length) && patches.length<contextEnd)
            for(const queue of queues)if(queue.length)add(queue.shift(),contextEnd);
        this.retained=new Set(patches.map(p=>p.tile.id));
        return patches;
    }
    clear(){this.retained.clear();this.atlas=this.mapID=this.level=undefined;}
}
module.exports={FloorContext};
