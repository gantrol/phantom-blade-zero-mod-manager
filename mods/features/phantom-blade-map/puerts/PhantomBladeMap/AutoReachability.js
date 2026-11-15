"use strict";

function crossesLock(points,locks,mapID) {
    const blocked=locks.filter(p=>p.state!=="collected" && String(p.mapID).toLowerCase()===String(mapID).toLowerCase());
    for(let i=1;i<points.length;i++) {
        const a=points[i-1],b=points[i],dx=b.x-a.x,dy=b.y-a.y,length=dx*dx+dy*dy;
        for(const p of blocked) {
            const t=length?Math.max(0,Math.min(1,((p.x-a.x)*dx+(p.y-a.y)*dy)/length)):0;
            if(Math.hypot(p.x-a.x-t*dx,p.y-a.y-t*dy)<120 && Math.abs(p.z-a.z-t*(b.z-a.z))<220)return true;
        }
    }
    return false;
}

// Automatic selection only. Keep numeric results, never a live navigation path.
// One candidate query per 750 ms; misses expire after moving or unlocking a route.
class AutoReachability {
    constructor() { this.clear(); }
    clear() { this.cache=new Map();this.nextQuery=0;this.lockKey=undefined;this.scanned=new Set();this.scanMap=undefined; }
    beginScan(player) {
        if(this.scanMap!==player.mapID){this.scanned.clear();this.scanMap=player.mapID;}
    }
    updateLocks(locks) {
        const key=locks.map(p=>p.id+":"+p.state).sort().join("|");
        if(key!==this.lockKey){this.lockKey=key;this.cache.clear();this.scanned.clear();}
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
        if(cached!==undefined)return cached;
        if(now<this.nextQuery || player.aboard)return;
        this.nextQuery=now+750;
        const startZ=player.groundZ ?? player.z;
        let reachable=false,path;
        try {
            const puerts=require("puerts"),nav=UE.NavigationSystemV1;
            const pawn=UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context);
            if(!pawn || !UE.KismetSystemLibrary.IsValid(pawn))return;
            const startRef=puerts.$ref(),endRef=puerts.$ref();
            if(!nav.K2_ProjectPointToNavigation(context,new UE.Vector(player.x,player.y,startZ),startRef,
                undefined,undefined,new UE.Vector(100,100,180)))return;
            const start=puerts.$unref(startRef);
            if(nav.K2_ProjectPointToNavigation(context,new UE.Vector(point.x,point.y,point.z),endRef,
                undefined,undefined,new UE.Vector(150,150,220))) {
                const end=puerts.$unref(endRef);
                // Do not snap a destination onto a different floor or across a gap.
                if(Math.hypot(end.X-point.x,end.Y-point.y)<=150 && Math.abs(end.Z-point.z)<=220) {
                    path=nav.FindPathToLocationSynchronously(context,start,end,pawn,undefined);
                    if(path) {
                        path.EnableRecalculationOnInvalidation(UE.ENavigationOptionFlag.Disable);
                        if(path.IsValid() && !path.IsPartial()) {
                            const points=path.PathPoints,count=points.Num();
                            if(count && count<=128) {
                                const last=points.Get(count-1);
                                reachable=Math.hypot(last.X-end.X,last.Y-end.Y,last.Z-end.Z)<=100;
                                const copied=[];
                                if(reachable)for(let i=0;i<count;i++) {
                                    const p=points.Get(i);copied.push({x:p.X,y:p.Y,z:p.Z});
                                }
                                if(reachable)reachable=!crossesLock(copied,locks,player.mapID);
                                if(reachable) {
                                    const ignored=UE.NewArray(UE.Actor);ignored.Add(pawn);
                                    const helper=require("../Library/TsHelper").default;
                                    const actors=point.save && helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
                                    const source=actors && actors.GetActorByID(point.save,UE.Actor.StaticClass());
                                    if(source && UE.KismetSystemLibrary.IsValid(source))ignored.Add(source);
                                    // A nearby nav projection must not put the approach on the far
                                    // side of a wall. Ignore the collectible's own collision.
                                    reachable=!UE.KismetSystemLibrary.LineTraceSingle(context,
                                        new UE.Vector(end.X,end.Y,end.Z+50),new UE.Vector(point.x,point.y,point.z+50),
                                        UE.ETraceTypeQuery.TraceTypeQuery1,false,ignored,UE.EDrawDebugTrace.None,
                                        puerts.$ref(new UE.HitResult()),true,new UE.LinearColor(),new UE.LinearColor(),0);
                                }
                            }
                        }
                    }
                }
            }
        } catch(error) {
            this.nextQuery=now+5000;
            if(!this.reportedError) {
                this.reportedError=true;
                require("./Diagnostics").record("auto-reachability-error",{error:String(error)});
            }
            return;
        } finally {
            path=undefined;
            // Back off after an expensive native query instead of stacking more work.
            this.nextQuery=Math.max(this.nextQuery,Date.now()+Math.max(750,(Date.now()-now)*100));
        }
        if(this.cache.size>=128)this.cache.delete(this.cache.keys().next().value);
        this.cache.set(point.id,{reachable,mapID:player.mapID,x:player.x,y:player.y,z:startZ,
            target:[point.x,point.y,point.z].join(":"),expires:now+(reachable?3000:10000)});
        if(!reachable)this.scanned.add(point.id);
        return reachable;
    }
}
module.exports={AutoReachability};
