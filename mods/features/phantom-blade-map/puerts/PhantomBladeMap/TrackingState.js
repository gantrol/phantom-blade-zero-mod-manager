"use strict";
const {createJournal}=require("./provider/Journal");
const {createEasyCsvIO}=require("./provider/EasyCsvIO");
const {resolveUserRoot}=require("./provider/StorageRoot");
const Diagnostics=require("./Diagnostics");
let journal,record,revision=0;
const fields=["id","category","sourceClass","staticSource","filterGroup","filterGroups","questKey","branch","task","taskPointID","nativeID","native","pointKind","documentSource","objectiveID","mapID","x","y","z","save","reward","inventory","domain","nodes","flow"];
function open() {
    if(record)return;
    const UE=require("ue"),puerts=require("puerts");
    journal=createJournal(createEasyCsvIO(UE,puerts,resolveUserRoot(UE),"PhantomBladeMapTracking",path=>UE.BlueprintPathsLibrary.FileExists(path)),"PhantomBladeMapTracking");
    const loaded=journal.load();record=loaded.record?.values || {targets:{}};revision=loaded.record?.revision || 0;
    if(loaded.blocked){journal=undefined;Diagnostics.record("tracking-storage-blocked");}
}
function read(profile){if(!profile)return;open();return record.targets[profile];}
function write(profile,point) {
    if(!profile)return;
    open();const value=point?Object.fromEntries(fields.filter(k=>point[k]!==undefined).map(k=>[k,point[k]])):undefined;
    if(JSON.stringify(record.targets[profile])===JSON.stringify(value))return;
    if(value)record.targets[profile]=value;else delete record.targets[profile];
    if(journal)try{journal.save({schemaVersion:1,revision:revision+1,values:record});revision++;}
    catch(error){Diagnostics.record("tracking-storage-error",{error:String(error)});}
}
module.exports={read,write};
