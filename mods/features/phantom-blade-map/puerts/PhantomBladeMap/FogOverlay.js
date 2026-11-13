"use strict";
const Fog=require("./Fog");
const {rgba}=require("./Appearance");
const Diagnostics=require("./Diagnostics");
class FogOverlay {
    constructor(view,parent){this.view=view;this.parent=parent;this.cells=[];this.samples=new Map();}
    update(atlas,layer,camera,width,height,coverage,enabled) {
        if(!enabled || !atlas?.layers[layer]){this.clear();return;}
        // World-aligned, 16–32px cells, capped at 128 cells along the long axis.
        // A translucent feathered mask preserves terrain beneath unexplored areas.
        const span=2**Math.ceil(Math.log2(camera.cm*Math.max(16,width/128,height/128)));
        const key=[atlas.world,atlas.mapID,layer,span].join(":");
        if(this.key!==key || this.atlas!==atlas) {
            this.clear();this.key=key;this.atlas=atlas;this.samples=atlas.fogGrid(layer,span);
            this.span=span;this.mapID=atlas.mapID;this.layer=layer;
        }
        this.camera={...camera};this.width=width;this.height=height;this.coverage=coverage;
        const left=Math.floor((camera.x-width*camera.cm/2)/span),top=Math.floor((camera.y-height*camera.cm/2)/span);
        const right=Math.floor((camera.x+width*camera.cm/2)/span),bottom=Math.floor((camera.y+height*camera.cm/2)/span);
        this.bounds={left,top,right,bottom};this.visible=[];this.pending=[];
        // The extra ring keeps feathering independent of the viewport edge.
        for(let y=top-1;y<=bottom+1;y++)for(let x=left-1;x<=right+1;x++) {
            const id=x+":"+y,sample=this.samples.get(id);if(!sample)continue;
            this.visible.push([id,sample]);if(!sample.uv)this.pending.push(sample);
        }
        this.draw();this.step();
    }
    step() {
        if(this.timer!==undefined){clearTimeout(this.timer);this.timer=undefined;}
        if(!this.key)return;
        const fog=this.coverage.record({mapID:this.mapID})?.fog;
        if(!fog?.known){this.hide();return;}
        let count=0;
        this.coverage.sample(this.view.page,this.view.UE,()=>{
            const started=Date.now();
            while(count<128 && count<this.pending.length && (count===0 || Date.now()-started<3)) {
                const sample=this.pending[count],uv=this.coverage.project({...sample,mapID:this.mapID});
                if(!uv)break;
                sample.uv=uv;count++;
            }
        });
        this.pending.splice(0,count);
        // Large-map data refreshes are event-driven. Finish pending projections
        // independently, without rebuilding markers or waiting for mouse input.
        if(this.pending.length)this.timer=setTimeout(()=>this.step(),count?16:100);
        else {
            this.render(fog);
            if(!this.reported) {
                this.reported=true;
                Diagnostics.record("atlas-fog-ready",{world:this.atlas.world,layer:this.layer,span:this.span,samples:this.visible.length,rectangles:this.rectangles.length});
            }
        }
    }
    render(fog) {
        const rasterKey=[this.key,...Object.values(this.bounds),fog.revision].join(":");
        if(this.rasterKey===rasterKey){this.draw();return;}
        const hidden=new Set();
        for(const [key,{uv}] of this.visible)
            if(uv && uv.every(value=>value>=-.02 && value<=1.02) && !Fog.visible(fog,...uv))hidden.add(key);
        const {left,top,right,bottom}=this.bounds,rectangles=[];
        const weight=(x,y)=>{
            let count=0;
            for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++)if(hidden.has((x+dx)+":"+(y+dy)))count++;
            return count;
        };
        // Merge equal horizontal runs and extend matching runs vertically.
        // Contiguous fog uses a few rectangles, not thousands of cell widgets.
        let previous=new Map();
        for(let y=top;y<=bottom;y++) {
            const row=new Map();let start=left,level=weight(left,y);
            for(let x=left+1;x<=right+1;x++) {
                const next=x<=right?weight(x,y):-1;if(next===level)continue;
                if(level) {
                    const key=[start,x,level].join(":"),old=previous.get(key);
                    const rect=old || {x:start,y,width:x-start,height:0,level};
                    rect.height++;row.set(key,rect);if(!old)rectangles.push(rect);
                }
                start=x;level=next;
            }
            previous=row;
        }
        this.rasterKey=rasterKey;this.rectangles=rectangles;this.draw();
    }
    draw() {
        // Coarse 16-32px navmesh cell rectangles create blocky staircase artifacts
        // over floor-plan textures. Exploration filtering is applied to markers instead.
        this.hide();
    }
    hide(){for(const cell of this.cells)this.view.visible(cell,false);this.drawKey=undefined;}
    clear(detach=true) {
        if(this.timer!==undefined)clearTimeout(this.timer);
        this.timer=undefined;if(detach)this.hide();
        this.samples.clear();this.visible=[];this.pending=[];
        this.rectangles=this.rasterKey=this.drawKey=undefined;
        this.key=this.atlas=this.coverage=undefined;this.reported=false;
    }
    dispose(detach=true){this.clear(detach);this.cells=[];this.view=this.parent=undefined;}
}
module.exports={FogOverlay};
