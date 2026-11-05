"use strict";
const documents=require("./UnlocatedDocuments");
const initialDocuments=require("./InitialDocuments");
const {describeItem}=require("./SourceState");

function read(bag,UE,text,located=new Set(),cycle) {
    return documents.concat(initialDocuments).filter(document=>!located.has(document.id)).map(document=> {
        const owned=bag.Inventories.GetRef(document.id);
        const initial=document.sourceKind==="initial";
        const state=owned && (owned.CurrentAmount>0 || cycle!==undefined && owned.AcquiredCycle===cycle)?"collected":initial?"unknown":"remaining";
        const point={id:"document:"+document.id,reward:document.id,category:"book",collectionKey:"book",
            filterGroup:"documents",filterGroups:["documents"],mapID:"",layer:-1,pointKind:"pending",globalDocument:true,
            state,availability:initial?"documentInitialGrant":"documentSourceUnknown",documentSource:initial?"initial":undefined,
            documentGroup:document.group,documentPage:document.page,
            label:"",inventory:"Inventories"};
        describeItem(UE,point);
        if(!point.label && document.nameKey)point.label=UE.KismetTextLibrary.Conv_TextToString(
            UE.PBZBlueprintFunctionLibrary.MakeTextByKey(document.nameKey));
        point.label ||= text.book;
        return point;
    });
}
module.exports={read};
