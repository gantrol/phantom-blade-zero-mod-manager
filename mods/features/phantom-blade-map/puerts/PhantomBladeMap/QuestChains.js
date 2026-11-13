"use strict";
const catalog=require("./QuestChainCatalog");
const {located}=require("./QuestObjectives");
const CollectionState=require("./CollectionState");
const documents=require("./DocumentCatalog");
const plain=(UE,value)=>UE.KismetTextLibrary.Conv_TextToString(value).replace(/<[^>]*>/g,"");
const statusKey=status=>"questState_"+status;

function branchState(branch,UE) {
    if(branch.BranchType===UE.EBranchType.BranchCompleted)return "completed";
    if(branch.BranchType===UE.EBranchType.Failed)return "failed";
    if(branch.GetBranchState()===UE.EPBZQuestBranchState.InProcess)return "active";
    if(branch.BranchType===UE.EBranchType.BranchLocked)return "blocked";
    if(branch.BranchType===UE.EBranchType.BranchActivating)return "blocked";
    return branch.BranchType===UE.EBranchType.None?"future":"unknown";
}

// Only plain values leave this call. A loaded quest is evidence of its state;
// absence from QuestList is never interpreted as an unaccepted/failed quest.
function read(quest,events,UE,bag,cycle) {
    if(!quest || quest.bIsTutorial || quest.bIsHidden)return;
    const questKey=String(UE.KismetSystemLibrary.GetPathName(quest.GetClass()));
    const definition=catalog[questKey],branches=new Map();
    const status=quest.IsQuestComplete()?"completed":quest.QuestCompletion===UE.EQuestCompletion.QC_Failed?"failed":
        quest.QuestCompletion===UE.EQuestCompletion.QC_Started?(quest.IsQuestBlocked()?"blocked":"active"):"unknown";
    if(status==="unknown")return;
    for(const branch of quest.Branches) {
        if(branch.bHidden)continue;
        const id=String(branch.ID);
        branches.set(id,{id,label:plain(UE,branch.GetFlowLocTargetText()),status:branchState(branch,UE),
            type:branch.BranchType,state:branch.GetBranchState(),
            mapID:String(branch.BelongToWorld || "")});
    }
    const tags=definition && events?new Set(Array.from(events.CacheTags.Tags.GameplayTags,t=>String(t.TagName))):new Set();
    const definitions=definition?.steps || Array.from(branches.values(),b=>({id:b.id,branch:b.id}));
    const steps=definitions.map(step=>{
        const branch=branches.get(step.branch);
        const nativeProgress={status:branch?.status || "unknown",type:branch?.type,state:branch?.state};
        let state=nativeProgress.status,reason,derived;
        // A missed encounter must not look completed just because its rumor
        // branch was acknowledged. The departure tag is the successful path.
        if(step.missedTag && tags.has(step.missedTag) && !tags.has(step.doneTag)) {state="missed";reason=step.reason;}
        else if(step.doneTag && tags.has(step.doneTag))derived={satisfied:true,tag:step.doneTag};
        // The fisherman also has a verified direct hand-in path that starts 1
        // without completing S2. It satisfies this guide step, not native S2.
        else if(step.satisfiedByBranch && ["active","completed"].includes(branches.get(step.satisfiedByBranch)?.status))
            derived={satisfied:true,branch:step.satisfiedByBranch};
        else if(step.unsettledTag && tags.has(step.unsettledTag) && state!=="completed")reason="questSettlementPending";
        if(derived?.satisfied)state="completed";
        if(state==="active" && status==="blocked")state="blocked";
        const revealed=["active","completed","failed"].includes(nativeProgress.status) || !!derived?.satisfied ||
            state==="missed" || reason==="questSettlementPending" || branch?.type===UE.EBranchType.BranchActivating;
        return {...step,status:state,nativeProgress,derived,revealed,reason,label:branch?.label || "",mapID:branch?.mapID || "",known:!!definition};
    });
    // Requirements explain inactive steps only. Neither presentation order nor
    // a failed alternative can override an active native branch.
    for(const step of steps) {
        if(!["future","blocked"].includes(step.status) || step.reason)continue;
        const previous=(step.requiresAll || []).map(id=>steps.find(s=>s.id===id));
        step.missing=previous.filter(s=>s && s.status!=="completed").map(s=>s.id);
        step.reason=step.missing.length?"questPrerequisite":"questAwaitStage";
    }
    const rewards=(definition?.rewards || []).map(reward=>{
        const inventory=bag?.[reward.inventory],record=inventory?.GetRef(reward.id);
        const known=!!inventory && Number.isInteger(cycle);
        const point={...reward,reward:reward.id,nodes:documents.find(d=>d.id===reward.id)?.nodes};
        const acquired=known && CollectionState.collected(point,{bag,cycle,events,tags});
        return {...reward,label:require("./SourceState").propName(UE,reward.id),
            amount:record?.CurrentAmount,acquired:known?acquired:undefined,
            status:acquired?"claimed":!known?"unknown":reward.excludedTag && tags.has(reward.excludedTag)?"missed":"unclaimed",
            available:!reward.availableTag || !!events && tags.has(reward.availableTag)};
    });
    return {questKey,label:plain(UE,quest.GetQuestName()),status,
        nativeProgress:{status,completion:quest.QuestCompletion},steps,rewards};
}

function obstacle(questKey,branch,events) {
    const steps=catalog[questKey]?.steps;
    if(!steps?.some(s=>s.missedTag || s.unsettledTag))return;
    const tags=new Set(Array.from(events.CacheTags.Tags.GameplayTags,t=>String(t.TagName)));
    const missed=step=>step.missedTag && tags.has(step.missedTag) && !tags.has(step.doneTag);
    for(const step of steps.filter(s=>s.branch===branch)) {
        if(missed(step))return step.reason;
        if(step.unsettledTag && tags.has(step.unsettledTag))return "questSettlementPending";
    }
}

function next(points,previous) {
    const candidates=points.filter(p=>p.category==="quest" && p.questKey===previous.questKey);
    const same=candidates.find(p=>p.id===previous.id || previous.nativeID && p.nativeID===previous.nativeID);
    if(same)return same;
    const branch=candidates.filter(p=>p.branch===previous.branch);
    if(branch.length===1)return branch[0];
    // Multiple simultaneous branches require a deliberate choice. Never pick
    // the first array member and accidentally switch a mutually exclusive path.
    if(candidates.length===1)return candidates[0];
}
function pending(point,choice=false,reason) {
    return {...point,x:undefined,y:undefined,z:undefined,nativeID:undefined,nativeTrack:false,
        pointKind:"pending",availability:reason || (choice?"questChooseStep":"questAwaitStage")};
}
function title(step,text) {return (step.objective?text["objective_"+step.objective] || step.label:step.label) || text.questSteps;}
function stepPoints(chain,step,points) {
    if(step.status!=="active")return [];
    const same=points.filter(p=>p.questKey===chain.questKey && p.branch===step.branch && p.pointKind!=="pending");
    if(step.objective) {
        const exact=same.filter(p=>p.objectiveID===step.objective);
        if(exact.length)return exact;
        if(chain.steps.filter(s=>s.branch===step.branch).length>1)return [];
    }
    return same;
}

const guidanceModes=["explore","hints","guide"];
const guidance=value=>guidanceModes.includes(value)?value:"hints";
// Exploration mode adds no destination or future-step information to the game.
function visiblePoints(points,mode) {return guidance(mode)==="explore"?[]:points;}
function entries(chains,points,text,{key="",mode="hints",remainingOnly=true,tracked,collections=[],expanded=false,focus,choices}={}) {
    mode=guidance(mode);
    const current=chains.find(c=>c.questKey===key);
    const state=step=>text[statusKey(step.status)] || text.unknown;
    if(key && !current)return {current:{questKey:key,label:focus?.label || text.questPrerequisite},rows:[{
        id:"unloaded:"+key,category:"quest",title:text.questState_unknown,detail:text.questNotLoaded,canTrack:false
    }]};
    if(current) {
        const rows=[],completed=current.steps.filter(s=>s.status==="completed");
        if(completed.length && mode!=="explore")rows.push({id:"completed:"+key,category:"quest",toggleCompleted:true,
            title:text.questCompletedCount.replace("{0}",completed.length),detail:text[expanded?"questCollapse":"questExpand"],canTrack:false});
        for(const step of current.steps) {
            const focused=focus?.step===step.id || focus?.branch===step.branch;
            if(mode!=="explore" && step.status==="completed" && !expanded && !focused)continue;
            if(mode==="explore" && step.status!=="completed")continue;
            if(mode==="hints" && !step.revealed)continue;
            const matches=stepPoints(current,step,points),targets=matches.length?matches:[undefined];
            const baseTitle=title(step,text),stepTitle=baseTitle;
            for(const point of targets) {
                const missing=(step.missing || []).map(id=>current.steps.find(s=>s.id===id)).filter(Boolean);
                const requirement=missing.length?text.questPrerequisite+" · "+missing.map(s=>title(s,text)).join(" / "):"";
                const rawReason=step.reason==="questPrerequisite"?requirement:text[step.reason] || requirement;
                const reason=step.status==="missed" && rawReason?"⚠ "+rawReason:rawReason;
                const detail=[step.derived?.satisfied && step.nativeProgress.status!=="completed"?text.questStepSatisfied:state(step),step.parallel?text.questParallel:"",reason,
                    point?(text["region_"+String(point.mapID).toLowerCase()] || point.mapID):
                        step.status==="active"?text.objectivePending:""].filter(Boolean).join(" · ");
                rows.push({id:point?.id || "step:"+key+":"+step.id,point,step,focused,category:"quest",state:"remaining",
                    label:baseTitle,title:stepTitle,detail,tooltip:[stepTitle,detail,step.label].filter(Boolean).join("\n"),
                    canTrack:!!point && located(point),tracked:!!point && point.id===tracked?.id});
            }
        }
        if(current.status!=="completed" && (mode==="explore" || mode==="hints" && !rows.some(r=>r.step && r.step.status!=="completed"))) {
            const label=mode==="explore"?text.questExploreProgress:text.questSteps;
            rows.push({id:"progress:"+key,category:"quest",state:"remaining",title:label,label,
                detail:mode==="hints" && current.status==="active"?text.questAwaitStage:state(current),canTrack:false});
        }
        for(const reward of current.rewards) {
            const step=current.steps.find(s=>s.id===reward.step);
            if(mode==="explore" && reward.status!=="claimed")continue;
            if(mode==="hints" && reward.status!=="claimed" && !(reward.availableTag && reward.available) &&
                step?.status!=="completed" && step?.reason!=="questSettlementPending")continue;
            const sources=collections.filter(p=>p.reward===reward.id && !p.globalDocument && located(p) && p.state!=="collected");
            const source=sources.find(p=>!p.availability) || sources[0];
            // Known locations remain inspectable while a gate is unmet. The
            // track action and route still require the reward's own conditions.
            const point=reward.status==="unclaimed" && source?
                {...source,availability:source.availability || (!reward.available?"questPrerequisite":undefined)}:undefined;
            rows.push({id:point?.id || "reward:"+key+":"+reward.id,point,reward,category:reward.category || "quest",state:"remaining",label:reward.label || text.questReward,
                title:reward.label || text.questReward,detail:[text.questReward,state(reward),reward.optional?text.questOptional:"",
                    reward.status==="unclaimed"?(!reward.available?text.questPrerequisite:text[source?.availability] || (!point?text.objectivePending:"")):""].filter(Boolean).join(" · "),
                canTrack:!!point && !point.availability,tracked:!!point && point.id===tracked?.id});
        }
        const active=current.steps.filter(s=>s.status==="active");
        const overview=[state(current),text.questCompletedCount.replace("{0}",completed.length),
            mode!=="explore" && active.length>1?text.questParallel:""];
        return {rows,current,overview:overview.filter(Boolean).join(" · ")};
    }
    const pendingRewards=chain=>chain.rewards.some(r=>["unclaimed","unknown"].includes(r.status));
    const rows=chains.filter(c=>choices?choices.some(link=>link.questKey===c.questKey):!remainingOnly || c.status!=="completed" || mode!=="explore" && pendingRewards(c)).map(chain=>{
        const active=mode==="explore"?[]:chain.steps.flatMap(s=>stepPoints(chain,s,points));
        const point=tracked?.questKey===chain.questKey?next(active,tracked):active.length===1?active[0]:undefined;
        const visibleSteps=mode==="guide"?chain.steps:chain.steps.filter(s=>s.revealed);
        const stage=visibleSteps.find(s=>s.status==="active") || visibleSteps.find(s=>["blocked","unknown","missed","failed"].includes(s.status));
        const progress=chain.steps.filter(s=>s.status==="completed").length;
        const rawReason=mode!=="explore"?text[stage?.reason]:"";
        const stageReason=stage?.status==="missed" && rawReason?"⚠ "+rawReason:rawReason;
        const detail=[state(chain),mode==="guide" && chain.steps.length>1?progress+"/"+chain.steps.length:"",mode!=="explore" && stage?title(stage,text):"",stageReason,
            mode!=="explore" && chain.status==="completed" && pendingRewards(chain)?text.questRewardsPending:"",
            active.length>1?text.questChooseStep:""].filter(Boolean).join(" · ");
        return {id:"chain:"+chain.questKey,chainKey:chain.questKey,point,category:"quest",state:"remaining",label:chain.label,
            title:chain.label,detail,tooltip:chain.label+"\n"+detail,
            canTrack:tracked?.questKey===chain.questKey || !!point && located(point),tracked:tracked?.questKey===chain.questKey};
    });
    for(const link of choices || [])if(!rows.some(row=>row.chainKey===link.questKey))rows.push({
        id:"chain:"+link.questKey,chainKey:link.questKey,category:"quest",title:link.label,detail:text.questNotLoaded,canTrack:false});
    rows.sort((a,b)=>Number(b.tracked)-Number(a.tracked) || a.title.localeCompare(b.title));
    return {rows};
}
module.exports={read,next,pending,entries,stepPoints,obstacle,guidanceModes,guidance,visiblePoints};
