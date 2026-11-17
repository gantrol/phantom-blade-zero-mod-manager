"use strict";
const {PointIndex}=require("./PointIndex");
const Availability=require("./CollectionAvailability");
const CollectionState=require("./CollectionState");
const SourceState=require("./SourceState");

// Native state is read only after an acquisition/progression notification.
// The queue stores point IDs; no subsystem/actor/struct survives a slice.
class CollectionRefresh {
    constructor(points,pending=[]) {
        this.points=points;this.index=new PointIndex(points);this.pending=new Set();this.changed=0;this.checked=0;
        for(const id of pending)if(this.index.byID.has(id))this.pending.add(id);
    }
    invalidate(change,player,focusID) {
        if(change.reasons?.every(reason=>reason==="quest"))return;
        for(const p of this.index.affected(change,player,focusID))this.pending.add(p.id);
        this.progress=undefined;
    }
    step(context,UE,helper) {
        if(!this.pending.size)return [];
        const get=type=>helper.getWorldSubsystem(context,UE[type].StaticClass());
        const bag=get("PBZBackPackSubsystem"),game=get("PBZGameSystem"),events=get("EventPipelineManager"),actors=get("PBZLevelActorManager"),bells=get("BellManager");
        if(!bag || !game || !events || !actors)return [];
        this.progress ||= Availability.capture(events,UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context),bag,UE);
        const acquisition={bag,cycle:game.GetCycleCount(),events,tags:this.progress.tags};
        const changed=[],started=Date.now();
        for(let count=0;this.pending.size && count<16 && (count===0 || Date.now()-started<2);count++) {
            const id=this.pending.values().next().value;this.pending.delete(id);
            const p=this.index.byID.get(id);if(!p || p.category==="quest" || p.category==="marker")continue;
            if(p.state==="collected" && p.category!=="bell")continue;
            const previousState=p.state,before=JSON.stringify([p.state,p.availability,p.requirements,p.x,p.y,p.z]);
            if(p.category==="bell" && bells)p.state=bells.GetAllActivateBellIDs().Contains(p.reward)?"collected":"remaining";
            else if(CollectionState.collected(p,acquisition))p.state="collected";
            if(p.state!=="collected") {
                Availability.refreshSource(p,actors,UE);
                if(!p.landmark) {
                    const state=SourceState.refresh(p,actors,UE);
                    // A container's explicit completion is authoritative even
                    // when its reward has an inventory-based acquisition rule.
                    const done=!CollectionState.supported(p) && ((p.category==="amber" || p.category==="blackstone") && p.save && actors.HasSaveDataByActorID(p.save) ||
                        p.domain && game.CompleteAddItemMobDomainNames.Contains(p.domain) || p.nodes?.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined));
                    if(done || state==="collected")p.state="collected";
                    else if(state)p.state=state;
                }
            }
            Availability.refresh(p,this.progress);this.checked++;
            if(previousState!==p.state)require("./Diagnostics").record("collection-source-state",{id:p.id,from:previousState,to:p.state});
            if(before!==JSON.stringify([p.state,p.availability,p.requirements,p.x,p.y,p.z])){changed.push(p);this.changed++;}
        }
        if(!this.pending.size) {
            if(this.changed)this.index=new PointIndex(this.points);
            require("./Diagnostics").record("collection-event-applied",{checked:this.checked,changed:this.changed});
            this.progress=undefined;this.checked=this.changed=0;
        }
        return changed;
    }
}
module.exports={CollectionRefresh};
