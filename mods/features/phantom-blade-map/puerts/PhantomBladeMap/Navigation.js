"use strict";
const {aliases}=require("./OfflineAtlas");
const {pending}=require("./Conditions");
const NavigationQuery=require("./NavigationQuery");
const world=id=>aliases[String(id || "").toLowerCase()] || String(id || "").toLowerCase();
const distance=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z);

function nearest(points,player,from=0) {
    let best;
    for(let i=from;i<points.length-1;i++) {
        const a=points[i],b=points[i+1],dx=b.x-a.x,dy=b.y-a.y,dz=b.z-a.z,length=dx*dx+dy*dy+dz*dz;
        const t=length?Math.max(0,Math.min(1,((player.x-a.x)*dx+(player.y-a.y)*dy+(player.z-a.z)*dz)/length)):0;
        const point={x:a.x+t*dx,y:a.y+t*dy,z:a.z+t*dz};
        const gap=distance(player,point);
        if(!best || gap<best.gap)best={index:i,t,point,gap};
    }
    return best;
}

function connections(points,atlas) {
    const result=[];
    for(const link of atlas.floorLinks || [])for(const [from,to] of [[link.a,link.b],[link.b,link.a]]) {
        const a={x:from[0],y:from[1],z:from[2]},b={x:to[0],y:to[1],z:to[2]};
        const start=nearest(points,a),end=nearest(points,b);
        if(!start || !end || start.gap>180 || end.gap>180)continue;
        const at=start.index+start.t,after=end.index+end.t;
        if(after<=at+.001)continue;
        const floor=atlas.layers[to[3]];
        result.push({...a,layer:from[3],toLayer:to[3],toKind:floor?.kind,toDepth:floor?.depth || 0,at,after});
    }
    return result.sort((a,b)=>a.at-b.at);
}

class Navigation {
    constructor(){this.clear();}
    clear(){this.key="";this.nextQuery=0;this.result=undefined;this.origin=undefined;this.cursor=0;}
    update(context,player,target,atlas,locks,UE,now=Date.now(),fineNavigation=false) {
        if(!player || !target || ![target.x,target.y,target.z].every(Number.isFinite) || target.state==="collected" ||
            target.facility || target.nativeLock || target.pointKind==="mapMarker" || target.availability && !pending(target)) {
            this.clear();return;
        }
        if(!fineNavigation) {
            this.clear();
            this.result={targetID:target.id,state:world(player.mapID)===world(target.mapID)?"routeCoarse":"routeOtherArea",points:[],remaining:[]};
            return this.result;
        }
        const lockKey=locks.filter(p=>p.state!=="collected").map(p=>p.id).sort().join("|");
        const key=[player.mapID,target.id,target.x,target.y,target.z,lockKey].join(":");
        if(key!==this.key){const next=this.nextQuery;this.clear();this.nextQuery=next;this.key=key;this.result={targetID:target.id,state:"routeLoading",points:[]};}
        if(world(player.mapID)!==world(target.mapID)){this.result.state="routeOtherArea";return this.result;}
        if(player.aboard){this.result={targetID:target.id,state:"routeUnavailable",points:[]};return this.result;}
        const feet={...player,z:player.groundZ ?? player.z};
        const hit=nearest(this.result.points,feet,this.cursor);
        const stale=!this.origin || distance(feet,this.origin)>600 || hit?.gap>350;
        if(hit?.gap>350){this.result.points=[];this.result.state="routeLoading";}
        if(!player.aboard && now>=this.nextQuery && (stale || this.result.state==="routeLoading" || now>=this.expires)) {
            try {
                this.result={targetID:target.id,...NavigationQuery.read(context,player,target,locks,UE,now)};
            } catch(error) {
                this.result={targetID:target.id,state:"routeUnavailable",points:[]};
                if(!this.reported){this.reported=true;require("./Diagnostics").record("navigation-query-failed",{error:String(error)});}
            }
            this.origin={...feet};this.cursor=0;
            this.nextQuery=Date.now()+(this.result.state==="routeLoading"?80:1500);
            this.expires=now+(this.result.state==="routeReady"?5000:4000);
        }
        const result=this.result,position=nearest(result.points,feet,this.cursor);
        result.remaining=[];result.distance=0;result.next=undefined;result.transition=undefined;
        if(position && position.gap<=350) {
            this.cursor=position.index;
            result.remaining=[{...position.point,mapID:player.mapID},...result.points.slice(position.index+1)];
            for(let i=1;i<result.remaining.length;i++)result.distance+=distance(result.remaining[i-1],result.remaining[i]);
            // Aim at the next corner, never through a wall toward the final item.
            result.next=result.remaining.find((p,i)=>i>0 && distance(feet,p)>100) || result.remaining[result.remaining.length-1];
            let previous;
            for(const p of result.remaining) {
                p.layer=atlas?.floor(p) ?? -1;
                if(previous && previous.layer>=0 && p.layer>=0 && previous.layer!==p.layer && !result.transition)
                    result.transition={...previous,toLayer:p.layer};
                previous=p;
            }
            if(atlas && world(player.mapID)===atlas.world) {
                result.connections ||= connections(result.points,atlas);
                const progress=position.index+position.t;
                const entrance=result.connections.find(p=>p.at>=progress && p.layer===atlas.floor(feet));
                if(entrance) {
                    result.transition=entrance;
                    if(Math.floor(entrance.at)===position.index && distance(feet,entrance)>100)result.next=entrance;
                }
            }
        }
        return result;
    }
}
function describe(route,text) {
    if(!route)return "";
    const length=route.remaining?.length>1?text.routeDistance+" "+Math.round(route.distance/100)+" m":"";
    const floor=route.transition?.toKind?text[route.transition.toKind]+(route.transition.toDepth?" "+route.transition.toDepth:""):"";
    return [text[route.state],length,floor?text.routeNextFloor.replace("{0}",floor):route.transition?text.nextEntrance:""].filter(Boolean).join(" · ");
}
module.exports={Navigation,describe};
