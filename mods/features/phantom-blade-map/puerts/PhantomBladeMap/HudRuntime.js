"use strict";
const Diagnostics=require("./Diagnostics"),Atlas=require("./OfflineAtlas");
const {Exploration}=require("./Exploration"),{CanvasDrawing}=require("./CanvasDrawing"),{PlanarMap}=require("./PlanarMap"),{rgba}=require("./Appearance");
const SLOT="__phantomBladeMapGuide";
const TrackingState=require("./TrackingState");
const {located}=require("./QuestObjectives");
const Availability=require("./CollectionAvailability");
const AutoCollect=require("./AutoCollect"),Filter=require("./CollectibleFilter");
const PlayerContext=require("./PlayerContext");
const world=id=>Atlas.aliases[String(id).toLowerCase()] || String(id).toLowerCase();
let target,model,text,coverage,atlas,timer,paused=true,ownerKey="",stableSince=0,lastState=0,lastMini=0,desiredFloor=-1,floorSince=0,currentFloor=-1;
let checkProfile=true,stateCursor=0,frameClass,viewGeneration=0;
let lastPhase="",lastFog=0;
let automaticDirty=true,lastAutomatic=0,automaticMap="";
function phase(value){if(value!==lastPhase){lastPhase=value;Diagnostics.record("hud-state",{state:value,target:target?.id || ""});}}
function owner(UE) {
    const context=UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
    if(!context || !UE.KismetSystemLibrary.IsValid(context))return;
    const pc=UE.GameplayStatics.GetPlayerController(context,0);
    if(!pc || !UE.KismetSystemLibrary.IsValid(pc))return;
    const pawn=UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context) || UE.GameplayStatics.GetPlayerCharacter(context,0),hud=UE.PBZHUDLayout.GetHUDLayout(context);
    const key=[pc,pc.GetWorld(),pawn,hud].map(p=>p && UE.KismetSystemLibrary.IsValid(p)?String(UE.KismetSystemLibrary.GetPathName(p)):"").join("|");
    return {context,pc,key,hud};
}
function retire(frame,UE,reason) {
    if(!frame || !UE.KismetSystemLibrary.IsValid(frame))return;
    const view=frame[SLOT];frame[SLOT]=undefined;
    // An orphan must disappear before cleanup. Returning from Tick would leave
    // its last rendered marker on screen when the new HUD takes ownership.
    frame.SetVisibility(UE.ESlateVisibility.Collapsed);
    frame.RemoveFromParent();
    if(!view || view.disposed)return;
    view.disposed=true;view.markerEnabled=false;
    try {
        try{view.map?.dispose();}catch(error){Diagnostics.record("minimap-release",{error:String(error)});}
        if(view.icon && UE.KismetSystemLibrary.IsValid(view.icon))view.icon.SetBrushFromTexture(undefined);
    } finally {view.drawing.release(false);}
    Diagnostics.record("hud-view-retired",{reason,generation:view.generation,target:view.targetID || ""});
}
function attached(view,current) {
    return view.host==="boat"?view.frame.IsInViewport():
        view.frame.GetParent()===current.hud?.HudOverlay;
}
function presentationAllowed(current,UE) {
    const {context,pc,hud}=current;
    if(!hud || !UE.KismetSystemLibrary.IsValid(hud) || !hud.IsActivated() || pc.bCinematicMode || UE.GameplayStatics.IsGamePaused(context))return false;
    const playerHUD=pc.GetHUD();
    if(playerHUD && !playerHUD.bShowHUD)return false;
    // A viewport-hosted boat map must still respect native HUD hiding. Do not
    // infer that an invisible branch is merely the sailing presentation.
    for(let node=hud;node;node=node.GetParent())if(!node.IsVisible() || node.GetRenderOpacity()<=0)return false;
    return true;
}
function remove(current,UE,reason="detached") {
    if(!current)return;
    const {pc,hud}=current;
    for(const holder of [pc,hud])if(holder && UE.KismetSystemLibrary.IsValid(holder)) {
        const view=holder[SLOT];holder[SLOT]=undefined;
        if(view)retire(view.frame,UE,reason);
    }
    if(!hud || !UE.KismetSystemLibrary.IsValid(hud))return;
    const overlay=hud.HudOverlay;
    if(!overlay || !UE.KismetSystemLibrary.IsValid(overlay) || !frameClass)return;
    // A native HUD wrapper may be recreated and lose its JS-only cache. The
    // overlay's own children remain authoritative; inspect only our frame type.
    for(let i=overlay.GetChildrenCount()-1;i>=0;i--) {
        const child=overlay.GetChildAt(i);
        if(child && child.IsA(frameClass))retire(child,UE,reason);
    }
}
function currentView(current,UE) {
    const {pc,hud}=current,cached=pc[SLOT] || hud[SLOT];
    if(cached && !cached.disposed && cached.generation===viewGeneration && UE.KismetSystemLibrary.IsValid(cached.frame) && attached(cached,current))return cached;
    pc[SLOT]=hud[SLOT]=undefined;
    const overlay=hud.HudOverlay;
    if(!overlay || !frameClass)return;
    for(let i=overlay.GetChildrenCount()-1;i>=0;i--) {
        const frame=overlay.GetChildAt(i);
        if(!frame || !frame.IsA(frameClass))continue;
        const view=frame[SLOT];
        if(view && !view.disposed && view.generation===viewGeneration && view.ownerKey===ownerKey) {
            pc[SLOT]=hud[SLOT]=view;return view;
        }
    }
}
function releaseData() {atlas?.dispose();atlas=undefined;coverage?.clearProjection();currentFloor=desiredFloor=-1;}
function detach() {const UE=require("ue"),current=owner(UE);if(current)remove(current,UE);}
function stop(persist=true) {if(persist && model?.profile)TrackingState.write(model.profile,undefined);viewGeneration++;target=undefined;automaticDirty=true;detach();}
function suspend() {viewGeneration++;if(timer!==undefined)clearInterval(timer);timer=undefined;releaseData();}
function getFrameClass(UE) {
    // Only the generated type is cached. Its instances and callbacks belong to
    // the current HUD; no game widget class or global tick function is patched.
    if(!frameClass) {
        class PhantomBladeMapFrame extends UE.UserWidget {
            Tick(geometry,deltaSeconds) {drawFrame(this,geometry,deltaSeconds);}
        }
        // The shipped UE constructor has no Object.prototype in its static
        // chain, but this PuerTS makeUClass calls cls.hasOwnProperty directly.
        // Supply that method on our class only; leave the engine types intact.
        Object.defineProperty(PhantomBladeMapFrame,"hasOwnProperty",{value:Object.prototype.hasOwnProperty});
        frameClass=require("puerts").makeUClass(PhantomBladeMapFrame);
    }
    return frameClass;
}
function create(current,aboard,UE,cpp) {
    remove(current,UE,"replaced");
    const {context,pc,hud}=current;
    // A private UserWidget supplies Slate's current-frame geometry and Tick.
    // The boat uses its own player-screen layer; normal play uses HudOverlay.
    // Each mode owns one tree and the previous mode is retired before mounting.
    const frame=UE.WidgetBlueprintLibrary.Create(context,getFrameClass(UE),pc);
    if(!frame?.WidgetTree)throw new Error("HUD frame widget has no tree");
    const root=UE.NewObject(UE.CanvasPanel.StaticClass(),frame.WidgetTree);
    frame.WidgetTree.RootWidget=root;
    const drawing=new CanvasDrawing(frame,UE,cpp,text);
    drawing.roots.push(frame);drawing.widgets.push(frame,root);drawing.owned.add(root);
    const view={drawing,root,frame,ownerKey,host:aboard?"boat":"hud",generation:++viewGeneration};pc[SLOT]=hud[SLOT]=view;frame[SLOT]=view;
    frame.SetVisibility(UE.ESlateVisibility.HitTestInvisible);root.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    if(aboard) {
        if(!frame.AddToPlayerScreen(0))throw new Error("Boat map could not join the player screen");
    } else {
        hud.HudOverlay.AddChild(frame);
        const slot=UE.WidgetLayoutLibrary.SlotAsOverlaySlot(frame);
        if(!slot)throw new Error("HUD guide has no overlay slot");
        slot.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill);slot.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);
    }
    frame.ForceVolatile(true);
    Diagnostics.record("hud-view-created",{generation:view.generation,owner:ownerKey,host:view.host});
    view.map=new PlanarMap(drawing,root,32);
    view.heading=drawing.makeText(root,14,0,0,280,26,rgba("paper"));view.detail=drawing.makeText(root,12,0,0,280,48,rgba("paper"));
    view.detail.widget.SetAutoWrapText(true);view.heading.slot.SetZOrder(25);view.detail.slot.SetZOrder(25);
    for(const node of [view.heading,view.detail]){node.widget.SetShadowOffset(new UE.Vector2D(1,1));node.widget.SetShadowColorAndOpacity(new UE.LinearColor(0,0,0,.9));}
    const box=drawing.create(UE.CanvasPanel,root);view.box={widget:box,slot:UE.WidgetLayoutLibrary.SlotAsCanvasSlot(box)};
    view.box.slot.SetPosition(new UE.Vector2D(0,0));
    view.box.slot.SetSize(new UE.Vector2D(220,108));view.box.slot.SetAlignment(new UE.Vector2D(.5,0));view.box.slot.SetZOrder(30);
    drawing.visible(view.box,false);
    const image=drawing.makeRect(box,90,0,40,40,1,1,1,1);view.icon=image.widget;
    view.distance=drawing.makeText(box,14,0,42,220,24,rgba("gold"));view.elevation=drawing.makeText(box,11,0,66,220,22,rgba("paper"));
    view.label=drawing.makeText(box,11,0,86,220,24,rgba("paper"));
    for(const node of [view.distance,view.elevation,view.label]){node.widget.SetJustification(UE.ETextJustify.Center);node.widget.SetShadowOffset(new UE.Vector2D(1,1));node.widget.SetShadowColorAndOpacity(new UE.LinearColor(0,0,0,.9));}
    return view;
}
function copy(point,native=false) {
    const value={native};
    for(const key of ["id","x","y","z","mapID","category","state","label","stage","save","sourceClass","staticSource","filterGroup","filterGroups","inventory","reward","questKey","branch","domain","pointKind","documentSource","taskPointID","task","nativeID","knownQuestPoint","objectiveID","flow","availability","requirement","sourceUnavailable","flowAvailability","sourceCheckedAt","landmark","globalDocument"])
        if(point[key]!==undefined)value[key]=point[key];
    value.nodes=(point.nodes||[]).slice();return value;
}
function completed(context,UE) {
    const helper=require("../Library/TsHelper").default;
    if(target.category==="marker")return false;
    if(target.category==="quest") {
        const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context);
        const tracked=narrative?.GetTrackedQuest();
        if(target.native && tracked && String(UE.KismetSystemLibrary.GetPathName(tracked.GetClass()))!==target.questKey)return "changed";
        const quest=Array.from(narrative?.QuestList||[]).find(q=>q && String(UE.KismetSystemLibrary.GetPathName(q.GetClass()))===target.questKey);
        if(!quest)return false;
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(!events)return false;
        const points=require("./SideQuests").readQuest(quest,events,UE);
        let next=points.find(p=>p.id===target.id || (target.nativeID && p.nativeID===target.nativeID)) || points[0];
        if(!next) {
            if(quest.QuestCompletion!==UE.EQuestCompletion.QC_Started)return true;
            next={...target,x:undefined,y:undefined,z:undefined,pointKind:"pending",nativeTrack:false};
        }
        if(next.id!==target.id || next.pointKind!==target.pointKind || next.x!==target.x || next.y!==target.y || next.z!==target.z) {
            const native=target.native && next.nativeTrack;
            if(native)narrative.SetTrackedPoint(quest,next.branch,next.taskPointID);
            target=copy(next,native);
            TrackingState.write(model.profile,target);
            Diagnostics.record("hud-stage",{id:target.id,kind:target.pointKind,mapID:target.mapID});
        }
        if(target.native && !tracked){target.native=false;TrackingState.write(model.profile,target);}
        return false;
    }
    return itemCompleted(context,UE,target);
}
function itemCompleted(context,UE,point) {
    const helper=require("../Library/TsHelper").default;
    if(point.category==="quest" || point.category==="marker")return false;
    if(point.flow) {
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager)Availability.refreshSource(point,manager,UE);
    }
    if(point.category==="book" && point.reward) {
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass());
        const game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass());
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(require("./Documents").collected(point,bag,game?.GetCycleCount(),events))return true;
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager && !point.flow)Availability.refreshSource(point,manager,UE);
        // A container's completion never substitutes for this document's record.
        return false;
    }
    const treasures=require("./Treasures");
    if(treasures.contains(point.reward)) {
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass());
        const game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass());
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(bag && game && events && treasures.collected(point.reward,bag,game.GetCycleCount(),Availability.snapshot(events)))return true;
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager && !point.flow)Availability.refreshSource(point,manager,UE);
        return false;
    }
    if(point.save) {
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager) {
            if((point.category==="amber" || point.category==="blackstone") && manager.HasSaveDataByActorID(point.save))return true;
            const SourceState=require("./SourceState"),state=SourceState.savedState(manager,point.save,UE,require("puerts"),point.sourceClass,point.staticSource);
            if(state==="collected")return true;
            if(state==="remaining")point.state=state;
            const kind=SourceState.source(point.save,point.sourceClass),actor=kind && manager.GetActorByID(point.save,UE.Actor.StaticClass());
            if(actor && UE.KismetSystemLibrary.IsValid(actor) && !actor.IsActorBeingDestroyed()) {
                const done=kind.live?actor[kind.live]:undefined;
                if(done===true)return true;
                Availability.observe(point,actor);
                if(!point.sourceUnavailable) {
                    if(done===false || kind.pickup && actor.bIsPickable && actor.IsValidPickup())point.state="remaining";
                }
            }
        }
    }
    if(point.category==="bell")return !!helper.getWorldSubsystem(context,UE.BellManager.StaticClass())?.GetAllActivateBellIDs().Contains(point.reward);
    if(point.inventory && point.reward) {
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass()),game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass());
        const item=bag?.[point.inventory].GetRef(point.reward);return !!item && item.AcquiredCycle===game?.GetCycleCount();
    }
    if(point.domain && helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass())?.CompleteAddItemMobDomainNames.Contains(point.domain))return true;
    if(point.nodes?.length){const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());if(events && point.nodes.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined))return true;}
    return false;
}
function update(current,now,UE) {
    if(paused || !model || !model.config.visibility)return;
    const cpp=require("cpp");
    if(!current){if(ownerKey)viewGeneration++;phase("waiting-world");releaseData();ownerKey="";return;}
    const {context,pc,hud,key}=current;
    if(key!==ownerKey){viewGeneration++;releaseData();remove(current,UE,"owner-changed");ownerKey=key;stableSince=now;checkProfile=true;return;}
    if(now-stableSince<450 || !hud || !UE.KismetSystemLibrary.IsValid(hud)){phase("waiting-hud");return;}
    const helper=require("../Library/TsHelper").default,position=PlayerContext.read(context,UE,helper);
    if(!position || !presentationAllowed(current,UE)){phase("paused-or-hidden");remove(current,UE);return;}
    if(checkProfile) {
        checkProfile=false;
        if(require("./Store").profile(UE,context)!==model.profile){target=model=coverage=undefined;suspend();remove(current,UE);return;}
    }
    if(now-lastState>1000) {
        lastState=now;
        const events=helperEvents(context,UE);
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass());
        if(events) {
            const progress=Availability.capture(events,UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context),bag,UE);
            if(Availability.refreshAll(model.points,progress))automaticDirty=true;
            if(target)Availability.refresh(target,progress);
        }
        const done=target && completed(context,UE);
        if(done){if(done===true)model.points=model.points.filter(p=>p.id!==target.id);stop();}
        if(target && target.category!=="quest") {
            const point=model.points.find(p=>p.id===target.id);
            if(point)for(const key of ["state","availability","requirement","sourceUnavailable","flowAvailability","sourceCheckedAt"])point[key]=target[key];
        }
        // Recheck a bounded part of the already-known catalog. No discovery or
        // actor enumeration runs here; native source completion remains decisive.
        for(let n=0;n<2 && model.points.length;n++) {
            stateCursor%=model.points.length;const point=model.points[stateCursor];
            if(itemCompleted(context,UE,point))model.points.splice(stateCursor,1);
            else stateCursor++;
        }
        if(model.config.autoCollect) {
            // Prefer checking nearby known placements as their partitions load.
            // Only IDs already in the catalog are queried; no actor scan runs.
            const map=helper.getWorldSubsystem(context,UE.MapSubsystem.StaticClass());
            const mapID=String(map?.GetCurrentMapID() || "").toLowerCase();
            const nearby=model.points.filter(p=>(p.flow || p.save && (p.state==="unknown" || p.sourceUnavailable)) && String(p.mapID).toLowerCase()===mapID &&
                Filter.visible(p,model.config) && now-(p.checkedAt || 0)>4000 &&
                (!model.config.exploredOnly || p.explored || coverage.visible(p)))
                .sort((a,b)=>Math.hypot(a.x-position.x,a.y-position.y,a.z-position.z)-Math.hypot(b.x-position.x,b.y-position.y,b.z-position.z)).slice(0,2);
            for(const point of nearby) {
                point.checkedAt=now;
                if(itemCompleted(context,UE,point))model.points=model.points.filter(p=>p.id!==point.id);
            }
        }
    }
    if(!model.config.minimap && !model.config.autoCollect && (!target || target.native)){suspend();remove(current,UE);return;}
    const map=helper.getWorldSubsystem(context,UE.MapSubsystem.StaticClass()),mapID=String(map?.GetCurrentMapID()||"");
    if(!mapID || mapID==="None"){remove(current,UE);return;}
    const {x,y,z,yaw,aboard}=position,player={x,y,z,yaw,aboard,mapID};coverage.visit(player);
    if(now-lastFog>=1000 || atlas?.mapID!==mapID) {
        lastFog=now;coverage.refresh(require("./Fog").records(context,UE));
    }
    if(automaticDirty || automaticMap!==mapID || now-lastAutomatic>1000)syncAutomatic(context,player);
    if(!atlas || atlas.world!==world(mapID)){releaseData();atlas=Atlas.open(mapID);}
    if(atlas)atlas.mapID=mapID;
    const actualFloor=atlas?.floor(player)??-1;
    if(actualFloor!==desiredFloor){desiredFloor=actualFloor;floorSince=now;}
    if(currentFloor<0 || now-floorSince>450)currentFloor=desiredFloor;
    player.layer=currentFloor;
    const local=located(target) && world(target.mapID)===world(mapID),config=model.config;
    let view=currentView(current,UE);
    if(view && view.host!==(aboard?"boat":"hud")){remove(current,UE,"travel-mode-changed");view=undefined;}
    view=view || create(current,aboard,UE,cpp);
    const d=view.drawing;
    view.markerEnabled=false;
    const geometry=view.root.GetCachedGeometry(),size=UE.SlateBlueprintLibrary.GetLocalSize(geometry),width=size.X,height=size.Y;
    if(width<400 || height<300){d.visible(view.box,false);phase("waiting-layout");return;}
    const layoutKey=width+":"+height;
    if(view.layoutKey!==layoutKey){view.layoutKey=layoutKey;view.motion=undefined;Diagnostics.record("hud-layout",{target:target?.id || "",host:view.host,width,height,attached:attached(view,current)});}
    phase("laid-out");
    const nonSpatial=target && (target.availability || !located(target) || !local);
    if(config.minimap) {
        view.heading.widget.SetJustification(UE.ETextJustify.Left);view.detail.widget.SetJustification(UE.ETextJustify.Left);
        const miniWidth=Math.min(280,width*.23),left=width-miniWidth-28,top=56;
        view.map.bounds(left,top,miniWidth,miniWidth);d.place(view.heading,left+12,top+10);d.place(view.detail,left,top+miniWidth+8);
        const layer=atlas?.layers[currentFloor];d.textValue(view.heading,atlas?(text["region_"+mapID.toLowerCase()] || "")+" · "+(layer?text[layer.kind]+(layer.depth?" "+layer.depth:""):text.floorUnknown):text.offlineMissing);
        d.visible(view.heading,true);d.visible(view.detail,!!target);
        if(now-lastMini>=160) {
            lastMini=now;
            const points=model.points.concat(config.showTraversal?model.facilities:[]).filter(q=>located(q) && world(q.mapID)===world(mapID) && Filter.visible(q,config) && (!config.remainingOnly || q.state!=="collected"));
            for(const q of points)q.layer=atlas?.floor(q)??-1;
            if(target && local && !points.some(q=>q.id===target.id))points.unshift({...target,layer:atlas?.floor(target)??-1});
            coverage.sample(context,UE,()=>view.map.update(atlas,currentFloor,{...player,cm:50},player,points,local?target:undefined,coverage,config.exploredOnly,config.showFloors,config.showFog));
        }
        if(target) {
            const distance=Math.round(Math.hypot(target.x-player.x,target.y-player.y,target.z-player.z)/100);
            const detail=target.availability?text[target.availability]:!located(target)?text.objectivePending:local?text.straight+" "+distance+" m":text.targetOtherArea;
            d.textValue(view.detail,[target.label,text["objective_"+target.objectiveID]].filter(Boolean).join(" · ")+"\n"+detail);
        }
    } else {
        view.map.clear();d.visible(view.heading,!!nonSpatial);d.visible(view.detail,!!nonSpatial);
        if(nonSpatial) {
            view.heading.widget.SetJustification(UE.ETextJustify.Right);view.detail.widget.SetJustification(UE.ETextJustify.Right);
            d.place(view.heading,width-308,64);d.place(view.detail,width-308,94);
            d.textValue(view.heading,target.label || text[target.category]);
            d.textValue(view.detail,target.availability?text[target.availability]:!located(target)?text.objectivePending:
                text.targetOtherArea+" · "+(text["region_"+target.mapID.toLowerCase()] || target.mapID));
        }
    }
    if(!target || target.native || nonSpatial){d.visible(view.box,false);view.motion=undefined;return;}
    view.markerEnabled=true;
    if(view.targetID!==target.id) {
        const name=target.category==="quest"?"T_Track_News":"T_Track_Collection",path="/Game/PBZContent/UI/InGame/Quest/"+name+"."+name;
        view.icon.SetBrushFromSoftTexture(UE.KismetSystemLibrary.Conv_SoftObjPathToSoftObjRef(UE.KismetSystemLibrary.MakeSoftObjectPath(path)));
        d.textValue(view.label,[target.label,text["objective_"+target.objectiveID]].filter(Boolean).join(" · "));view.targetID=target.id;view.motion=undefined;
    }
    view.distanceMeters=Math.round(Math.hypot(target.x-player.x,target.y-player.y,target.z-player.z)/100);
    const dz=Math.round((target.z-player.z)/100);
    d.textValue(view.elevation,Math.abs(dz)>2?(dz>0?text.above:text.below)+" "+Math.abs(dz)+" m":text.sameLevel);
}
function renderMarker(view,current,geometry,deltaSeconds,UE) {
    if(!target || !view.markerEnabled || view.targetID!==target.id)return;
    const {pc}=current,d=view.drawing,puerts=require("puerts");
    const size=UE.SlateBlueprintLibrary.GetLocalSize(geometry),width=size.X,height=size.Y;
    if(width<400 || height<300){d.visible(view.box,false);view.motion=undefined;return;}
    // Project into full-viewport pixels, then convert once into THIS frame's
    // geometry. Player-relative coordinates and cached geometries can disagree
    // with the HUD's viewport origin/scale during camera and layout updates.
    const screen=puerts.$ref(new UE.Vector2D()),front=pc.ProjectWorldLocationToScreen(new UE.Vector(target.x,target.y,target.z),screen,false);
    let x,y;
    if(front) {
        const local=puerts.$ref(new UE.Vector2D());
        UE.SlateBlueprintLibrary.ScreenToWidgetLocal(pc,geometry,puerts.$unref(screen),local,false);
        const q=puerts.$unref(local);x=q.X;y=q.Y;
    } else {
        const camera=pc.PlayerCameraManager;
        if(!camera || !UE.KismetSystemLibrary.IsValid(camera)){d.visible(view.box,false);view.motion=undefined;return;}
        const p=camera.GetCameraLocation(),yaw=camera.GetCameraRotation().Yaw*Math.PI/180;
        const dx=target.x-p.X,dy=target.y-p.Y,side=-dx*Math.sin(yaw)+dy*Math.cos(yaw);
        // Keep the chosen edge inside a small rear-axis dead band. Camera sway
        // must not alternate between opposite screen edges.
        if(!view.edgeSide || Math.abs(side)>Math.max(100,Math.hypot(dx,dy)*.08))view.edgeSide=side<0?-1:1;
        x=view.edgeSide<0?0:width;y=height*.6;
    }
    if(!Number.isFinite(x) || !Number.isFinite(y)){d.visible(view.box,false);view.motion=undefined;return;}
    const edge=x<118 || x>width-118 || y<45 || y>height-145;
    if(front && edge)view.edgeSide=x<width*.5?-1:1;
    x=Math.max(118,Math.min(width-118,x));y=Math.max(45,Math.min(height-145,y));
    const previous=view.motion;
    // A visible world marker must stay on its world point, with no screen-space
    // interpolation trailing the camera. Only an edge-pinned guide is eased.
    if(edge && previous?.edge && deltaSeconds>0 && deltaSeconds<.2 && Math.abs(x-previous.x)<width*.6) {
        const blend=1-Math.exp(-deltaSeconds/.045);
        x=previous.x+(x-previous.x)*blend;y=previous.y+(y-previous.y)*blend;
    }
    view.motion={x,y,edge};
    // Move the render transform, leaving the canvas layout origin unchanged.
    view.box.widget.SetRenderTranslation(new UE.Vector2D(x,y));
    d.visible(view.box,true);
    d.textValue(view.distance,(edge?text.offscreen+" · ":"")+view.distanceMeters+" m");
}
function drawFrame(frame,geometry,deltaSeconds) {
    try {
        const UE=require("ue"),view=frame[SLOT];
        if(!view || view.disposed || view.generation!==viewGeneration || paused || timer===undefined || !model?.config.visibility){retire(frame,UE,"inactive-generation");return;}
        const current=owner(UE);
        if(!current?.hud || !UE.KismetSystemLibrary.IsValid(current.hud) || current.key!==view.ownerKey || !attached(view,current)){retire(frame,UE,"owner-changed");return;}
        if(!presentationAllowed(current,UE)){retire(frame,UE,"paused-or-hidden");return;}
        current.pc[SLOT]=current.hud[SLOT]=view;
        if(!view.frameObserved) {
            view.frameObserved=true;
            Diagnostics.record("hud-frame-renderer",{source:"widget-tick",target:target?.id || "",owner:current.key});
        }
        renderMarker(view,current,geometry,deltaSeconds,UE);
    } catch(error) {fail(error);}
}
function fail(error) {
    Diagnostics.record("hud-error",{error:String(error),stack:String(error.stack||"").slice(0,1200)});
    suspend();try{detach();}catch{}
}
function syncAutomatic(context,player) {
    if(!model?.config.autoCollect || !model.catalogReady || !player)return;
    automaticDirty=false;lastAutomatic=Date.now();automaticMap=player.mapID;
    const explored=p=>coverage?.visible(p) || false;
    const current=model.points.find(p=>p.id===target?.id);
    if(current && target?.state==="unknown")current.state="unknown";
    if(current && AutoCollect.eligible(current,player,model.config,explored))return;
    // Distance selects a destination, never a path. Keep it until its native
    // completion/availability changes, so walking does not churn the target.
    if(target)stop();
    const UE=require("ue");
    for(let budget=0;budget<4;budget++) {
        const point=AutoCollect.nearest(model.points,player,model.config,explored);
        if(!point)break;
        if(itemCompleted(context,UE,point)){model.points=model.points.filter(p=>p.id!==point.id);continue;}
        if(!AutoCollect.eligible(point,player,model.config,explored))continue;
        select(context,point,text);Diagnostics.record("auto-collect-target",{id:point.id,mapID:point.mapID});break;
    }
    automaticDirty=false;
}
function autoStatus() {
    const active=!!model?.config.autoCollect;
    return {active,tracked:!!target,status:active?(model.catalogReady?(target?text.autoCollectRunning:text.autoCollectWaiting):text.collectionScanning):text?.autoCollectStart};
}
function publish(page,next,language) {
    const UE=require("ue");next.profile=next.profile || require("./Store").profile(UE,page);
    if(model?.profile!==next.profile){target=undefined;coverage=undefined;}
    if(!coverage)coverage=new Exploration(next.coverage?.records || [],next.coverage?.visits || []);
    else coverage.refresh(next.coverage?.records || []);
    for(const p of next.coverage?.visits||[])coverage.visit(p);
    model={profile:next.profile,region:next.region,points:next.points.filter(p=>p.state!=="collected").map(p=>({...p,nodes:(p.nodes||[]).slice()})),facilities:(next.facilities || []).map(p=>({...p})),catalogReady:next.catalogReady,config:{...next.config}};text=language;automaticDirty=true;
    if(target && target.category!=="quest") {
        const point=model.points.find(p=>p.id===target.id);
        if(point)target=copy(point,target.native);
    }
    if(!target && !model.config.autoCollect) {
        const saved=TrackingState.read(model.profile);
        if(saved) {
            const point=model.points.find(p=>p.id===saved.id) || (saved.questKey && model.points.find(p=>p.questKey===saved.questKey));
            if(point){select(page,point,language,point.category==="quest" && require("./NativeTracking").track(page,UE,point));Diagnostics.record("tracking-restored",{id:target.id});}
            else if(saved.category!=="quest")select(page,{...saved,label:require("./SourceState").propName(UE,saved.reward)||language[saved.category]},language);
        }
    }
}
function helperEvents(context,UE) {return require("../Library/TsHelper").default.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());}
function select(page,point,language,native=false) {stop(false);target=copy(point,native);text=language;lastState=0;
    if(!native) {
        const narrative=require("ue").PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page);
        if(narrative?.GetTrackedQuest()){narrative.ClearTrackedPoint();page.refreshTrackedHighlight?.();}
    }
    TrackingState.write(model?.profile || require("./Store").profile(require("ue"),page),target);
    Diagnostics.record("hud-target",{id:target.id,native,mapID:target.mapID});}
function mapOpened(){paused=true;suspend();detach();}
function mapClosed(reason) {
    if(!["finish","hidden","menu-exit","world-changed","slate-release"].includes(reason)){suspend();return;}
    paused=false;ownerKey="";
    Diagnostics.record("hud-resume",{reason,target:target?.id || "",minimap:!!model?.config.minimap,visible:!!model?.config.visibility,profile:!!model?.profile});
    if(model?.config.visibility && ((target && !target.native) || model.config.minimap || model.config.autoCollect) && timer===undefined)timer=setInterval(()=>{
        // Collection state updates are independent of marker rendering.
        try{const UE=require("ue");update(owner(UE),Date.now(),UE);}catch(error){fail(error);}
    },80);
}
module.exports={publish,select,stop,mapOpened,mapClosed,syncAutomatic,autoStatus,currentID:()=>target?.id || "",current:()=>target?copy(target,target.native):undefined,
    saved:profile=>TrackingState.read(profile),visits:profile=>model?.profile===profile?coverage?.visits || []:[]};
