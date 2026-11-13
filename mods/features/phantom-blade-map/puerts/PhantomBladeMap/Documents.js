"use strict";
const sources=require("./DocumentCatalog");
const {aliases}=require("./OfflineAtlas");
const {describeItem}=require("./SourceState");

function collected(point,bag,cycle,events) {
    const item=bag?.Inventories.GetRef(point.reward);
    // Carried-over pages do not complete this cycle's pickups. Starting pages
    // have no pickup to repeat; handed-in letters keep their acquisition record.
    return !!(item && (cycle!==undefined && item.AcquiredCycle===cycle ||
        point.documentSource==="initial" && item.CurrentAmount>0) ||
        events && point.nodes?.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined));
}

function read(context,mapIDs,UE,text,explored) {
    const allowed=new Set(mapIDs.map(id=>id.toLowerCase()));
    const worlds=new Set([...allowed].map(id=>aliases[id] || id));
    const cycle=context.game.GetCycleCount(),points=[],represented=new Set();
    for(const source of sources) {
        if(represented.has(source.id))continue;
        if(source.mapID?!allowed.has(source.mapID.toLowerCase()):!worlds.has(source.world))continue;
        const [x,y,z]=source.position;
        const mapID=source.mapID || String(context.map.GetMapIDByLevelAndLocation(source.levelName,new UE.Vector(x,y,z)));
        if(!allowed.has(mapID.toLowerCase()))continue;
        represented.add(source.id);
        const point={id:"document:"+source.id,reward:source.id,category:"book",collectionKey:"book",
            filterGroup:"documents",filterGroups:["documents"],mapID,x,y,z,layer:-1,
            pointKind:"documentReward",documentSource:source.sourceKind,documentGroup:source.group,documentPage:source.page,save:source.save || "",
            nodes:source.nodes.slice(),flow:source.flow,inventory:"Inventories",
            stage:[text[{dialogue:"documentDialogueReward",quest:"documentQuestReward",combat:"documentCombatReward"}[source.sourceKind]],text[source.sourceLabelKey]].filter(Boolean).join(" · ")};
        point.state=collected(point,context.bag,cycle,context.events)?"collected":"remaining";
        describeItem(UE,point);
        if(!point.label && source.nameKey)point.label=UE.KismetTextLibrary.Conv_TextToString(UE.PBZBlueprintFunctionLibrary.MakeTextByKey(source.nameKey));
        point.label ||= text.book;
        point.explored=explored(point);points.push(point);
    }
    return points;
}

module.exports={read,collected};
