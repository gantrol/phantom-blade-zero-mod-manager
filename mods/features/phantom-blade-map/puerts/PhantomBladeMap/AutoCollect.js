"use strict";
const Filter=require("./CollectibleFilter");
const {located}=require("./QuestObjectives");
function eligible(point,player,config,explored) {
    return !!player && located(point) && point.state==="remaining" && !point.availability &&
        (!point.documentSource || point.documentSource==="pickup") &&
        Filter.collectible(point) && Filter.visible(point,config) &&
        String(point.mapID).toLowerCase()===String(player.mapID).toLowerCase() &&
        (!config.exploredOnly || point.explored || explored(point));
}
function score(point,player) {
    const dx=point.x-player.x,dy=point.y-player.y,dz=point.z-player.z;
    const crossFloor=(player.layer>=0 && point.layer>=0 && point.layer!==player.layer) || Math.abs(dz)>380;
    return Math.hypot(dx,dy,dz*(crossFloor?3.8:1.4))+(crossFloor?3200:0);
}
function nearest(points,player,config,explored) {
    let best,distance=Infinity;
    for(const point of points) {
        if(!eligible(point,player,config,explored))continue;
        const d=score(point,player);
        if(d<distance || d===distance && point.id<(best?.id || "")){best=point;distance=d;}
    }
    return best;
}
module.exports={eligible,nearest};
