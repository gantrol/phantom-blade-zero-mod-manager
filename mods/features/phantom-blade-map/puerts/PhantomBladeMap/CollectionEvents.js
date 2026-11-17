"use strict";
const Diagnostics=require("./Diagnostics");
const hubs=new WeakMap();

function watch(context,listener) {
    const UE=require("ue"),helper=require("../Library/TsHelper").default,owner=context.GetWorld();
    let hub=hubs.get(owner);
    if(!hub) {
        hub={listeners:new Set(),bindings:[],items:new Set(),reasons:new Set(),positions:[],inventory:false};
        hubs.set(owner,hub);
        const emit=(reason,itemID,inventory=false)=>{
            hub.reasons.add(reason);hub.inventory ||= inventory;
            if(itemID && String(itemID)!=="None")hub.items.add(String(itemID));
            // One position snapshot per native transaction, not a world scan.
            if(!hub.positions.length && UE.KismetSystemLibrary.IsValid(owner)) {
                const p=require("./PlayerContext").read(owner,UE,helper);
                const map=helper.getWorldSubsystem(owner,UE.MapSubsystem.StaticClass());
                if(p && map)hub.positions.push({x:p.x,y:p.y,z:p.groundZ,mapID:String(map.GetMapIDByLocation(new UE.Vector(p.x,p.y,p.z)))});
            }
            if(hub.timer!==undefined)return;
            // Rewards may be broadcast before the source writes its completion
            // bit. Coalesce the transaction once, after that native call stack.
            hub.timer=setTimeout(()=>hub.flush(),120);
        };
        // Closing a map hands its transaction to the queues before releasing
        // the final subscription. This only transfers metadata, not native reads.
        hub.flush=()=>{
            if(hub.timer!==undefined)clearTimeout(hub.timer);
            hub.timer=undefined;
            if(!hub.reasons.size)return;
            const change={items:[...hub.items],reasons:[...hub.reasons],positions:hub.positions,inventory:hub.inventory};
            hub.items.clear();hub.reasons.clear();hub.positions=[];hub.inventory=false;
            Diagnostics.record("collection-event",{reasons:change.reasons,items:change.items.length,itemIDs:change.items.slice(0,8)});
            for(const callback of hub.listeners)try{callback(change);}catch(error){Diagnostics.record("collection-event-error",{error:String(error)});}
        };
        const bind=(source,name,callback)=>{
            const delegate=source?.[name];
            if(!delegate?.Add || !delegate?.Remove)return;
            const handler=(...args)=>{try{callback(...args);}catch(error){Diagnostics.record("collection-event-error",{event:name,error:String(error)});}};
            delegate.Add(handler);hub.bindings.push({source,name,handler});
        };
        const bag=helper.getWorldSubsystem(owner,UE.PBZBackPackSubsystem.StaticClass());
        bind(bag,"InventoryAmountChanged",id=>emit("inventory",id));
        bind(bag,"RemoveProps",id=>emit("inventory",id));
        bind(bag,"OnAllItemsGranted",()=>emit("granted",undefined,true));
        // These acquisition paths can bypass the ordinary stack-amount event.
        // Use a native item ID when supplied; otherwise check cached unique
        // rewards, as for an ID-less grant batch. Never retain native payloads.
        for(const name of ["OnWeaponAcquired","OnAccessoryObtained","PushMedicineToInventoryDelegate","PushItemToInventory","GetBooks"])
            bind(bag,name,id=>typeof id==="string" && id && id!=="None"?emit("inventory",id):emit("granted",undefined,true));
        // Dust is a character currency, not a backpack item. Subscribe to the
        // same gameplay event as the shipped DustTips widget, independently of
        // that widget's visibility. A gain only invalidates local source state;
        // enemy drops and consumed dust items must not mark a map point done.
        hub.clearDust=()=>{
            const dust=hub.dust;hub.dust=undefined;
            if(dust && UE.KismetSystemLibrary.IsValid(dust.action)) {
                dust.action.EventReceived.Remove(dust.handler);
                dust.action.Cancel();
            }
        };
        const bindDust=pawn=>{
            if(!pawn || !UE.KismetSystemLibrary.IsValid(pawn) ||
                typeof pawn.GetCurrentDust!=="function" || hub.dust?.pawn===pawn)return;
            hub.clearDust();
            const action=UE.AbilityAsync_WaitGameplayEvent.WaitGameplayEventToActor(pawn,
                helper.constructGameplayTag("GamePlayEvent.UpdateDustTips"),false,false);
            if(!action)return;
            const handler=payload=>{
                try {
                    const change=payload.OptionalObject;
                    if(change && Number(change.ChangeDustNum)>0)emit("dust","currency:dust");
                } catch(error){Diagnostics.record("collection-event-error",{event:"dust",error:String(error)});}
            };
            hub.dust={pawn,action,handler};
            action.EventReceived.Add(handler);action.Activate();
            Diagnostics.record("collection-currency-bound",{eventTag:"GamePlayEvent.UpdateDustTips"});
        };
        // Rebind on character replacement, not on a timer. Possessing a boat
        // does not discard the character's currency subscription.
        bind(UE.GameplayStatics.GetPlayerController(owner,0),"OnPossessedPawnChanged",(_old,pawn)=>bindDust(pawn));
        bind(UE.GameplayStatics.GetGameMode(owner),"SpawnPawnDelegate",pawn=>bindDust(pawn));
        bindDust(UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(owner) || UE.GameplayStatics.GetPlayerCharacter(owner,0));
        const map=helper.getWorldSubsystem(owner,UE.MapSubsystem.StaticClass());
        bind(map,"OnLockStateChanged",()=>emit("locks"));
        const bells=helper.getWorldSubsystem(owner,UE.BellManager.StaticClass());
        bind(bells,"OnInteractBell",actor=>emit("bell",actor?.BellID));
        // Reflected delegate names, not the similarly named broadcast functions.
        const events=helper.getWorldSubsystem(owner,UE.EventPipelineManager.StaticClass());
        bind(events,"OnWithSourceTagAdd",()=>emit("progress"));
        bind(events,"OnEntryCollisionVisibleChange",()=>emit("progress"));
        const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(owner);
        bind(narrative,"OnQuestTrackingChanged",()=>emit("quest"));
        bind(narrative,"OnQuestBranchStateChanged",()=>emit("progress"));
        bind(narrative,"OnQuestTaskCompleted",()=>emit("progress"));
        Diagnostics.record("collection-events-bound",{events:hub.bindings.map(p=>p.name)});
    }
    hub.listeners.add(listener);
    const stop=()=>{
        hub.listeners.delete(listener);
        if(hub.listeners.size)return;
        if(hub.timer!==undefined)clearTimeout(hub.timer);
        try{hub.clearDust?.();}catch(error){Diagnostics.record("collection-event-error",{event:"dust-unbind",error:String(error)});}
        for(const {source,name,handler} of hub.bindings) {
            try{if(UE.KismetSystemLibrary.IsValid(source))source[name]?.Remove(handler);}catch{}
        }
        hub.bindings=[];hubs.delete(owner);
    };
    stop.flush=hub.flush;
    return stop;
}
module.exports={watch};
