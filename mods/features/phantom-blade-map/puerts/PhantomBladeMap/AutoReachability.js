"use strict";

const NavigationQuery=require("./NavigationQuery");

// Automatic selection only. Keep numeric results, never a live navigation path.
// One candidate query per 750 ms; misses expire after moving or unlocking a route.
class AutoReachability {
    constructor() { this.clear(); }
    clear() {
        this.cache=new Map();this.nextQuery=0;this.pendingID="";this.owner=undefined;this.lockKey=undefined;this.scanned=new Set();
        this.scanOrigin=undefined;this.retryScanAt=0;this.nextRetryAt=0;this.status="autoCollectWaiting";
    }
    bind(context) {
        const owner=context.GetWorld();
        if(this.owner!==owner){this.clear();this.owner=owner;}
    }
    beginScan(player,now=Date.now()) {
        const origin=this.scanOrigin,z=player.groundZ ?? player.z;
        const moved=!origin || String(origin.mapID).toLowerCase()!==String(player.mapID).toLowerCase() ||
            Math.hypot(player.x-origin.x,player.y-origin.y)>500 || Math.abs(z-origin.z)>200;
        if(moved || this.retryScanAt && now>=this.retryScanAt) {
            this.scanned.clear();this.retryScanAt=0;this.status="autoCollectWaiting";
            this.nextRetryAt=now+5000;
            this.scanOrigin={x:player.x,y:player.y,z,mapID:player.mapID};
            if(moved){this.cache.clear();this.pendingID="";}
        }
        return !this.retryScanAt;
    }
    finishScan(now=Date.now()) {
        this.pendingID="";this.status="autoCollectNoReachable";this.retryScanAt=now+10000;
    }
    updateLocks(locks) {
        const key=locks.map(p=>p.id+":"+p.state).sort().join("|");
        if(key!==this.lockKey){this.lockKey=key;this.cache.clear();this.scanned.clear();this.pendingID="";this.retryScanAt=0;this.status="autoCollectWaiting";}
    }
    cached(point,player,now=Date.now()) {
        const entry=this.cache.get(point.id);
        if(!entry || now>=entry.expires || entry.mapID!==player.mapID ||
            Math.hypot(player.x-entry.x,player.y-entry.y)>500 || Math.abs((player.groundZ ?? player.z)-entry.z)>200 ||
            entry.target!==[point.x,point.y,point.z].join(":"))return;
        return entry.reachable;
    }
    check(context,point,player,UE,locks=[],now=Date.now()) {
        const cached=this.cached(point,player,now);
        if(cached!==undefined){this.pendingID="";return cached;}
        if(now<this.nextQuery)return;
        const startZ=player.groundZ ?? player.z;
        let result;
        try {
            result=NavigationQuery.read(context,player,point,locks,UE,now);
        } catch(error) {
            this.pendingID="";this.nextQuery=now+5000;
            this.status="autoCollectNavUnavailable";
            this.scanned.add(point.id);
            if(!this.reportedError) {
                this.reportedError=true;
                require("./Diagnostics").record("auto-reachability-error",{error:String(error)});
            }
            return;
        }
        this.pendingID=result.state==="routeLoading"?point.id:"";
        this.nextQuery=now+(this.pendingID?80:750);
        this.status="autoCollectWaiting";
        if(this.pendingID)return;
        if(["pawn","aboard","origin","collision-unavailable"].includes(result.reason)) {
            this.status="autoCollectNavUnavailable";this.nextQuery=now+2000;
            this.report(result.reason,point,player,now);return;
        }
        const reachable=result.state==="routeReady";
        if(this.cache.size>=128)this.cache.delete(this.cache.keys().next().value);
        this.cache.set(point.id,{reachable,mapID:player.mapID,x:player.x,y:player.y,z:startZ,
            target:[point.x,point.y,point.z].join(":"),expires:now+(reachable?3000:10000)});
        if(!reachable) {
            this.scanned.add(point.id);
            if(this.scanned.size>=8)this.status="autoCollectNoReachable";
            this.report(result.reason || result.state,point,player,now);
        }
        return reachable;
    }
    report(reason,point,player,now) {
        if(now-(this.lastReport || 0)<15000)return;
        this.lastReport=now;
        require("./Diagnostics").record("auto-reachability-wait",{
            reason,id:point.id,mapID:player.mapID,checked:this.scanned.size,
            player:{x:player.x,y:player.y,z:player.groundZ ?? player.z},
            target:{x:point.x,y:point.y,z:point.z}
        });
    }
}
module.exports={AutoReachability};
