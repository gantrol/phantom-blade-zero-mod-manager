"use strict";
const Assets=require("../PhantomBladeRuntime/Assets");
const Diagnostics=require("./Diagnostics");
// The map and HUD share one queue: at most eight 256px tiles in flight and
// 512 warm entries (32 MiB of BC7 payload). Pages own subscriptions, not assets.
class TexturePool {
    constructor() {
        this.clients=new Set();this.capacity=512;
        this.entries=new Map();this.desired=new Set();this.clock=0;
    }
    get(path,UE,validate=false) {
        const entry=this.entries.get(path),now=Date.now();
        if(entry?.texture && UE && (validate || now-(entry.validatedAt || 0)>=1000)) {
            if(!UE.KismetSystemLibrary.IsValid(entry.texture))entry.texture=undefined;
            entry.validatedAt=now;
        }
        return entry?.texture;
    }
    update() {
        // Interleave viewport priorities so the small HUD cannot wait behind
        // an entire large-map queue. A path has only one entry/request.
        this.desired=new Set();
        const queues=[...this.clients].map(client=>[...client.desired]);
        const length=Math.max(0,...queues.map(queue=>queue.length));
        for(let i=0;i<length;i++)for(const queue of queues)if(queue[i])this.desired.add(queue[i]);
        for(const path of this.desired) {
            let entry=this.entries.get(path);
            if(!entry){entry={path,failures:0,retryAt:0};this.entries.set(path,entry);}
            entry.used=++this.clock;
        }
        if(this.request && this.request.entries.every(e=>!this.desired.has(e.path)))this.cancel();
        this.trim();this.pump();
    }
    trim() {
        if(this.entries.size<=this.capacity)return;
        const loading=new Set(this.request?.entries || []);
        const unused=[...this.entries.values()].filter(e=>!this.desired.has(e.path) && !loading.has(e)).sort((a,b)=>a.used-b.used);
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
        if(this.request)return;
        if(this.timer!==undefined){clearTimeout(this.timer);this.timer=undefined;}
        const owner=[...this.clients].find(client=>client.desired.size && client.view);
        if(!owner)return;
        const now=Date.now(),entries=[];let retryAt=Infinity;
        for(const path of this.desired) {
            const entry=this.entries.get(path);
            if(this.get(path,owner.view.UE))continue;
            if(entry.retryAt>now){retryAt=Math.min(retryAt,entry.retryAt);continue;}
            entries.push(entry);if(entries.length===8)break;
        }
        if(!entries.length){if(Number.isFinite(retryAt))this.schedule(retryAt-now);return;}
        const request={entries,started:now,owner};this.request=request;
        request.timer=setTimeout(()=>{
            if(this.request!==request)return;
            this.cancel();this.failed(entries);this.report(request,0,entries.map(e=>e.path),"timeout");this.pump();
        },5000);
        try {
            request.handle=Assets.load(owner.view.page,entries.map(e=>e.path),loaded=>this.complete(request,loaded));
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
        if(this.request!==request || !request.owner.view)return;
        clearTimeout(request.timer);this.request=undefined;
        const failed=[];let count=0;
        for(let i=0;i<request.entries.length;i++) {
            const entry=request.entries[i],texture=loaded[i];
            if(this.entries.get(entry.path)!==entry)continue;
            if(texture && request.owner.view.UE.KismetSystemLibrary.IsValid(texture)) {
                entry.texture=texture;entry.validatedAt=Date.now();entry.failures=0;entry.retryAt=0;count++;
            } else failed.push(entry);
        }
        this.failed(failed);this.trim();this.report(request,count,failed.map(e=>e.path));
        // Drain on the next task, after the native callback has released its
        // request. Cached native loads may complete synchronously.
        this.schedule(0);
        for(const client of this.clients)if(request.entries.some(entry=>client.desired.has(entry.path))) {
            try{client.ready();}catch(error){console.warn("[PhantomBladeMap] texture update: "+String(error));}
        }
    }
    report(request,loaded,failed,reason) {
        const ready=[...this.desired].filter(path=>this.get(path)).length;
        // A successful streaming batch is routine; synchronous diagnostic I/O
        // must not run at every tile boundary while the player is moving.
        if(!failed.length && (ready<this.desired.size || Date.now()-(this.lastReport || 0)<15000))return;
        this.lastReport=Date.now();
        Diagnostics.record("atlas-texture-load",{requested:request.entries.length,loaded,failed,reason,
            required:this.desired.size,ready,
            cached:this.entries.size,elapsedMs:Date.now()-request.started});
    }
    cancel() {
        const request=this.request;this.request=undefined;
        if(request){clearTimeout(request.timer);Assets.cancel(request.handle);}
    }
}
const pool=new TexturePool();
class TextureCache {
    constructor(view,ready) {
        this.view=view;this.ready=ready;this.desired=new Set();
        pool.clients.add(this);
    }
    get(path) {
        return pool.get(path,this.view?.UE,true);
    }
    get progress() {
        let ready=0,failed=0;
        for(const path of this.desired) {
            if(pool.get(path,this.view?.UE))ready++;
            else if(pool.entries.get(path)?.failures)failed++;
        }
        return {total:this.desired.size,ready,failed};
    }
    update(paths) {
        if(!this.view)return;
        const previous=this.desired.values();
        if(paths.length===this.desired.size && paths.every(path=>path===previous.next().value))return;
        this.desired=new Set(paths);pool.update();
    }
    clear(){this.update([]);}
    dispose() {
        pool.clients.delete(this);
        this.desired.clear();
        // Native async requests must not outlive the page used as their owner.
        if(pool.request?.owner===this)pool.cancel();
        this.view=this.ready=undefined;
        pool.update();
    }
}
module.exports={TextureCache};
