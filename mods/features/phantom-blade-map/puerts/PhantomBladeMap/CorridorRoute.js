"use strict";
const Graph=require("./CorridorGraph"),Funnel=require("./CorridorFunnel");
const distance=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z);
function start(atlas,player,target,visible) {
    const graph=Graph.open(atlas.world),result={state:"routeLoading",points:[],transitions:[],distance:0,targetID:target.id,source:target.mapID,geometry:"portal-funnel",dynamicObstacles:false};
    let phase="load",closed=false,source,goal,trace,path=[],cursor=0,puller,corner=1,sourcePoint,goalPoint;
    let partial=false,approachDistance=Infinity,approachPoint;
    let points=[],mapPoints=[],transitions=[],meters=0;
    const heap=[],costs=new Map(),previous=new Map(),settled=new Set(),portals=[];
    const point=id=>({...graph.point(id),mapID:atlas.mapID});
    function push(id,score){let i=heap.length;heap.push([id,score]);while(i){const p=(i-1)>>1;if(heap[p][1]<=score)break;heap[i]=heap[p];i=p;}heap[i]=[id,score];}
    function pop(){const first=heap[0],last=heap.pop();if(heap.length){let i=0;while(i*2+1<heap.length){let c=i*2+1;if(c+1<heap.length&&heap[c+1][1]<heap[c][1])c++;if(heap[c][1]>=last[1])break;heap[i]=heap[c];i=c;}heap[i]=last;}return first[0];}
    function finish(state) {
        result.state=state;
        if(state==="routeReady" || state==="routePartial" || state==="routeUnexplored") {
            result.points=points;result.mapPoints=mapPoints;result.transitions=transitions;result.distance=meters;
            result.endpoint=points[points.length-1];result.approach=result.endpoint?distance(result.endpoint,target):undefined;
        }
        phase="done";heap.length=0;costs.clear();previous.clear();settled.clear();path=[];portals.length=0;puller=undefined;
    }
    function append(p) {
        const a=points[points.length-1];if(!visible(p))return false;
        if(a) {
            const length=distance(a,p);
            for(let d=200;d<length;d+=200) {
                const t=d/length;
                if(!visible({x:a.x+(p.x-a.x)*t,y:a.y+(p.y-a.y)*t,z:a.z+(p.z-a.z)*t,mapID:p.mapID}))return false;
            }
            meters+=length;
        }
        points.push(p);
        // Keep height samples for guidance; draw each straight floor-plan
        // segment once instead of spending a widget on every polygon edge.
        const b=mapPoints[mapPoints.length-1],c=mapPoints[mapPoints.length-2];
        if(c && c.floor===b.floor && b.floor===p.floor) {
            const dx=p.x-c.x,dy=p.y-c.y,length=dx*dx+dy*dy;
            const t=length?((b.x-c.x)*dx+(b.y-c.y)*dy)/length:0;
            const deviation=length?Math.abs(dx*(b.y-c.y)-dy*(b.x-c.x))/Math.sqrt(length):Math.hypot(b.x-c.x,b.y-c.y);
            if(t>=0 && t<=1 && deviation<.001)mapPoints.pop();
        }
        mapPoints.push(p);return true;
    }
    function pin(p){const q={...p,exitZ:p.z};return {left:q,right:q,from:p.floor,to:p.floor,rise:0};}
    function emit(p,opening) {
        if(!append({...p,floor:opening.from,mapID:atlas.mapID}))return false;
        if(opening.from!==opening.to) {
            transitions.push({...p,from:opening.from,to:opening.to,dz:opening.rise,mapID:atlas.mapID});
            if(!append({...p,z:p.exitZ,floor:opening.to,mapID:atlas.mapID}))return false;
        }
        return true;
    }
    return {result,step(){
        if(closed || phase==="done")return true;
        if(graph.navigationRevision!==atlas.navigationRevision){finish("routeDataMismatch");return true;}
        if(!graph.ready){graph.step();return false;}
        const begin=Date.now();
        for(let batch=0;batch<128 && Date.now()-begin<2;batch++) {
            if(phase==="load") {
                sourcePoint=graph.locate(player);goalPoint=graph.locate(target,300,200);
                if(!sourcePoint || !goalPoint){finish("routeUnavailable");return true;}
                source=sourcePoint.id;goal=goalPoint.id;
                result.sourceAnchor={...sourcePoint,component:graph.component(source)};
                result.targetAnchor={...goalPoint,component:graph.component(goal)};
                if(graph.component(source)!==graph.component(goal)){partial=true;phase="approach";continue;}
                costs.set(source,0);push(source,0);phase="search";
            }
            if(phase==="approach") {
                // Approach only through the player's confirmed component.
                // No straight segment is drawn across the unresolved gap.
                const end=Math.min(graph.components.length,cursor+256),component=graph.component(source);
                for(;cursor<end;cursor++)if(graph.component(cursor)===component) {
                    const p=graph.project(cursor,target),d=distance(p,target);
                    if(d<approachDistance){approachDistance=d;approachPoint=p;}
                }
                if(cursor===graph.components.length) {
                    if(!approachPoint || approachDistance>=distance(sourcePoint,target)-100){finish("routeDisconnected");return true;}
                    goalPoint=approachPoint;goal=goalPoint.id;cursor=0;
                    costs.set(source,0);push(source,0);phase="search";
                }
                continue;
            }
            if(phase==="trace") {
                path.push(trace);
                if(trace===source){path.reverse();portals.push(pin(sourcePoint));cursor=1;phase="portals";}
                else trace=previous.get(trace).id;
                continue;
            }
            if(phase==="portals") {
                if(cursor===path.length){portals.push(pin(goalPoint));puller=Funnel.start(portals);phase="funnel";continue;}
                const id=path[cursor++],prev=previous.get(id),opening=graph.portal(prev.edge,prev.id);
                opening.rise=point(id).z-point(prev.id).z;
                if(opening.from!==opening.to) {
                    const a=opening.left,b=opening.right,p={x:(a.x+b.x)/2,y:(a.y+b.y)/2,z:(a.z+b.z)/2,exitZ:(a.exitZ+b.exitZ)/2};
                    opening.left=opening.right=p;
                }
                portals.push(opening);continue;
            }
            if(phase==="funnel") {
                if(puller.step()) {
                    phase="project";cursor=1;
                    if(!append({...sourcePoint,mapID:atlas.mapID})){finish("routeUnexplored");return true;}
                }
                continue;
            }
            if(phase==="project") {
                if(cursor===portals.length){finish(partial?"routePartial":"routeReady");return true;}
                const corners=puller.corners;
                while(corner<corners.length-1 && corners[corner].portal<cursor)corner++;
                const opening=portals[cursor],p=Funnel.crossing(corners[corner-1],corners[corner],opening);
                if(!p) {
                    // Quantized degenerate portals can prevent string pulling.
                    // The original convex-polygon corridor remains valid.
                    phase="corridor";cursor=0;points=[];mapPoints=[];transitions=[];meters=0;result.geometry="shared-edge-corridor";
                    if(!append({...sourcePoint,mapID:atlas.mapID})){finish("routeUnexplored");return true;}
                    continue;
                }
                if(!emit(p,opening)){finish("routeUnexplored");return true;}
                cursor++;continue;
            }
            if(phase==="corridor") {
                if(cursor===path.length) {
                    if(!append({...goalPoint,mapID:atlas.mapID})){finish("routeUnexplored");return true;}
                    finish(partial?"routePartial":"routeReady");return true;
                }
                const id=path[cursor++];
                if(cursor>1) {
                    const opening=portals[cursor-1],a=opening.left,b=opening.right;
                    if(!emit({x:(a.x+b.x)/2,y:(a.y+b.y)/2,z:(a.z+b.z)/2,exitZ:(a.exitZ+b.exitZ)/2},opening)){finish("routeUnexplored");return true;}
                }
                if(!append(point(id))){finish("routeUnexplored");return true;}
                continue;
            }
            if(!heap.length){finish("routeUnavailable");return true;}
            const id=pop();if(settled.has(id))continue;settled.add(id);
            if(id===goal){trace=goal;phase="trace";continue;}
            for(let e=graph.offsets[id]*5;e<graph.offsets[id+1]*5;e+=5) {
                const next=graph.edges[e],cost=costs.get(id)+graph.edges[e+4];
                if(cost>=(costs.get(next)??Infinity))continue;
                costs.set(next,cost);previous.set(next,{id,edge:e});push(next,cost+distance(point(next),point(goal)));
            }
        }
        return false;
    },dispose(){closed=true;finish("routeUnavailable");result.points=[];result.mapPoints=[];result.transitions=[];points=[];mapPoints=[];transitions=[];}};
}
function guidance(route,player) {
    if(!["routeReady","routePartial"].includes(route?.state) || route.points.length<2)return undefined;
    let nearest=0,best=Infinity;
    for(let i=0;i<route.points.length-1;i++) {
        const a=route.points[i],b=route.points[i+1],dx=b.x-a.x,dy=b.y-a.y,dz=b.z-a.z,length=dx*dx+dy*dy+dz*dz;
        const t=length?Math.max(0,Math.min(1,((player.x-a.x)*dx+(player.y-a.y)*dy+(player.z-a.z)*dz)/length)):0;
        const d=distance(player,{x:a.x+t*dx,y:a.y+t*dy,z:a.z+t*dz});
        if(d<best){best=d;nearest=i;}
    }
    if(best>600)return {offRoute:true};
    for(let i=Math.max(1,nearest+1);i<route.points.length-1;i++) {
        const a=route.points[i-1],b=route.points[i],c=route.points[i+1];
        const u={x:b.x-a.x,y:b.y-a.y},v={x:c.x-b.x,y:c.y-b.y};
        const angle=Math.atan2(u.x*v.y-u.y*v.x,u.x*v.x+u.y*v.y);
        const meters=Math.round(distance(player,b)/100);
        if(a.floor!==b.floor)return {key:c.z>=a.z?"toAbove":"toBelow",meters,point:b};
        if(Math.abs(angle)>.65 && meters>3)return {key:angle>0?"turnRight":"turnLeft",meters,point:b};
    }
    const point=route.points[route.points.length-1];return {key:route.state==="routePartial"?"routeFrontier":"routeEnd",meters:Math.round(distance(player,point)/100),point};
}
module.exports={start,guidance};
