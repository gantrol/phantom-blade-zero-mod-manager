"use strict";
const {aliases}=require("./OfflineAtlas");
const world=id=>aliases[String(id || "").toLowerCase()] || String(id || "").toLowerCase();
const distance=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z);
const caches=new WeakMap();
const unavailable=reason=>({state:"routeUnavailable",reason,points:[]});

function crossesLock(a,b,locks,mapID) {
    const dx=b.x-a.x,dy=b.y-a.y,dz=b.z-a.z,length=dx*dx+dy*dy+dz*dz;
    return locks.some(p=>{
        if(p.state==="collected" || world(p.mapID)!==world(mapID))return false;
        const t=length?Math.max(0,Math.min(1,((p.x-a.x)*dx+(p.y-a.y)*dy+(p.z-a.z)*dz)/length)):0;
        return Math.hypot(p.x-a.x-t*dx,p.y-a.y-t*dy,p.z-a.z-t*dz)<180;
    });
}

function collision(context,pawn,target,UE) {
    const puerts=require("puerts"),lib=UE.KismetSystemLibrary;
    const capsule=pawn.CapsuleComponent;
    if(!capsule || !lib.IsValid(capsule))return;
    const radius=capsule.GetScaledCapsuleRadius(),half=capsule.GetScaledCapsuleHalfHeight();
    if(!(radius>0 && half>=radius))return;
    const profile=capsule.GetCollisionProfileName();
    const ignored=UE.NewArray(UE.Actor);ignored.Add(pawn);
    if(target.save) {
        const helper=require("../Library/TsHelper").default;
        const actor=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass())?.GetActorByID(target.save,UE.Actor.StaticClass());
        if(actor && lib.IsValid(actor))ignored.Add(actor);
    }
    const movement=pawn.CharacterMovement;
    const step=Math.min(45,Math.max(0,Number(movement?.MaxStepHeight) || 45));
    const slope=Math.min(1,Math.max(.7,Number(movement?.WalkableFloorZ) || .7));
    const vector=p=>new UE.Vector(p.x,p.y,p.z);
    return {
        floor(x,y,z) {
            const hit=puerts.$ref(new UE.HitResult());
            if(!lib.LineTraceSingleByProfile(context,new UE.Vector(x,y,z+step+8),new UE.Vector(x,y,z-step-8),
                profile,false,ignored,UE.EDrawDebugTrace.None,hit,true,new UE.LinearColor(),new UE.LinearColor(),0))return;
            const value=puerts.$unref(hit),p=value.ImpactPoint;
            if(value.bStartPenetrating || !(value.ImpactNormal.Z>=slope) || ![p.X,p.Y,p.Z].every(Number.isFinite))return;
            return {x:p.X,y:p.Y,z:p.Z,mapID:target.mapID};
        },
        blocked(a,b) {
            const raised=p=>({...p,z:p.z+half+8});
            return lib.CapsuleTraceSingleByProfile(context,vector(raised(a)),vector(raised(b)),radius,half,
                profile,false,ignored,UE.EDrawDebugTrace.None,puerts.$ref(new UE.HitResult()),true,
                new UE.LinearColor(),new UE.LinearColor(),0);
        }
    };
}

function approach(points,target,reason) {
    const from=points[points.length-1],gap=Math.hypot(from.x-target.x,from.y-target.y);
    // Only certify a short local approach. A distant route still requires NavMesh.
    if(gap>1500 || Math.abs(from.z-target.z)>220)return {state:"routePartial",reason,points};
    return {state:"routeLoading",reason:"approach",points,
        approachDistance:gap,walk:{from:{...from},previous:{...from},cursor:0,count:Math.max(1,Math.ceil(gap/40))}};
}

function start(context,player,target,locks,UE) {
    const puerts=require("puerts"),nav=UE.NavigationSystemV1;
    const pawn=UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context);
    if(!pawn || !UE.KismetSystemLibrary.IsValid(pawn))return unavailable("pawn");
    const feet={x:player.x,y:player.y,z:player.groundZ ?? player.z,mapID:player.mapID};
    const startRef=puerts.$ref(),endRef=puerts.$ref();
    if(!nav.K2_ProjectPointToNavigation(context,new UE.Vector(feet.x,feet.y,feet.z),startRef,
        undefined,undefined,new UE.Vector(100,100,180)))return approach([feet],target,"origin");
    const begin=puerts.$unref(startRef);
    let projected=nav.K2_ProjectPointToNavigation(context,new UE.Vector(target.x,target.y,target.z),endRef,
        undefined,undefined,new UE.Vector(150,150,220));
    if(!projected)projected=nav.K2_ProjectPointToNavigation(context,new UE.Vector(target.x,target.y,target.z),endRef,
        undefined,undefined,new UE.Vector(1000,1000,220));
    if(!projected)return approach([feet],target,"destination-projection");
    const end=puerts.$unref(endRef);
    if(Math.hypot(end.X-target.x,end.Y-target.y)>1000 || Math.abs(end.Z-target.z)>220)
        return approach([feet],target,"destination-distance");
    const path=nav.FindPathToLocationSynchronously(context,begin,end,pawn,undefined);
    if(!path)return approach([feet],target,"path-missing");
    path.EnableRecalculationOnInvalidation(UE.ENavigationOptionFlag.Disable);
    if(!path.IsValid())return approach([feet],target,"path-invalid");
    const values=path.PathPoints,count=values.Num(),points=[];
    if(!count || count>256)return unavailable("path-size");
    for(let i=0;i<count;i++) {
        const p=values.Get(i);
        if(![p.X,p.Y,p.Z].every(Number.isFinite))return unavailable("path-coordinate");
        points.push({x:p.X,y:p.Y,z:p.Z,mapID:player.mapID});
    }
    for(let i=1;i<points.length;i++)if(crossesLock(points[i-1],points[i],locks,player.mapID))
        return {state:"routeBlocked",reason:"locked",points:points.slice(0,i)};
    if(!path.IsPartial() && distance(points[points.length-1],{x:end.X,y:end.Y,z:end.Z})>100)
        return unavailable("path-endpoint");
    return approach(points,target,path.IsPartial()?"partial":"destination");
}

function step(context,player,target,locks,UE,result) {
    const walk=result.walk;if(!walk)return;
    const pawn=UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context);
    const probe=pawn && UE.KismetSystemLibrary.IsValid(pawn) && collision(context,pawn,target,UE);
    const fail=reason=>{result.state="routePartial";result.reason=reason;result.walk=undefined;};
    if(!probe){fail("collision-unavailable");return;}
    const started=Date.now();
    // At most four ground rays and four character sweeps per slice. Only plain
    // coordinates survive a slice; no hit, actor or navigation wrapper is saved.
    for(let budget=0;budget<4 && (budget===0 || Date.now()-started<2);budget++) {
        const t=(walk.cursor+1)/walk.count;
        const next=probe.floor(walk.from.x+(target.x-walk.from.x)*t,walk.from.y+(target.y-walk.from.y)*t,walk.previous.z);
        if(!next){fail("approach-floor");return;}
        if(crossesLock(walk.previous,next,locks,player.mapID)){result.state="routeBlocked";result.reason="locked";result.walk=undefined;return;}
        if(probe.blocked(walk.previous,next)){fail("approach-collision");return;}
        result.points.push(next);walk.previous=next;walk.cursor++;
        if(walk.cursor===walk.count) {
            if(Math.abs(next.z-target.z)>220){fail("approach-height");return;}
            result.state="routeReady";result.reason="walkable-approach";
            result.walk=undefined;return;
        }
    }
}

function read(context,player,target,locks,UE,now=Date.now()) {
    if(world(player.mapID)!==world(target.mapID))return {state:"routeOtherArea",points:[]};
    if(player.aboard)return unavailable("aboard");
    const owner=context.GetWorld();
    let cache=caches.get(owner);
    if(!cache){cache={entries:new Map(),nextQuery:0,lastReport:0};caches.set(owner,cache);}
    const lockKey=locks.filter(p=>p.state!=="collected").map(p=>[p.id,p.mapID,p.x,p.y,p.z].join(":")).sort().join("|");
    const key=[player.mapID,target.mapID,target.id,target.x,target.y,target.z,lockKey].join(":");
    let entry=cache.entries.get(key);
    const feet={x:player.x,y:player.y,z:player.groundZ ?? player.z};
    if(entry && (now>=entry.expires || distance(feet,entry.origin)>200)) {cache.entries.delete(key);entry=undefined;}
    if(!entry) {
        if(now<cache.nextQuery)return {state:"routeLoading",reason:"budget",points:[]};
        const began=Date.now();
        try {
            entry={origin:feet,result:start(context,player,target,locks,UE),expires:now+15000,nextStep:0,started:began};
            if(!entry.result.walk)entry.expires=now+3000;
            cache.entries.set(key,entry);
            while(cache.entries.size>24)cache.entries.delete(cache.entries.keys().next().value);
        } finally {cache.nextQuery=Date.now()+Math.min(3000,Math.max(750,(Date.now()-began)*20));}
    }
    if(entry.result.walk && now>=entry.nextStep) {
        entry.nextStep=now+40;
        step(context,player,target,locks,UE,entry.result);
        if(!entry.result.walk)entry.expires=now+3000;
    }
    if(!entry.result.walk && !entry.reported && now-cache.lastReport>=5000) {
        entry.reported=true;cache.lastReport=now;
        require("./Diagnostics").record("navigation-result",{id:target.id,mapID:player.mapID,state:entry.result.state,
            reason:entry.result.reason,points:entry.result.points.length,approachCm:entry.result.approachDistance,ms:Date.now()-entry.started});
    }
    const {walk,...result}=entry.result;
    return {...result,points:result.points.map(p=>({...p}))};
}
module.exports={read};
