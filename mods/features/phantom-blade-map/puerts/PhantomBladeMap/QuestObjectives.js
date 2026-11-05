"use strict";
// Objective locations from the installed build's flow assets and placed actors.
// IDs and tags are game data; display names come from the quest's localized text.
const locations={
    goldenDog:{mapID:"HouJie",x:-249692.469696,y:110027.129438,z:-3499.108504},
    cubeEnemy:{mapID:"MeiGuHu",x:-99552.642045,y:1009.733613,z:711.158297},
    cubeChest:{mapID:"MeiGuHu",x:-97988,y:24790,z:1094},
    fisherman:{mapID:"PangZhen",x:-192632,y:119441,z:-3839},
    phantom:{mapID:"PangZhen",x:-189095,y:122958,z:-3901},
    swordswoman:{mapID:"ZhengDaoDaYing",x:21162.776115,y:80620.139200,z:10970.292336},
    swordFormation:{mapID:"ZhengDaoDaYing",x:29975.211635,y:71525.006424,z:6839.068280}
};
const located=point=>!!point && [point.x,point.y,point.z].every(Number.isFinite);
function resolve(questKey,branch,events) {
    const tags=new Set(Array.from(events.CacheTags.Tags.GameplayTags,t=>String(t.TagName)));
    const key=questKey.slice(questKey.lastIndexOf("/")+1).split(".")[0];
    const goal=id=>({...locations[id],objectiveID:id,task:id,taskPointID:"",pointKind:"supplemental"});
    if(key==="DaHuang" && branch==="1")return [goal("goldenDog")];
    if(key==="FangKuai") {
        if(branch==="S1") {
            const points=[];
            if(!tags.has("pz.渔夫.获得方块1"))points.push(goal("cubeEnemy"));
            if(!tags.has("pz.渔夫.获得方块2"))points.push(goal("cubeChest"));
            if(tags.has("pz.渔夫.获得方块1") && tags.has("pz.渔夫.获得方块2"))points.push(goal("fisherman"));
            return points;
        }
        if(branch==="S2")return [goal("fisherman")];
        if(branch==="1")return [goal("phantom")];
    }
    if(key==="MoJiaoZhiNv" && branch==="S1" && !tags.has("zddy.击杀.红衣女剑客") && !tags.has("zddy.红衣女剑客.荡绳离开"))return [goal("swordswoman")];
    if(key==="MoJiaoZhiNv" && branch==="1")return [goal("swordFormation")];
    return [];
}
module.exports={resolve,located};
