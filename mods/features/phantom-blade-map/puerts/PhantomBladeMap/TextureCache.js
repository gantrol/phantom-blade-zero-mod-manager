"use strict";
const Assets=require("../PhantomBladeRuntime/Assets");
const Diagnostics=require("./Diagnostics");
// One owner, eight 256px tiles in flight (at most 2 MiB decoded), and a bounded
// working set. Widget recycling does not discard recently visited textures.
class TextureCache {
    constructor(view,capacity,ready) {
        this.view=view;this.capacity=capacity;this.ready=ready;
        this.entries=new Map();this.desired=new Set();this.clock=0;
    }
    get(path){return this.entries.get(path)?.texture;}
    update(paths) {
        this.desired=new Set(paths);
        for(const path of this.desired) {
            let entry=this.entries.get(path);
            if(!entry){entry={path,failures:0,retryAt:0};this.entries.set(path,entry);}
            entry.used=++this.clock;
        }
        if(this.request && this.request.entries.every(e=>!this.desired.has(e.path)))this.cancel();
        this.trim();this.pump();
    }
    trim() {
        const unused=[...this.entries.values()].filter(e=>!this.desired.has(e.path)).sort((a,b)=>a.used-b.used);
        for(const entry of unused) {
            if(this.entries.size<=this.capacity)break;
            this.entries.delete(entry.path);
        }
    }
    schedule(delay) {
        if(this.timer!==undefined)clearTimeout(this.timer);
        this.timer=setTimeout(()=>{this.timer=undefined;this.pump();},delay);
    }
    pump() {
        if(!this.view || this.request)return;
        if(this.timer!==undefined){clearTimeout(this.timer);this.timer=undefined;}
        const now=Date.now(),entries=[];let retryAt=Infinity;
        for(const path of this.desired) {
            const entry=this.entries.get(path);
            if(entry.texture)continue;
            if(entry.retryAt>now){retryAt=Math.min(retryAt,entry.retryAt);continue;}
            entries.push(entry);if(entries.length===8)break;
        }
        if(!entries.length){if(Number.isFinite(retryAt))this.schedule(retryAt-now);return;}
        const request={entries,started:now};this.request=request;
        request.timer=setTimeout(()=>{
            if(this.request!==request)return;
            this.cancel();this.failed(entries);this.report(request,0,entries.map(e=>e.path),"timeout");this.pump();
        },5000);
        try {
            request.handle=Assets.load(this.view.page,entries.map(e=>e.path),loaded=>this.complete(request,loaded));
        } catch(error) {
            if(this.request!==request)return;
            this.cancel();this.failed(entries);this.report(request,0,entries.map(e=>e.path),String(error));this.schedule(0);
        }
    }
    failed(entries) {
        for(const entry of entries)if(this.entries.get(entry.path)===entry) {
            entry.failures++;
            entry.retryAt=Date.now()+Math.min(30000,500*2**Math.min(entry.failures-1,6));
        }
    }
    complete(request,loaded) {
        // Identity also rejects callbacks from a cleared/reopened map, including
        // an older request for the same path. A cancelled callback owns nothing.
        if(this.request!==request || !this.view)return;
        clearTimeout(request.timer);this.request=undefined;
        const failed=[];let count=0;
        for(let i=0;i<request.entries.length;i++) {
            const entry=request.entries[i],texture=loaded[i];
            if(this.entries.get(entry.path)!==entry)continue;
            if(texture && this.view.UE.KismetSystemLibrary.IsValid(texture)) {
                entry.texture=texture;entry.failures=0;entry.retryAt=0;count++;
            } else failed.push(entry);
        }
        this.failed(failed);this.trim();this.report(request,count,failed.map(e=>e.path));
        // Drain on the next task, after the native callback has released its
        // request. Cached native loads may complete synchronously.
        this.schedule(0);this.ready();
    }
    report(request,loaded,failed,reason) {
        const ready=[...this.desired].filter(path=>this.get(path)).length;
        if(!failed.length && ready<this.desired.size)return;
        Diagnostics.record("atlas-texture-load",{requested:request.entries.length,loaded,failed,reason,
            required:this.desired.size,ready,
            cached:this.entries.size,elapsedMs:Date.now()-request.started});
    }
    cancel() {
        const request=this.request;this.request=undefined;
        if(request){clearTimeout(request.timer);Assets.cancel(request.handle);}
    }
    clear() {
        if(this.timer!==undefined)clearTimeout(this.timer);
        this.timer=undefined;this.cancel();this.desired.clear();this.entries.clear();
    }
    dispose(){this.clear();this.view=this.ready=undefined;}
}
module.exports={TextureCache};
