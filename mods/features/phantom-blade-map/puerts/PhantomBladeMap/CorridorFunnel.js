"use strict";
const area=(a,b,c)=>(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
const same=(a,b)=>Math.abs(a.x-b.x)<.001 && Math.abs(a.y-b.y)<.001;
// Incremental string pulling inside an ordered polygon corridor. Cross-floor
// openings are supplied as point portals, retaining their required waypoint.
function start(portals) {
    let apex=portals[0].left,left=apex,right=apex,apexIndex=0,leftIndex=0,rightIndex=0,cursor=1,done=false;
    const corners=[{...apex,portal:0}];
    return {corners,step(){
        if(done)return true;
        if(cursor===portals.length) {
            const end=portals[portals.length-1].left;
            corners.push({...end,portal:portals.length-1});done=true;return true;
        }
        const next=portals[cursor];
        if(area(apex,right,next.right)>=0) {
            if(same(apex,right) || area(apex,left,next.right)<0){right=next.right;rightIndex=cursor;}
            else {
                corners.push({...left,portal:leftIndex});apex=left;apexIndex=leftIndex;
                left=right=apex;leftIndex=rightIndex=apexIndex;cursor=apexIndex+1;return false;
            }
        }
        if(area(apex,left,next.left)<=0) {
            if(same(apex,left) || area(apex,right,next.left)>0){left=next.left;leftIndex=cursor;}
            else {
                corners.push({...right,portal:rightIndex});apex=right;apexIndex=rightIndex;
                left=right=apex;leftIndex=rightIndex=apexIndex;cursor=apexIndex+1;return false;
            }
        }
        cursor++;return false;
    }};
}
// Intersect the straightened segment with each original opening. Height comes
// from the actual shared edge, preserving slopes along a straight XY path.
function crossing(a,b,portal) {
    const p=portal.left,q=portal.right,dx=b.x-a.x,dy=b.y-a.y,ex=q.x-p.x,ey=q.y-p.y;
    const denominator=dx*ey-dy*ex,length=ex*ex+ey*ey;
    let u;
    if(length<.000001)u=0;
    else if(Math.abs(denominator)>.000001) {
        const t=((p.x-a.x)*ey-(p.y-a.y)*ex)/denominator;
        u=((p.x-a.x)*dy-(p.y-a.y)*dx)/denominator;
        if(t<-.001 || t>1.001 || u<-.001 || u>1.001)return undefined;
    } else {
        const lineLength=dx*dx+dy*dy;
        const t=lineLength?Math.max(0,Math.min(1,(((p.x+q.x)/2-a.x)*dx+((p.y+q.y)/2-a.y)*dy)/lineLength)):0;
        u=((a.x+t*dx-p.x)*ex+(a.y+t*dy-p.y)*ey)/length;
        const x=p.x+Math.max(0,Math.min(1,u))*ex,y=p.y+Math.max(0,Math.min(1,u))*ey;
        if(Math.hypot(x-a.x-t*dx,y-a.y-t*dy)>1)return undefined;
    }
    u=Math.max(0,Math.min(1,u));
    return {x:p.x+u*ex,y:p.y+u*ey,z:p.z+u*(q.z-p.z),exitZ:p.exitZ+u*(q.exitZ-p.exitZ)};
}
module.exports={start,crossing};
