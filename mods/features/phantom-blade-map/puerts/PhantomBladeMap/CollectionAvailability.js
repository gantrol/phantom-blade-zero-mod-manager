"use strict";
const Requirements=require("./CollectionRequirements");
function rules(point) {return Requirements.flows[point.flow || Requirements.rewards[point.reward]];}
function snapshot(events) {
    const result=new Set(),tags=events.CacheTags.Tags.GameplayTags;
    for(let i=0,n=tags.Num();i<n;i++)result.add(String(tags.GetRef(i).TagName));
    return result;
}
function capture(events,narrative,bag,UE) {
    const tags=snapshot(events),branches=new Map(),items=new Map();
    // Materialize values only. No native quest/branch wrapper survives this call.
    if(narrative)for(const quest of narrative.QuestList) {
        if(!quest)continue;
        const key=String(UE.KismetSystemLibrary.GetPathName(quest.GetClass())).split(".").pop().replace(/_C$/,"");
        if(!Requirements.quests.has(key))continue;
        for(const branch of quest.Branches)branches.set(key+":"+String(branch.ID),branch.GetBranchState());
    }
    if(bag)for(const id of Requirements.items)items.set(id,Number(bag.Inventories.GetRef(id)?.CurrentAmount || 0));
    return {tags,branches,items,questsReady:!!narrative,itemsReady:!!bag};
}
function missing(point,progress) {
    const result=[];
    if(point.state==="collected" || point.landmark || point.globalDocument)return result;
    for(const check of rules(point) || []) {
        if(check.tag && !progress.tags.has(check.tag))result.push({...check});
        if(check.absent && progress.tags.has(check.absent))result.push({...check});
        if(check.quest) {
            const state=progress.branches.get(check.quest+":"+check.branch);
            if(!progress.questsReady || state===undefined)result.push({...check,reason:"collectionConditionUnknown"});
            else if((state===check.state)===check.invert)result.push({...check});
        }
        if(check.items) {
            if(!progress.itemsReady)result.push({reason:"collectionConditionUnknown"});
            else {
                const items=check.items.filter(id=>progress.items.get(id)<1);
                if(items.length)result.push({...check,items});
            }
        }
    }
    return result;
}
function read(point,progress) {return missing(point,progress)[0]?.reason;}
function compose(point) {
    if(!point.globalDocument) {
        const flow=point.flow && !point.landmark;
        const native=flow && (!point.sourceCheckedAt?"collectionConditionUnknown":point.flowAvailability);
        point.availability=point.state==="collected"?undefined:
            (flow?(native && (point.requirement || native)):point.requirement) ||
            (point.sourceUnavailable?"sourceUnavailable":undefined);
    }
    return point.availability;
}
function refresh(point,progress) {
    point.requirements=missing(point,progress);point.requirement=point.requirements[0]?.reason;
    return compose(point);
}
function refreshAll(points,progress) {
    let changed=false;
    for(const point of points) {
        const previous=JSON.stringify([point.availability,point.requirements]);
        refresh(point,progress);
        if(previous!==JSON.stringify([point.availability,point.requirements]))changed=true;
    }
    return changed;
}
function observe(point,actor) {
    point.sourceUnavailable=!!(actor.bHidden || actor["Not Interact"]===true);
    return compose(point);
}
function refreshSource(point,manager,UE) {
    const previous=point.availability;
    if(point.flow && !point.landmark && !point.globalDocument) {
        point.sourceCheckedAt=Date.now();
        try {
            point.flowAvailability=require("./CollectionFlow").read(point,manager,UE);
            point.sourceUnavailable=point.flowAvailability==="sourceUnavailable";
        }
        catch(error) {
            point.flowAvailability="collectionConditionUnknown";
            if(!point.flowErrorReported) {
                point.flowErrorReported=true;
                require("./Diagnostics").record("collection-flow-read-failed",{id:point.id,flow:point.flow,error:String(error)});
            }
        }
        compose(point);
    } else if(point.save && require("./SourceState").source(point.save,point.sourceClass)) {
        const actor=manager.GetActorByID(point.save,UE.Actor.StaticClass());
        if(actor && UE.KismetSystemLibrary.IsValid(actor) && !actor.IsActorBeingDestroyed())observe(point,actor);
    }
    return previous!==point.availability;
}
const navigable=point=>!!point && !point.availability;
module.exports={snapshot,capture,read,refresh,refreshAll,observe,refreshSource,navigable};
