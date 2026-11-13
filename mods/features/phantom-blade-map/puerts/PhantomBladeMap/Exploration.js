"use strict";
const Fog=require("./Fog");
// Fog is the game's exploration state. Projection samples are numeric and
// never retain a map page or UObject.
class Exploration {
    constructor(records=[]) {
        this.records=records;this.visits=[];
        this.recordIndex=new Map(records.filter(r=>r?.mapID).map(r=>[String(r.mapID).toLowerCase(),r]));
        this.projected=new Map();
    }
    visit() {}
    record(p) {
        if(!p?.mapID)return undefined;
        return this.recordIndex.get(String(p.mapID).toLowerCase());
    }
    refresh(records) {
        this.records=records;
        this.recordIndex=new Map(records.filter(r=>r?.mapID).map(r=>[String(r.mapID).toLowerCase(),r]));
    }
    clearProjection(){this.projected.clear();}
    remember(p,uv) {
        if(!p?.mapID)return uv;
        const key=[String(p.mapID).toLowerCase(),p.x,p.y,p.z].join(":");
        if(this.projected.size>=8192)this.projected.delete(this.projected.keys().next().value);
        this.projected.set(key,uv);return uv;
    }
    sample(context,UE,draw) {
        const map=require("../Library/TsHelper").default.getWorldSubsystem(context,UE.MapSubsystem.StaticClass());
        const previousReader=this.reader;let budget=128;
        this.reader=p=>{
            if(!map || budget<=0 || !p?.mapID)return;
            budget--;
            const uv=map.ProjectWorldLocationToMap(new UE.Vector(p.x,p.y,p.z),p.mapID);
            return [uv.X,uv.Y];
        };
        try{return draw();}finally{this.reader=previousReader;}
    }
    project(p) {
        if(!p?.mapID || ![p.x,p.y,p.z].every(Number.isFinite))return;
        const key=[String(p.mapID).toLowerCase(),p.x,p.y,p.z].join(":");
        let uv=this.projected.get(key);
        if(!uv && this.reader) {
            uv=this.reader(p);
            if(uv && uv.every(Number.isFinite)) {
                this.remember(p,uv);
            } else uv=undefined;
        }
        return uv;
    }
    visibility(p) {
        const r=this.record(p);if(!r?.fog.known)return false;
        // An unprojected sample is pending, not evidence of fog. Route checks
        // resume it in the next projection window instead of truncating here.
        const uv=this.project(p);return uv?Fog.visible(r.fog,...uv):undefined;
    }
    visible(p) {return this.visibility(p)===true;}
    area(corners) {
        const r=this.record(corners[0]);
        return !!r?.fog.known && Fog.visibleArea(r.fog,corners.map(p=>this.project(p)));
    }
    possible(corners) {
        const r=this.record(corners[0]);if(!r?.fog.known)return false;
        const uv=corners.map(p=>this.project(p));if(uv.some(p=>!p))return false;
        const xs=uv.map(p=>p[0]),ys=uv.map(p=>p[1]);
        const l=Math.min(...xs),t=Math.min(...ys),right=Math.max(...xs),b=Math.max(...ys);
        return right>=0 && b>=0 && l<=1 && t<=1 && !r.fog.rectangles.some(([x,y,z,w])=>l>=x && t>=y && right<=z && b<=w);
    }
    snapshot(){return {records:this.records,visits:[]};}
}
module.exports={Exploration};
