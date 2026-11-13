"use strict";
const {groups,traversalVisible}=require("./Filters");
const settings=new Map(groups.map(g=>[g.key,g.setting]));
const categories={bell:"collection",weapon:"collection",shadowWeapon:"collection",accessory:"collection",medicine:"collection",
    challenge:"collection",amber:"collection",blackstone:"collection",flower:"collection",book:"documents",
    soulMaterial:"materials",upgradeMaterial:"materials",dust:"money",treasure:"keyItems",key:"keyItems"};
function group(point){return point.filterGroup || categories[point.category] || "supplies";}
function hasContent(point) {
    // Generic pickup/container actors need a known reward before becoming targets.
    return !["supply","chest"].includes(point.category) || !!point.reward || !!point.rewards?.length;
}
function visible(point,config) {
    if(!hasContent(point))return false;
    if(point.facility)return traversalVisible(point,config);
    if(["npc","treeHeart","marker","quest"].includes(point.category))return config[point.category]!==false;
    const matches=point.filterGroups?.length?point.filterGroups:[group(point)];
    return matches.some(key=>config[settings.get(key)]!==false);
}
function collectible(point) {
    return hasContent(point) && !point.facility && !["quest","npc","treeHeart","marker","bell","challenge"].includes(point.category);
}
module.exports={group,hasContent,visible,collectible};
