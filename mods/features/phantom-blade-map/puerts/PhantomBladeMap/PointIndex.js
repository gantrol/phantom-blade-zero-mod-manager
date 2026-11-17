"use strict";
const {aliases}=require("./OfflineAtlas");
const Requirements=require("./CollectionRequirements");
const world=id=>aliases[String(id || "").toLowerCase()] || String(id || "").toLowerCase();
const cellSize=6400;
const requiredItems=p=>(Requirements.flows[p.flow || Requirements.rewards[p.reward]] || []).flatMap(rule=>rule.items || []);

// Plain metadata only. Spatial queries run before floor lookups, native actor
// reads, widget creation and icon styling.
class PointIndex {
    constructor(points=[]) {
        this.cells=new Map();this.byID=new Map();this.byReward=new Map();this.inventory=[];this.progress=[];
        for(const point of points) {
            this.byID.set(point.id,point);
            const rewards=new Set([point.reward,...(point.rewards || []).map(p=>p.id),
                ...requiredItems(point)].filter(Boolean));
            for(const reward of rewards) {
                let list=this.byReward.get(reward);
                if(!list){list=[];this.byReward.set(reward,list);}list.push(point);
            }
            if(point.inventory || ["key","book","treasure"].includes(point.category))this.inventory.push(point);
            if(point.flow || point.nodes?.length || point.domain || Requirements.rewards[point.reward] ||
                ["key","treasure"].includes(point.category))this.progress.push(point);
            if(![point.x,point.y].every(Number.isFinite))continue;
            const key=[world(point.mapID),Math.floor(point.x/cellSize),Math.floor(point.y/cellSize)].join(":");
            let list=this.cells.get(key);
            if(!list){list=[];this.cells.set(key,list);}list.push(point);
        }
    }
    rect(mapID,x,y,halfWidth,halfHeight) {
        const result=[],prefix=world(mapID),minX=Math.floor((x-halfWidth)/cellSize),maxX=Math.floor((x+halfWidth)/cellSize);
        const minY=Math.floor((y-halfHeight)/cellSize),maxY=Math.floor((y+halfHeight)/cellSize);
        const add=points=>{for(const p of points)if(Math.abs(p.x-x)<=halfWidth && Math.abs(p.y-y)<=halfHeight)result.push(p);};
        if((maxX-minX+1)*(maxY-minY+1)>this.cells.size) {
            for(const [key,points] of this.cells)if(key.startsWith(prefix+":"))add(points);
        } else for(let gx=minX;gx<=maxX;gx++)for(let gy=minY;gy<=maxY;gy++)add(this.cells.get([prefix,gx,gy].join(":")) || []);
        return result;
    }
    near(player,radius) {
        return player?this.rect(player.mapID,player.x,player.y,radius,radius)
            .filter(p=>Math.hypot(p.x-player.x,p.y-player.y)<=radius):[];
    }
    affected(change,player,focusID) {
        const points=new Set();
        for(const id of change.items || [])for(const p of this.byReward.get(id) || []) {
            // A common material ID belongs to many unrelated containers. Their
            // source state is refreshed only inside the acquisition's locality.
            if(p.inventory || ["key","book","treasure","bell"].includes(p.category) || requiredItems(p).includes(id))points.add(p);
        }
        // The grant-complete delegate has no item IDs. A mixed grant can include
        // weapons that did not emit the ordinary inventory-amount notification.
        if(change.inventory)for(const p of this.inventory)points.add(p);
        if(change.reasons?.includes("progress"))for(const p of this.progress)points.add(p);
        for(const origin of change.positions?.length?change.positions:[player])for(const p of this.near(origin,2400))points.add(p);
        const focus=this.byID.get(focusID);if(focus)points.add(focus);
        return [...points];
    }
}
module.exports={PointIndex};
