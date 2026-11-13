"use strict";
const plain=(UE,value)=>UE.KismetTextLibrary.Conv_TextToString(value).replace(/<[^>]*>/g,"");
function questName(context,UE,check) {
    const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context);
    for(const quest of narrative?.QuestList || []) {
        if(!quest)continue;
        const id=String(UE.KismetSystemLibrary.GetPathName(quest.GetClass())).split(".").pop().replace(/_C$/,"");
        if(id!==check.quest)continue;
        const branch=Array.from(quest.Branches).find(b=>String(b.ID)===check.branch);
        return [plain(UE,quest.GetQuestName()),branch?plain(UE,branch.GetFlowLocTargetText()):""].filter(Boolean).join(" · ");
    }
    return "";
}
function describe(point,text,UE,context) {
    if(!point?.availability)return "";
    const fallback=text[point.availability] || text.collectionConditionUnknown;
    // A live source can supersede the explanatory table. Show details only
    // for the condition that actually blocks this point, never a stale gate.
    if(point.availability!==point.requirement || !point.requirements?.length)return fallback;
    const lines=point.requirements.map(check=>{
        if(check.reason==="collectionConditionUnknown")return text.collectionConditionUnknown;
        if(check.items) {
            const names=check.items.map(id=>require("./SourceState").propName(UE,id)).filter(Boolean);
            if(names.length)return text.conditionItems.replace("{0}",names.join(" / "));
        }
        if(check.quest) {
            const name=questName(context,UE,check);
            if(name)return text.conditionQuest.replace("{0}",name);
        }
        return text[check.reason] || fallback;
    });
    return [...new Set(lines)].join("\n");
}
const pending=point=>["collectionConditionUnknown","collectionSourceUnloaded","questState_unknown"].includes(point?.availability);
function links(point,chains,text,UE,context) {
    if(!point?.availability && point?.pointKind!=="lockSource")return [];
    const catalog=require("./QuestChainCatalog"),found=new Map();
    const add=(questKey,step,branch,label)=>{
        if(found.has(questKey))return;
        const chain=chains.find(c=>c.questKey===questKey);
        found.set(questKey,{questKey,step,branch,label:chain?.label || label || text.questPrerequisite});
    };
    const owners=reward=>{
        for(const [key,chain] of Object.entries(catalog)) {
            const item=chain.rewards?.find(r=>r.id===reward);
            if(item)add(key,item.step);
        }
    };
    for(const check of point.requirements || []) {
        if(check.quest) {
            const chain=chains.find(c=>c.questKey.split(".").pop().replace(/_C$/,"")===check.quest);
            if(chain)add(chain.questKey,undefined,check.branch);
            else if(UE && context) {
                const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context);
                for(const quest of narrative?.QuestList || []) {
                    if(!quest || quest.bIsHidden || quest.bIsTutorial)continue;
                    const key=String(UE.KismetSystemLibrary.GetPathName(quest.GetClass()));
                    if(key.split(".").pop().replace(/_C$/,"")===check.quest)add(key,undefined,check.branch,plain(UE,quest.GetQuestName()));
                }
            }
        }
        for(const id of check.items || [])owners(id);
        for(const [key,chain] of Object.entries(catalog)) {
            const tag=check.tag || check.absent;if(!tag)continue;
            const step=chain.steps.find(s=>[s.doneTag,s.missedTag,s.unsettledTag].includes(tag));
            const reward=chain.rewards?.find(r=>[r.availableTag,r.excludedTag].includes(tag));
            if(step || reward)add(key,step?.id || reward.step);
        }
    }
    if(!found.size)owners(point.reward);
    return [...found.values()];
}
module.exports={describe,pending,links};
