"use strict";
const Diagnostics = require("./Diagnostics");
const Objectives = require("./QuestObjectives");
const name = value => value == null || String(value).toLowerCase() === "none" ? "" : String(value);
const plainText = (UE, value) => UE.KismetTextLibrary.Conv_TextToString(value).replace(/<[^>]*>/g, "");

function resolve(quest, branch, task, UE, puerts) {
    const mapID=puerts.$ref(""), location=puerts.$ref(new UE.Vector());
    const found=UE.PBZNarrativeComponent.ResolveQuestPoint(quest,branch,task,mapID,location);
    if (!found) return undefined;
    const p=puerts.$unref(location), id=name(puerts.$unref(mapID));
    if (!id || ![p.X,p.Y,p.Z].every(Number.isFinite)) return undefined;
    return {mapID:id,x:p.X,y:p.Y,z:p.Z};
}

function readQuest(quest,events,UE,explored=()=>true) {
    if(!quest || quest.bIsTutorial || quest.QuestCompletion!==UE.EQuestCompletion.QC_Started || quest.IsQuestBlocked())return [];
    const puerts=require("puerts"),points=[];
    const questName=String(quest.GetName()), questKey=String(UE.KismetSystemLibrary.GetPathName(quest.GetClass()));
    const label=plainText(UE,quest.GetQuestName());

    for(const branch of quest.Branches) {
        if(branch.GetBranchState()!==UE.EPBZQuestBranchState.InProcess)continue;
        const branchID=name(branch.ID), stage=plainText(UE,branch.GetFlowLocTargetText());
        const candidates=[];
        for(const task of branch.QuestTasks) {
            const taskID=name(task.TargetPointID);
            if(!taskID || task.IsComplete() || !UE.PBZNarrativeComponent.IsTaskPointVisible(task,events.CacheTags.Tags))continue;
            const p=resolve(quest,branchID,taskID,UE,puerts);
            if(p)candidates.push({...p,task:taskID,taskPointID:taskID,pointKind:"objective"});
        }
        if(name(branch.BranchTargetPointID) && UE.PBZNarrativeComponent.IsBranchPointVisible(branch,events.CacheTags.Tags)) {
            const p=resolve(quest,branchID,"",UE,puerts);
            if(p)candidates.push({...p,task:"Self_"+branchID,taskPointID:"",pointKind:"objective"});
        }
        if(!candidates.length)candidates.push(...Objectives.resolve(questKey,branchID,events));
        if(!candidates.length)candidates.push({task:"pending",pointKind:"pending",mapID:name(branch.BelongToWorld)});
        for(const candidate of candidates) {
            const point={...candidate,id:"sidequest:"+questKey+":"+branchID+":"+candidate.task,
                quest:questName,questKey,branch:branchID,stage,label,category:"quest",state:"remaining",layer:-1,
                nativeID:candidate.pointKind==="objective"?"quest:"+questName+":"+candidate.task:undefined,
                nativeTrack:candidate.pointKind==="objective",knownQuestPoint:true};
            point.explored=Objectives.located(point) && explored(point);points.push(point);
        }
    }
    return points;
}

// Supplemental objectives come from completion flows, never accept-point fallback.
// All handles below are local to step(); queued work consists of quest names.
class SideQuests {
    constructor() { this.names=undefined;this.cursor=0;this.points=[];this.active=new Set();this.ready=false; }
    step(page,UE,helper,explored) {
        if(this.ready)return;
        const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page);
        const events=helper.getWorldSubsystem(page,UE.EventPipelineManager.StaticClass());
        if(!narrative || !events)return;
        if(!this.names) { this.names=Array.from(narrative.QuestList,q=>q?String(q.GetName()):"").filter(Boolean);return; }
        for(let budget=0;budget<2 && this.cursor<this.names.length;budget++,this.cursor++) {
            const quest=Array.from(narrative.QuestList).find(q=>q && String(q.GetName())===this.names[this.cursor]);
            if(!quest || quest.bIsTutorial || quest.QuestCompletion!==UE.EQuestCompletion.QC_Started || quest.IsQuestBlocked())continue;
            this.active.add(String(UE.KismetSystemLibrary.GetPathName(quest.GetClass())));
            this.points.push(...readQuest(quest,events,UE,explored));
        }
        if(this.cursor===this.names.length) {
            this.ready=true;this.names=[];
            Diagnostics.record("sidequests-ready",{active:this.active.size,points:this.points.length,
                objectives:this.points.filter(p=>p.pointKind==="objective").length,
                pending:this.points.filter(p=>p.pointKind==="pending").length,
                requested:this.points.filter(p=>/\/(DaHuang|FangKuai|MoJiaoZhiNv)\./.test(p.questKey)).map(p=>({questKey:p.questKey,branch:p.branch,kind:p.pointKind,mapID:p.mapID}))});
        }
    }
}
module.exports={SideQuests,readQuest};
