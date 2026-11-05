"use strict";
// Compile acquisition paths from ResourceExport JSON. This is an offline data
// build: no game process, save mutation or runtime asset loading is involved.
const fs=require("node:fs/promises"),path=require("node:path");
const unknown={unknown:true};
const signature=value=>JSON.stringify(value);
function combine(kind,values) {
    const flat=values.flatMap(v=>v?.[kind] || [v]);
    const stop=kind==="all"?false:true,identity=!stop;
    if(flat.includes(stop))return stop;
    const unique=[...new Map(flat.filter(v=>v!==identity).map(v=>[signature(v),v])).values()];
    return unique.length===0?identity:unique.length===1?unique[0]:{[kind]:unique};
}
const all=values=>combine("all",values),any=values=>combine("any",values);
// Presentation/action nodes do not add an acquisition predicate. Decisions,
// external callbacks and unsupported node types are handled explicitly below.
const pass=new Set([
    "FlowNode_Start","FlowNode_ExecutionSequence","FlowNode_Timer","PBZFlowTimer",
    "DialogueGraphNew_C","DialogueGraph_C","CallOwnerFunction_C","SetCollision_C",
    "SetTag_C","SetVisibility_C","SetDataLayerVisibility_C","SetCameraMode_C","UnSetCameraMode_C",
    "SetNPCEmotion_C","PlayUI_C","PlayLevelSequence_C","PlayLevelSequence_QTE_C",
    "PlaySubTitle_C","StopSubtitle_C","PlayUISound_C","BlackScreenWithDuration_C",
    "PlayTutorialUI_Complex_C","ChangeMusic_C","PlayAnim_C","RegisterInputType_C",
    "UnRegisterInputType_C","GetItems_C","LootItems_C","AddWeaponItem_C","AddMedicine_C",
    "ExecuteOnce_C","PlayerLooseTagAdd_C","UnLockXiaoZhangShop_C","StopTimer_C","StopRepeat_C",
    "TeleportTo_C","PushUpgradeUI_C","BeginQuest_C","FinishQuest_C","FinishQuestGroup_C"
]);
const guidOf=node=>node.Properties.NodeGuid.replaceAll("-","");
function compile(exports,targets) {
    const nodes=new Map(exports.filter(n=>n.Properties?.NodeGuid).map(n=>[guidOf(n),n]));
    const parents=new Map(),issues=new Set();
    for(const [guid,node] of nodes)for(const c of node.Properties.Connections || []) {
        const target=c.Value.NodeGuid.replaceAll("-","");
        if(!parents.has(target))parents.set(target,[]);
        parents.get(target).push({guid,pin:c.Key});
    }
    // Store graph routes, never reimplement native tag/quest predicates. At
    // runtime execute membership and choice validity come from the live flow.
    function edge(node,pin,seen) {
        const p=node.Properties,id=guidOf(node),type=node.Type;
        if(type==="SetExcuteType_C" && p.ExcuteType) {
            if(pin!=="Out" && pin!=="交互开始")return unknown;
            return {execute:id};
        }
        if(type==="FlowNode_HasWithSourceTag")return pin==="Match"?{gate:id}:unknown;
        if(type==="AddChoices_C") {
            const index=(p.OutputPins || []).findIndex(v=>v.PinName===pin);
            if(index<0 || !p.ChoiceData?.[index])return unknown;
            return all([{choice:id,index},p.OnlyRegister?{registeredChoice:id}:backward(id,seen)]);
        }
        if(type==="GiveItems_C")return all([{items:(p.ItemIDAndCount || []).map(v=>[v.Key,v.Value])},backward(id,seen)]);
        if(type==="SetExcuteType_C" || pass.has(type))return backward(id,seen);
        issues.add(type);return unknown;
    }
    let visits;
    function backward(guid,seen) {
        if(seen.has(guid))return false;
        if(++visits>12000){issues.add("path-budget");return unknown;}
        if(!nodes.has(guid)){issues.add("missing-reward-node");return unknown;}
        const incoming=parents.get(guid);
        if(!incoming?.length){issues.add("external-entry:"+nodes.get(guid).Type);return unknown;}
        const trail=new Set(seen).add(guid);
        return any(incoming.map(link=>edge(nodes.get(link.guid),link.pin,trail)));
    }
    const rules={};
    for(const target of targets){visits=0;rules[target]=backward(target,new Set());}
    return {nodes:rules,issues:[...issues].sort()};
}
async function main() {
    const [catalogPath,resourceRoot,outputPath,reportPath,documentsPath]=process.argv.slice(2);
    if(!reportPath)throw new Error("usage: compile-collection-gates <native-catalog> <resource-dir> <output-js> <report-json> [document-catalog-js]");
    const read=async file=>JSON.parse(await fs.readFile(file,"utf8"));
    const records=(await read(catalogPath)).records.filter(r=>r.map!=="Test" && r.category!=="BellDatas");
    if(documentsPath) {
        const text=await fs.readFile(documentsPath,"utf8");
        const docs=JSON.parse(text.slice(text.indexOf("module.exports=")+15).trim().replace(/;$/,""));
        records.push(...docs.filter(d=>d.flow).map(d=>({flow:d.flow,nodes:d.nodes,rewardId:d.id})));
    }
    const entries=await read(path.join(resourceRoot,"index.json"));
    const index=new Map(entries.map(e=>[path.basename(e.path,".uasset"),e]));
    const grouped=new Map();
    for(const r of records)if(r.flow && r.flow!=="None") {
        if(!grouped.has(r.flow))grouped.set(r.flow,{targets:new Set(),rewards:new Set(),placements:0});
        const g=grouped.get(r.flow);g.placements++;
        for(const n of r.nodes || [])g.targets.add(n.slice(-32));
        const reward=r.rewardId || r.itemId;if(reward && reward!=="None")g.rewards.add(reward);
    }
    const flows={},report=[];
    // At most four decoded resource packages are in flight. Results are sorted
    // before writing, so builds are deterministic across filesystem orderings.
    const work=[...grouped.entries()].sort(([a],[b])=>a.localeCompare(b));
    for(let start=0;start<work.length;start+=4)await Promise.all(work.slice(start,start+4).map(async ([flow,g])=>{
        const entry=index.get(flow);
        const result=entry?compile(await read(path.join(resourceRoot,entry.file)),g.targets):{nodes:{},issues:["missing-flow-asset"]};
        if(!g.targets.size)result.issues.push("missing-completion-node");
        flows[flow]=result.nodes;
        const rules=Object.values(result.nodes),unresolved=rules.filter(r=>signature(r).includes('"unknown"')).length;
        report.push({flow,placements:g.placements,rewards:[...g.rewards].sort(),nodes:rules.length,unresolved,issues:result.issues,source:entry?.path});
    }));
    const ordered=Object.fromEntries(Object.entries(flows).sort(([a],[b])=>a.localeCompare(b)));
    const output='"use strict";\n// Generated native reward routes. Predicates are evaluated by the live game.\nmodule.exports='+JSON.stringify(ordered,null,2)+';\n';
    await fs.mkdir(path.dirname(outputPath),{recursive:true});
    await fs.writeFile(outputPath,output);
    await fs.mkdir(path.dirname(reportPath),{recursive:true});
    report.sort((a,b)=>a.flow.localeCompare(b.flow));
    await fs.writeFile(reportPath,JSON.stringify({flows:report.length,placements:records.length,entries:report},null,2)+'\n');
    console.log(JSON.stringify({flows:report.length,nodes:report.reduce((s,r)=>s+r.nodes,0),unresolvedNodes:report.reduce((s,r)=>s+r.unresolved,0),missingAssets:report.filter(r=>r.issues.includes("missing-flow-asset")).map(r=>r.flow)}));
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
