"use strict";
const documents=require("./UnlocatedDocuments");
const initialDocuments=require("./InitialDocuments");
const {collected}=require("./Documents");
const {describeItem}=require("./SourceState");

function read(bag,UE,text,located=new Set(),cycle) {
    return documents.concat(initialDocuments).filter(document=>!located.has(document.id)).map(document=> {
        const initial=document.sourceKind==="initial";
        const point={id:"document:"+document.id,reward:document.id,category:"book",collectionKey:"book",
            filterGroup:"documents",filterGroups:["documents"],mapID:"",layer:-1,pointKind:"pending",globalDocument:true,
            availability:initial?"documentInitialGrant":"documentSourceUnknown",documentSource:initial?"initial":undefined,
            documentGroup:document.group,documentPage:document.page,
            label:"",inventory:"Inventories"};
        point.state=collected(point,bag,cycle)?"collected":initial?"unknown":"remaining";
        describeItem(UE,point);
        if(!point.label && document.nameKey)point.label=UE.KismetTextLibrary.Conv_TextToString(
            UE.PBZBlueprintFunctionLibrary.MakeTextByKey(document.nameKey));
        point.label ||= text.book;
        return point;
    });
}
module.exports={read};
