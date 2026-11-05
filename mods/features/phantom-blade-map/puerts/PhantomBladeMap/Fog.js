"use strict";
const layout=require("./FogLayout");
// The shipped game uses legacy fog widgets. Their authored rectangles are
// compiled offline; only the save-backed unlock IDs are read at runtime.
// Browsing or closing a map page cannot change this coverage source.
function records(context,UE) {
    const map=require("../Library/TsHelper").default.getWorldSubsystem(context,UE.MapSubsystem.StaticClass());
    if(!map)return [];
    const unlocks=new Map(),regions=new Map();
    for(const region of Object.values(layout.regions))for(const member of region.members) {
        const id=member.toLowerCase();if(unlocks.has(id))continue;
        const values=map.GetUnLockMaskIDs(member,0),ids=[];
        for(let i=0,n=values.Num();i<n;i++)ids.push(String(values.Get(i)).toLowerCase());
        unlocks.set(id,ids);
    }
    for(const [id,region] of Object.entries(layout.regions)) {
        const unlocked=new Set(region.members.flatMap(member=>unlocks.get(member.toLowerCase())));
        const remaining=region.masks.filter(([mask])=>!unlocked.has(mask));
        regions.set(id,{known:true,rectangles:remaining.map(row=>row.slice(1)),revision:remaining.map(row=>row[0]).join("|")});
    }
    return Object.entries(layout.memberships).map(([mapID,region])=>({mapID,fog:regions.get(region)}));
}
function visible(fog,u,v) {
    if(!(u>=-.02 && u<=1.02 && v>=-.02 && v<=1.02) || !fog?.known)return false;
    return !fog.rectangles.some(([left,top,right,bottom])=>u>left && u<right && v>top && v<bottom);
}
function visibleArea(fog,points) {
    if(!fog?.known || points.some(p=>!p || !visible(fog,p[0],p[1])))return false;
    const left=Math.min(...points.map(p=>p[0])),right=Math.max(...points.map(p=>p[0]));
    const top=Math.min(...points.map(p=>p[1])),bottom=Math.max(...points.map(p=>p[1]));
    return !fog.rectangles.some(([l,t,r,b])=>left<r && right>l && top<b && bottom>t);
}
module.exports={records,visible,visibleArea};
