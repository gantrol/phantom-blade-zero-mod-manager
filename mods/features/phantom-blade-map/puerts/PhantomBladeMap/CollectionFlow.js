"use strict";
const routes=require("./CollectionGates");
const guidText=guid=>[guid.A,guid.B,guid.C,guid.D].map(v=>(v>>>0).toString(16).padStart(8,"0")).join("").toUpperCase();
function guidValue(id,UE) {
    const guid=new UE.Guid();
    [guid.A,guid.B,guid.C,guid.D]=[0,8,16,24].map(i=>parseInt(id.slice(i,i+8),16));
    return guid;
}
function identities(point) {
    return (point.nodes || []).map(id=>String(id).match(/^(.*)_([A-Fa-f0-9]{32})$/))
        .filter(Boolean).map(match=>({owner:match[1],guid:match[2].toUpperCase()}));
}
function combine(values,all) {
    if(values.includes(!all))return !all;
    return values.includes(undefined)?undefined:all;
}
function evaluate(rule,flow,actor,UE) {
    const root=flow.GetRootFlowInstance();
    if(!root || !UE.KismetSystemLibrary.IsValid(root))return;
    function members(list) {
        const result=new Set();
        for(let i=0,n=list.Num();i<n;i++) {
            const node=list.Get(i);
            if(node)result.add(guidText(node.NodeGuid));
        }
        return result;
    }
    const active=members(flow.CurrentExecuteNode),choices=members(flow.CurrentChoiceNode);
    const puerts=require("puerts"),nodes=new Map();
    let bag;
    function node(id) {
        if(!nodes.has(id))nodes.set(id,root.GetNode(guidValue(id,UE)));
        return nodes.get(id);
    }
    function read(check) {
        if(typeof check==="boolean")return check;
        if(!check || check.unknown)return;
        if(check.all)return combine(check.all.map(read),true);
        if(check.any)return combine(check.any.map(read),false);
        if(check.execute)return active.has(check.execute);
        if(check.registeredChoice)return choices.has(check.registeredChoice);
        if(check.gate) {
            const gate=node(check.gate);
            if(gate && typeof gate.HasExecute==="boolean")return gate.HasExecute || gate.HasForceUnlocked===true;
        }
        if(check.choice) {
            const choice=node(check.choice);
            if(!choice || !choice.CheckChoiceValid || check.index>=choice.ChoiceData.Num())return;
            // This pure game function includes tag filters and quest states.
            // Get owns the struct copy; no child-container reference is kept
            // across the Blueprint call.
            const data=choice.ChoiceData.Get(check.index),valid=puerts.$ref(false);
            choice.CheckChoiceValid(data,valid);
            return puerts.$unref(valid);
        }
        if(check.items) {
            bag ||= require("../Library/TsHelper").default.getWorldSubsystem(actor,UE.PBZBackPackSubsystem.StaticClass());
            if(!bag)return;
            return check.items.every(([id,count])=>bag.HasEnoughItem(id,count,puerts.$ref(0)));
        }
    }
    return read(rule);
}
function read(point,manager,UE) {
    const rules=routes[point.flow],sources=identities(point),results=[];
    if(!rules || !sources.length)return "collectionConditionUnknown";
    for(const source of sources) {
        const actor=manager.GetActorByID(source.owner,UE.Actor.StaticClass());
        if(!actor || !UE.KismetSystemLibrary.IsValid(actor) || actor.IsActorBeingDestroyed()) {
            results.push("collectionSourceUnloaded");continue;
        }
        if(actor.bHidden || actor["Not Interact"]===true) {results.push("sourceUnavailable");continue;}
        const flow=actor.GetComponentByClass(UE.PBZFlowComponent.StaticClass());
        const available=flow?evaluate(rules[source.guid],flow,actor,UE):undefined;
        if(available===true)return;
        results.push(available===false?"collectionQuestRequired":"collectionConditionUnknown");
    }
    // Multiple reward nodes describe alternative acquisition paths.
    return results.find(value=>value==="collectionConditionUnknown") ||
        results.find(value=>value==="collectionSourceUnloaded") || results[0];
}
module.exports={read};
