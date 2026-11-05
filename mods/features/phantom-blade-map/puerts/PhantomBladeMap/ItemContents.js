"use strict";
const LootRewards = require("./LootRewards");
const LootItems = require("./LootItems");
const {describeItem,priority,classifyGroup} = require("./SourceState");
const Treasures = require("./Treasures");

function contents(loot, direct, declared=[]) {
    const rewards=(LootRewards[loot] || []).map(r=>({...r}));
    for(const item of LootRewards[loot] ? [] : declared) {
        const index=rewards.findIndex(r=>r.kind===item.kind && r.id===item.id);
        if(index>=0)rewards[index]={...item};else rewards.push({...item});
    }
    if(direct && direct!=="None" && !rewards.some(r=>r.kind==="item" && r.id===direct))
        rewards.push({kind:"item",id:direct,min:1,max:1});
    const preferred=LootItems[loot]?.[0];
    return rewards.sort((a,b)=>(b.id===preferred)-(a.id===preferred));
}
function describe(UE,bag,point,text) {
    text ||= require("./Locales").dictionary(UE);
    const rows=(point.rewards?.length ? point.rewards : point.reward ? [{kind:"item",id:point.reward}] : []);
    const resolved=rows.map(reward=> {
        const item={reward:reward.id,category:reward.kind==="item"?"supply":reward.kind,label:""};
        if(reward.kind==="dust") {item.label=text.dust;item.collectionKey="dust";}
        else if(reward.kind==="item") describeItem(UE,item);
        else {
            let data;
            if(reward.kind==="weapon")data=bag.GetMainWeaponTableData(reward.id)?.WeaponName;
            else if(reward.kind==="shadowWeapon")data=bag.GetShaqiWeaponTableData(reward.id)?.WeaponName;
            else if(reward.kind==="accessory")data=UE.PBZBackPackSubsystem.GetAccessoryData(reward.id)?.AccessoryName;
            else if(reward.kind==="medicine")data=UE.PBZBackPackSubsystem.GetMedicineData(reward.id)?.MedicineName;
            if(data?.Key && String(data.Key)!=="None")
                item.label=UE.KismetTextLibrary.Conv_TextToString(UE.PBZBlueprintFunctionLibrary.MakeTextByKey(String(data.Key)));
        }
        Treasures.classify(item);
        return classifyGroup({...reward,...item});
    }).sort((a,b)=>priority(a)-priority(b));
    if(!resolved.length)return;
    const primary=resolved[0];
    point.reward=primary.id;point.category=primary.category;
    point.collectionKey=primary.collectionKey;
    point.filterGroup=primary.filterGroup;
    point.filterGroups=Array.from(new Set(resolved.map(r=>r.filterGroup).filter(Boolean)));
    point.documentGroup=primary.documentGroup;point.documentPage=primary.documentPage;
    point.label=primary.label || point.label;
    point.contents=resolved.filter(r=>r.label).map(r=>r.label+(r.min===r.max && r.min>1?" ×"+r.min+(r.amountBasis==="base"?" ("+text.baseAmount+")":""):""));
}
function pickupMap(actor,sourceClass) {
    const map=sourceClass==="BP_Chest_C"?actor.PickupMaps:actor.PickupMap,result=[];
    if(!map)return result;
    // Materialize primitives before any reflected name lookup.
    for(let i=0,n=map.GetMaxIndex();i<n;i++)if(map.IsValidIndex(i)) {
        const key=map.GetKey(i),id=String(key),amount=Number(map.Get(key));
        if(id && id!=="None" && amount>0)result.push({kind:"item",id,min:amount,max:amount});
    }
    return result;
}
module.exports={contents,describe,pickupMap};
