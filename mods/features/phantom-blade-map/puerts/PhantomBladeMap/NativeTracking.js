"use strict";
const idOf = (quest, task) => "quest:" + quest + ":" + task;

// The native map has already applied quest, branch, completion and tag gates.
// Copy only its visible point values; no quest/widget references leave this call.
function snapshot(page, UE) {
    const points = [];
    for (const data of page.focusItemMap?.values() || []) {
        if (!data.questObjName || !data.taskID || !data.location) continue;
        if(String(data.taskID).startsWith("Accept_"))continue;
        const p = data.location;
        points.push({id:idOf(data.questObjName,data.taskID),quest:String(data.questObjName),
            task:String(data.taskID),mapID:String(data.mapID),x:p.X,y:p.Y,z:p.Z,
            category:"quest",state:"remaining",layer:-1,nativeVisible:true,label:""});
    }
    const narrative = UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page);
    const labels = new Map(), keys = new Map(), wanted=new Set(points.map(p=>p.quest));
    for (const quest of narrative?.QuestList || []) {
        if (!quest)continue;
        const name=String(quest.GetName());
        if(wanted.has(name)) {
            labels.set(name,UE.KismetTextLibrary.Conv_TextToString(quest.GetQuestName()));
            keys.set(name,String(UE.KismetSystemLibrary.GetPathName(quest.GetClass())));
        }
    }
    for (const point of points) { point.label = labels.get(point.quest) || "";point.questKey=keys.get(point.quest); }
    const tracked = narrative?.GetTrackedQuest();
    const trackedID = tracked ? idOf(String(tracked.GetName()),String(narrative.GetTrackedPointTaskID())) : "";
    return {points,trackedID};
}

function track(page, UE, point) {
    if(point.pointKind && point.pointKind!=="objective")return false;
    const narrative = UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page);
    const current = narrative?.GetTrackedQuest();
    if (current && idOf(String(current.GetName()),String(narrative.GetTrackedPointTaskID())) === (point.nativeID || point.id)) return true;
    for (const data of page.focusItemMap?.values() || []) {
        if (data.questObjName === point.quest && data.taskID === point.task) {
            // Native code owns tracking, HUD direction/distance, save state and
            // completion. Its normal action also refreshes native highlights.
            page.toggleTrackQuestPoint(data);
            const tracked=narrative?.GetTrackedQuest();
            return !!tracked && idOf(String(tracked.GetName()),String(narrative.GetTrackedPointTaskID()))===(point.nativeID || point.id);
        }
    }
    if(point.nativeTrack) for(const quest of narrative?.QuestList || []) {
        if(!quest || String(quest.GetName())!==point.quest)continue;
        narrative.SetTrackedPoint(quest,point.branch,point.taskPointID);
        const tracked=narrative.IsPointTracked(quest,point.branch,point.taskPointID);
        if(tracked)page.refreshTrackedHighlight();
        return tracked;
    }
    return false;
}
module.exports = {snapshot,track};
