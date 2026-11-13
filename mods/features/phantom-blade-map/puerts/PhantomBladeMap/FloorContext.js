"use strict";
// Current-floor patches keep their full budget. Context uses a separate small
// pool and cannot evict the floor the player is examining.
class FloorContext {
    constructor(limit) {this.limit=limit;this.retained=new Set();this.atlas=undefined;}
    patches(atlas,layer,camera,width,height) {
        if(this.atlas!==atlas || this.mapID!==atlas?.mapID){this.clear();this.atlas=atlas;this.mapID=atlas?.mapID;}
        if(!atlas?.layers.length)return [];
        let level=0;
        while(level+1<atlas.levels.length && atlas.levels[level+1]<=camera.cm*1.5)level++;
        const patches=[];
        const screen=(x,y)=>({x:width/2+(x-camera.x)/camera.cm,y:height/2+(y-camera.y)/camera.cm});
        const inView=(x,y,size,padding=0)=>{const p=screen(x,y),s=size/camera.cm;return p.x<=width+padding && p.y<=height+padding && p.x+s>=-padding && p.y+s>=-padding;};
        const nearby=(source,floor,padding=0,lod=level)=>{
            const tiles=source.layers[floor]?.tiles?.[lod],span=atlas.levels[lod]*atlas.tilePixels;
            if(!tiles)return [];
            const result=[];
            for(let i=0;i<tiles.length;i+=2) {
                const x=tiles[i]*span,y=tiles[i+1]*span;
                const id=source.world+":"+floor+":"+lod+":"+tiles[i]+":"+tiles[i+1];
                if(inView(x,y,span,padding))result.push({id,world:source.world,x,y,tx:tiles[i],ty:tiles[i+1],floor,lod,span,preload:!inView(x,y,span),
                    distance:Math.hypot(x+span/2-camera.x,y+span/2-camera.y)-(this.retained.has(id)?span*.12:0)});
            }
            return result.sort((a,b)=>Number(a.preload)-Number(b.preload) || a.distance-b.distance || a.tx-b.tx || a.ty-b.ty);
        };
        const add=(tile,budgetEnd,opacity,zOrder)=>{
            if(patches.length>=budgetEnd)return;
            const ghost=tile.world!==atlas.world || tile.floor!==layer,name="T_"+(tile.tx+"_"+tile.ty).replace(/-/g,"m"),span=tile.span;
            const path="/Game/PhantomBladeMap/Atlas/"+tile.world+"/L"+tile.floor+"/S"+atlas.levels[tile.lod]+"/"+name+"."+name;
            // Terrain is available before exploration, including a fresh cycle.
            // Exploration filtering belongs to markers, not floor textures.
            patches.push({x:tile.x,y:tile.y,size:span,tile,span,path,ghost,opacity,zOrder,key:path});
        };
        const storey=l=>!l?0:l.kind==="under"?-l.depth:l.depth;
        const activeStorey=storey(atlas.layers[layer]);
        let primary=nearby(atlas,layer);
        // Prefer a complete coarser floor over holes from truncating fine tiles.
        while(primary.length>this.limit && level<atlas.levels.length-1)primary=nearby(atlas,layer,0,++level);
        // Keep a complete coarse base while detail tiles stream in. A failed
        // or delayed fine load must not leave a rectangular hole in the map.
        const coarse=atlas.levels.length-1;
        const backdrop=level<coarse?nearby(atlas,layer,0,coarse):[];
        if(backdrop.length<=8)for(const tile of backdrop)add(tile,8,.65,3);
        const primaryEnd=patches.length+this.limit;
        for(const tile of primary)add(tile,primaryEnd,1,4);
        const contextLimit=Math.min(48,Math.ceil(this.limit/2)),contextEnd=patches.length+contextLimit;
        // Base ground (L0) holds ~85% of non-overlapping street/shore geometry.
        // When browsing an upper or lower storey, always render L0 right underneath
        // at readable contrast so the surrounding map never turns into black voids.
        const groundIndex=atlas.layers.findIndex(l=>l.kind==="ground");
        const contexts=[];
        if(groundIndex>=0 && groundIndex!==layer)contexts.push({source:atlas,floor:groundIndex,opacity:.48,zOrder:2,priority:4});
        // Adjacent and other storeys in the current world share the context pool,
        // ordered by true signed storey distance rather than raw array index.
        const otherIndices=atlas.layers.map((_,index)=>index).filter(index=>index!==layer && index!==groundIndex)
            .sort((a,b)=>Math.abs(storey(atlas.layers[a])-activeStorey)-Math.abs(storey(atlas.layers[b])-activeStorey));
        for(const index of otherIndices) {
            const adjacent=Math.abs(storey(atlas.layers[index])-activeStorey)<=1;
            contexts.push({source:atlas,floor:index,opacity:adjacent?.28:.16,zOrder:1,priority:adjacent?2:1});
        }
        // Composite regions (e.g. PangZhen = pangzhen + houjie + ximeng) share world
        // coordinates across sibling sub-maps; fill visible sibling ground & adjacent tiles.
        for(const sibling of atlas.siblings || []) {
            const sibGround=sibling.layers.findIndex(l=>l.kind==="ground");
            if(sibGround>=0)contexts.push({source:sibling,floor:sibGround,opacity:.32,zOrder:1,priority:3});
            for(let idx=0;idx<sibling.layers.length;idx++)if(idx!==sibGround) {
                contexts.push({source:sibling,floor:idx,opacity:.07,zOrder:1,priority:1});
            }
        }
        const queues=contexts.map(context=>({...context,lod:level,items:nearby(context.source,context.floor,0,level),cursor:0})).filter(q=>q.items.length);
        // Reduce only the layer that frees the most budget for the least loss
        // of useful context. One crowded upper floor must not blur all streets.
        while(queues.reduce((sum,q)=>sum+q.items.length,0)>contextLimit) {
            let choice,items,lod,best=0;
            for(const queue of queues)for(let next=queue.lod+1;next<atlas.levels.length;next++) {
                const candidate=nearby(queue.source,queue.floor,0,next);
                const saving=(queue.items.length-candidate.length)/(queue.priority*(atlas.levels[next]/atlas.levels[level]));
                if(saving>best){best=saving;choice=queue;items=candidate;lod=next;}
            }
            if(!choice)break;
            choice.items=items;choice.lod=lod;
        }
        queues.sort((a,b)=>b.priority-a.priority);
        // If even the coarsest context exceeds the pool, omit a whole distant
        // floor. Truncating its tile list would create rectangular map holes.
        while(queues.reduce((sum,q)=>sum+q.items.length,0)>contextLimit)queues.pop();
        for(const queue of queues)for(const tile of queue.items)add(tile,contextEnd,queue.opacity,queue.zOrder);
        this.retained=new Set(patches.map(p=>p.tile.id));
        return patches;
    }
    clear(){this.retained.clear();this.atlas=this.mapID=this.level=undefined;}
}
module.exports={FloorContext};
