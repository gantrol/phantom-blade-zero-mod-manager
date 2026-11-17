"use strict";
const Diagnostics=require("./Diagnostics"),Atlas=require("./OfflineAtlas");
const {Exploration}=require("./Exploration"),{CanvasDrawing}=require("./CanvasDrawing"),{PlanarMap}=require("./PlanarMap"),{rgba}=require("./Appearance");
const SLOT="__phantomBladeMapGuide";
const TrackingState=require("./TrackingState");
const {located}=require("./QuestObjectives");
const Availability=require("./CollectionAvailability");
const Conditions=require("./Conditions");
const CollectionState=require("./CollectionState");
const AutoCollect=require("./AutoCollect"),Filter=require("./CollectibleFilter");
const PlayerContext=require("./PlayerContext");
const NativeLocks=require("./NativeLocks");
const {SpaceContext}=require("./SpaceContext");
const {Navigation,describe:routeDescription}=require("./Navigation");
const navigation=new Navigation();
const spaceContext=new SpaceContext();
const {AutoReachability}=require("./AutoReachability");
const autoReachability=new AutoReachability();
const {CollectionRefresh}=require("./CollectionRefresh");
const {PointIndex}=require("./PointIndex");
const {TraversalMarkers}=require("./TraversalMarkers");
const CollectionEvents=require("./CollectionEvents");
let collectionUpdates,facilityIndex,collectionWatch,collectionOwner,locksDirty=true;
let traversal,traversalText;
const catalogWorlds=new WeakMap();
const world=id=>Atlas.aliases[String(id).toLowerCase()] || String(id).toLowerCase();
let target,model,text,coverage,atlas,timer,paused=true,ownerKey="",lastState=0,lastMini=0,desiredFloor=-1,floorSince=0,currentFloor=-1;
let installed=false;
let checkProfile=true,frameClass,viewGeneration=0;
let lastPhase="",lastFog=0,lastLanguageCheck=0;
let automaticDirty=true,lastAutomatic=0,automaticMap="",lastAutoPickupAt=0,autoAdvanceUntil=0;

function phase(value){if(value!==lastPhase){lastPhase=value;Diagnostics.record("hud-state",{state:value,target:target?.id || ""});}}
function owner(UE,identify=true) {
    const context=UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
    if(!context || !UE.KismetSystemLibrary.IsValid(context))return;
    const pc=UE.GameplayStatics.GetPlayerController(context,0);
    if(!pc || !UE.KismetSystemLibrary.IsValid(pc))return;
    const pawn=UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context) || UE.GameplayStatics.GetPlayerCharacter(context,0),hud=UE.PBZHUDLayout.GetHUDLayout(context);
    const key=identify?[pc,pc.GetWorld(),pawn,hud].map(p=>p && UE.KismetSystemLibrary.IsValid(p)?String(UE.KismetSystemLibrary.GetPathName(p)):"").join("|"):undefined;
    return {context,pc,key,hud,pawn};
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
    // Story sequences can run without enabling the controller's cinematic mode.
    // Keep the state timer alive so the map returns when the sequence finishes.
    const sequence=require("../Library/TsHelper").default.getWorldSubsystem(context,UE.PBZLevelSequenceManager.StaticClass());
    if(sequence && UE.KismetSystemLibrary.IsValid(sequence) && sequence.IsInSequence())return false;
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
function releaseData() {atlas?.dispose();atlas=undefined;traversal?.dispose();traversal=traversalText=undefined;coverage?.clearProjection();currentFloor=desiredFloor=-1;spaceContext.reset();navigation.clear();}
function updateTraversal(context,mapID,UE,helper) {
    if(!model.config.minimap || !model.config.showTraversal)return;
    if(!traversal || !traversal.allowed.has(mapID.toLowerCase()) || traversalText!==text) {
        traversal?.dispose();
        const currentWorld=world(mapID);
        const mapIDs=[mapID,currentWorld,...Object.keys(Atlas.aliases).filter(id=>Atlas.aliases[id]===currentWorld)];
        traversal=new TraversalMarkers(mapIDs);traversalText=text;
    }
    const count=traversal.points.length;
    // The HUD resolves the current world's authored points in bounded batches,
    // including when no map page has been opened or its scan was interrupted.
    traversal.step(context,UE,helper,text,()=>false);
    if(model.facilities!==traversal.points || count!==traversal.points.length) {
        model.facilities=traversal.points;facilityIndex=new PointIndex(model.facilities);lastMini=0;
    }
}
function detach() {const UE=require("ue"),current=owner(UE);if(current)remove(current,UE);}
function stop(persist=true) {
    if(persist && model?.profile)TrackingState.write(model.profile,undefined);
    target=undefined;automaticDirty=true;lastMini=0;navigation.clear();
    // Changing a destination must not destroy the minimap and its tile cache.
    if(!model?.config.minimap){viewGeneration++;detach();return;}
    const UE=require("ue"),current=owner(UE),view=current?.hud && currentView(current,UE);
    if(view) {
        view.markerEnabled=false;view.targetPoint=view.motion=undefined;
        for(const node of [view.box,view.detailBg,view.detailRule,view.detail])view.drawing.visible(node,false);
    }
}
function suspend(keepCollection=false) {
    viewGeneration++;if(timer!==undefined)clearInterval(timer);timer=undefined;releaseData();
    if(!keepCollection){collectionWatch?.();collectionWatch=collectionOwner=undefined;}
}
function resetModel(config,profile="",gameTime=0) {
    releaseData();autoReachability.clear();target=undefined;coverage=new Exploration();
    model={profile,gameTime,region:"",points:[],facilities:[],locks:[],catalogReady:false,config:{...config}};
    collectionUpdates=new CollectionRefresh(model.points);facilityIndex=new PointIndex();locksDirty=true;
    automaticDirty=true;lastAutomatic=lastMini=lastState=lastFog=0;automaticMap="";
}
function resume() {
    if(paused || !model?.config.visibility || timer!==undefined)return;
    if(!model.config.minimap && !model.config.autoCollect && (!target || target.native && target.category!=="quest"))return;
    timer=setInterval(()=>{
        try{const UE=require("ue");update(owner(UE),Date.now(),UE);}catch(error){fail(error);}
    },80);
}
function install() {
    if(installed)return;
    const UE=require("ue"),settings=require("./Settings").open(UE,require("puerts"));
    text=require("./Locales").dictionary(UE);
    resetModel(settings.read().values);paused=false;installed=true;
    settings.subscribe(snapshot=>{
        model.config={...snapshot.values};automaticDirty=true;lastMini=0;
        if(!model.config.visibility || !model.config.minimap && !model.config.autoCollect && !target){suspend();detach();}
        else resume();
    });
    Diagnostics.record("hud-startup",{minimap:model.config.minimap,visible:model.config.visibility});
    resume();
}
function loadingStatus(view,width,UE,message,progress,stage="initializing") {
    const d=view.drawing,miniWidth=Math.round(Math.min(280,width*.23)),left=width-miniWidth-28,top=56;
    d.place(view.loadBg,left+8,top+32);view.loadBg.slot.SetSize(new UE.Vector2D(miniWidth-16,42));
    d.place(view.loadStatus,left+14,top+34);view.loadStatus.slot.SetSize(new UE.Vector2D(miniWidth-28,20));
    d.place(view.loadBar,left+14,top+58);view.loadBar.slot.SetSize(new UE.Vector2D(miniWidth-28,6));
    view.loadBar.widget.SetIsMarquee(!!message && progress===undefined);
    if(progress!==undefined)view.loadBar.widget.SetPercent(Math.max(0,Math.min(1,progress)));
    d.textValue(view.loadStatus,message);
    for(const node of [view.loadBg,view.loadStatus,view.loadBar])d.visible(node,!!message);
    const state=message?stage:"ready";
    if(view.loadStage!==state) {
        view.loadStage=state;
        Diagnostics.record("minimap-loading",{state,mapID:view.mapID || "",progress,catalogReady:!!model?.catalogReady,
            icons:view.map.pinByID.size,pendingIcons:view.map.pinJobs.size});
    }
}
function loadingFrame(view,width,UE,message=text.minimapLoading,stage="initializing") {
    if(!model.config.minimap)return;
    const d=view.drawing,miniWidth=Math.round(Math.min(280,width*.23)),left=width-miniWidth-28,top=56;
    view.map.bounds(left,top,miniWidth,miniWidth);d.visible(view.map.root,true);
    view.heading.slot.SetSize(new UE.Vector2D(Math.max(80,miniWidth-38),20));d.place(view.heading,left+10,top+4);
    d.textValue(view.heading,text.minimap);d.visible(view.heading,true);
    for(const node of [view.box,view.detailBg,view.detailRule,view.detail])d.visible(node,false);
    loadingStatus(view,width,UE,message,undefined,stage);
}
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
    lastMini=0;
    Diagnostics.record("hud-view-created",{generation:view.generation,owner:ownerKey,host:view.host});
    view.map=new PlanarMap(drawing,root,32);
    view.loadBg=drawing.makeRect(root,0,0,1,22,...rgba("ink",.88));view.loadBg.slot.SetZOrder(26);
    view.loadStatus=drawing.makeText(root,11,0,0,240,20,rgba("gold"));view.loadStatus.slot.SetZOrder(27);
    const bar=drawing.create(UE.ProgressBar,root);
    view.loadBar={widget:bar,slot:UE.WidgetLayoutLibrary.SlotAsCanvasSlot(bar)};view.loadBar.slot.SetZOrder(27);
    bar.SetFillColorAndOpacity(new UE.LinearColor(...rgba("gold")));
    for(const node of [view.loadBg,view.loadStatus,view.loadBar])drawing.visible(node,false);
    view.detailBg=drawing.makeRect(root,0,0,1,1,...rgba("ink",.68));view.detailBg.slot.SetZOrder(24);
    view.detailRule=drawing.makeRect(root,0,0,2,1,...rgba("gold",.68));view.detailRule.slot.SetZOrder(25);
    view.heading=drawing.makeText(root,12,0,0,240,20,rgba("paper"));view.detail=drawing.makeText(root,12,0,0,280,44,rgba("paper"));
    view.detail.widget.SetAutoWrapText(true);view.heading.slot.SetZOrder(25);view.detail.slot.SetZOrder(25);
    for(const node of [view.heading,view.detail]){node.widget.SetShadowOffset(new UE.Vector2D(1,1));node.widget.SetShadowColorAndOpacity(new UE.LinearColor(0,0,0,.9));}
    for(const node of [view.detailBg,view.detailRule,view.heading,view.detail])drawing.visible(node,false);
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
    value.nodes=(point.nodes||[]).slice();value.requirements=point.requirements?.map(p=>({...p}));return value;
}
function completed(context,UE) {
    const helper=require("../Library/TsHelper").default;
    if(target.category==="marker")return false;
    if(target.category==="quest") {
        const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context);
        const tracked=narrative?.GetTrackedQuest();
        if(target.native && tracked && String(UE.KismetSystemLibrary.GetPathName(tracked.GetClass()))!==target.questKey)return "changed";
        const quest=Array.from(narrative?.QuestList||[]).find(q=>q && String(UE.KismetSystemLibrary.GetPathName(q.GetClass()))===target.questKey);
        const chains=require("./QuestChains");
        if(!quest) {
            target=copy(chains.pending(target,false,"questState_unknown"));
            TrackingState.write(model.profile,target);return false;
        }
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(!events)return false;
        const points=require("./SideQuests").readQuest(quest,events,UE);
        let next=chains.next(points,target);
        if(!next) {
            if(quest.IsQuestComplete() || quest.QuestCompletion!==UE.EQuestCompletion.QC_Started)return true;
            next=chains.pending(target,points.length>1);
        }
        if(next.id!==target.id || next.pointKind!==target.pointKind || next.availability!==target.availability || next.x!==target.x || next.y!==target.y || next.z!==target.z) {
            const native=target.native && next.nativeTrack;
            if(native)narrative.SetTrackedPoint(quest,next.branch,next.taskPointID);
            // Missing MOD coordinates do not cancel the game's tracked quest.
            // Native tracking owns its own stage transitions and visibility.
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
    if(point.state==="collected")return true;
    const helper=require("../Library/TsHelper").default;
    if(point.category==="quest" || point.category==="marker")return false;
    if(CollectionState.supported(point)) {
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass());
        const game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass());
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(CollectionState.collected(point,{bag,cycle:game?.GetCycleCount(),events}))return true;
        if(point.category==="book" || require("./Treasures").contains(point.reward)) {
            const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
            if(manager)Availability.refreshSource(point,manager,UE);
            // Their acquisition records take precedence over a shared container.
            return false;
        }
    }
    if(point.flow) {
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager)Availability.refreshSource(point,manager,UE);
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
    if(point.domain && helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass())?.CompleteAddItemMobDomainNames.Contains(point.domain))return true;
    if(point.nodes?.length){const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());if(events && point.nodes.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined))return true;}
    return false;
}
function update(current,now,UE) {
    if(paused || !model || !model.config.visibility)return;
    const cpp=require("cpp");
    if(!current){if(ownerKey)viewGeneration++;phase("waiting-world");releaseData();collectionWatch?.();collectionWatch=collectionOwner=undefined;ownerKey="";return;}
    const {context,pc,hud,key}=current;
    if(key!==ownerKey){viewGeneration++;releaseData();remove(current,UE,"owner-changed");ownerKey=key;checkProfile=true;}
    if(!hud || !UE.KismetSystemLibrary.IsValid(hud)){phase("waiting-hud");return;}
    const helper=require("../Library/TsHelper").default,position=PlayerContext.read(context,UE,helper);
    if(!position || !presentationAllowed(current,UE)){phase("paused-or-hidden");remove(current,UE);return;}
    let view=currentView(current,UE);
    if(view && view.host!==(position.aboard?"boat":"hud")){remove(current,UE,"travel-mode-changed");view=undefined;}
    // MainStart runs before the game applies its saved language. Resolve it
    // again at the first visible HUD, and follow later in-game changes.
    if(!view || now-lastLanguageCheck>=1000) {
        lastLanguageCheck=now;
        const nextText=require("./Locales").dictionary(UE);
        if(text!==nextText){text=nextText;lastMini=0;Diagnostics.record("hud-language",{language:String(UE.KismetInternationalizationLibrary.GetCurrentLanguage())});}
    }
    view=view || create(current,position.aboard,UE,cpp);
    const d=view.drawing;
    if(d.text!==text){d.text=text;d.textValue(view.map.northBadge,text.dirN);}
    view.markerEnabled=false;
    const geometry=view.root.GetCachedGeometry(),size=UE.SlateBlueprintLibrary.GetLocalSize(geometry),width=size.X,height=size.Y;
    if(width<400 || height<300){d.visible(view.box,false);phase("waiting-layout");return;}
    const layoutKey=width+":"+height;
    if(view.layoutKey!==layoutKey){view.layoutKey=layoutKey;view.motion=undefined;Diagnostics.record("hud-layout",{target:target?.id || "",host:view.host,width,height,attached:attached(view,current)});}
    // Let Slate paint the placeholder before reading save journals, fog or atlas
    // metadata. Starting the HUD must not depend on opening the native map.
    if(!view.initialized || !view.frameObserved) {
        loadingFrame(view,width,UE);view.initialized=true;phase("loading-minimap");return;
    }
    if(checkProfile || now-lastState>1000) {
        checkProfile=false;
        const profile=require("./Store").profile(UE,context);
        const gameTime=Number(helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass())?.GetGameTime());
        // Loading an earlier checkpoint of this same character also invalidates
        // the point list. Keep polling through save loading instead of stopping
        // until the user next opens the map. Terrain does not need a save ID.
        if(profile!==model.profile || gameTime<model.gameTime) {
            resetModel(model.config,profile,gameTime);
            if(profile)publish(context,model,text);
            if(view.disposed)return;
            view.map.clear();view.targetPoint=view.motion=undefined;
            loadingFrame(view,width,UE);phase("loading-minimap");return;
        }
        model.gameTime=gameTime;
    }
    const map=helper.getWorldSubsystem(context,UE.MapSubsystem.StaticClass());
    // GetCurrentMapID only reads a cache. The native Map.GetCurrentMapID first
    // refreshes it from the player; without that call a fresh game returns None.
    // Refresh at a bounded rate, including travel across sub-map boundaries.
    if(map && now-(view.lastMapRefresh || 0)>=500) {
        map.UpdateCurrentMapIDByPlayer();view.lastMapRefresh=now;
    }
    const mapID=String(map?.GetCurrentMapID() || "");
    if(!mapID || mapID.toLowerCase()==="none") {
        if(!view.waitingMap){view.map.clear();view.waitingMap=true;}
        loadingFrame(view,width,UE,text.minimapWaitingArea,"area");phase("waiting-map");return;
    }
    view.waitingMap=false;
    if(view.mapID!==mapID){view.mapID=mapID;Diagnostics.record("hud-map",{mapID,source:"player-refresh"});}
    if(collectionOwner!==context.GetWorld()) {
        collectionWatch?.();collectionOwner=context.GetWorld();
        collectionWatch=CollectionEvents.watch(context,change=>{
            collectionUpdates?.invalidate(change,undefined,target?.id);
            if(change.reasons.includes("locks") || change.reasons.includes("granted"))locksDirty=true;
            if(change.reasons.includes("bell"))lastFog="";
        });
        if(model.catalogReady)collectionUpdates.invalidate({reasons:["resume"],items:[],inventory:true,
            positions:[{x:position.x,y:position.y,z:position.groundZ,mapID}]},undefined,target?.id);
        locksDirty=true;
    }
    if(locksDirty && (model.config.minimap || model.config.autoCollect)) {
        model.locks=NativeLocks.read(context,undefined,UE,text);locksDirty=false;
        autoReachability.updateLocks(model.locks);
    }
    if(collectionUpdates && model.catalogReady) {
        const changed=collectionUpdates.step(context,UE,helper);
        if(changed.length) {
            automaticDirty=true;lastMini=0;
            const updated=changed.find(p=>p.id===target?.id);
            if(updated?.state==="collected") {
                if(model.config.autoCollect)lastAutoPickupAt=now;
                stop();
            } else if(updated)target=copy(updated,target.native);
        }
    }
    // Quest guidance has a separate lifecycle from collectible acquisition.
    if(now-lastState>1000) {
        lastState=now;
        const done=target?.category==="quest" && completed(context,UE);
        if(done){
            if(done===true){model.points=model.points.filter(p=>p.id!==target.id);collectionUpdates=new CollectionRefresh(model.points,collectionUpdates.pending);}
            stop();
        }
    }
    if(!model.config.minimap && !model.config.autoCollect && !target){
        if(target?.category!=="quest")suspend();
        remove(current,UE);return;
    }
    const {x,y,z,groundZ,yaw,aboard}=position,player={x,y,z,groundZ,yaw,aboard,mapID};coverage.visit(player);
    const fogKey=[mapID,Math.floor(x/1600),Math.floor(y/1600)].join(":");
    if(model.config.exploredOnly && lastFog!==fogKey) {
        lastFog=fogKey;coverage.refresh(require("./Fog").records(context,UE));
    }
    if(!atlas || atlas.world!==world(mapID)){releaseData();atlas=Atlas.open(mapID);}
    if(atlas)atlas.mapID=mapID;
    atlas?.step();
    updateTraversal(context,mapID,UE,helper);
    spaceContext.update(atlas,player,now);
    const actualFloor=atlas?.floor(player)??-1;
    if((actualFloor>=0 || !atlas?.pending) && actualFloor!==desiredFloor){desiredFloor=actualFloor;floorSince=now;}
    if(currentFloor<0 || now-floorSince>450)currentFloor=desiredFloor;
    player.layer=currentFloor;
    if(automaticDirty || autoReachability.pendingID || automaticMap!==mapID || now-lastAutomatic>1000)syncAutomatic(context,player);
    if(view.disposed)return;
    const local=located(target) && world(target.mapID)===world(mapID),config=model.config;
    const distance=local?Math.round(Math.hypot(target.x-player.x,target.y-player.y,target.z-player.z)/100):undefined;
    const route=navigation.update(context,player,target,atlas,model.locks,UE,now,config.fineNavigation);
    const guidance=routeDescription(route,text);
    phase("laid-out");
    const nonSpatial=target && (target.availability && !Conditions.pending(target) || !located(target) || !local);
    const autoFlash=!!(model.config.autoCollect && target && now<autoAdvanceUntil);
    if(view.autoFlash!==autoFlash) {
        view.autoFlash=autoFlash;
        const c=rgba(autoFlash?"gold":"paper");
        view.detail.widget.SetColorAndOpacity(d.color(c[0],c[1],c[2],c[3]??1));
    }
    if(config.minimap) {
        view.heading.widget.SetJustification(UE.ETextJustify.Left);view.detail.widget.SetJustification(UE.ETextJustify.Left);
        const miniWidth=Math.round(Math.min(280,width*.23)),left=width-miniWidth-28,top=56;
        view.map.bounds(left,top,miniWidth,miniWidth);
        view.heading.slot.SetSize(new UE.Vector2D(Math.max(80,miniWidth-38),20));d.place(view.heading,left+10,top+4);
        const layer=atlas?.layers[currentFloor];d.textValue(view.heading,atlas?(text["region_"+mapID.toLowerCase()] || "")+" · "+(layer?text[layer.kind]+(layer.depth?" "+layer.depth:""):text.floorUnknown):text.offlineMissing);
        d.visible(view.heading,true);d.visible(view.detailBg,!!target);d.visible(view.detailRule,!!target);d.visible(view.detail,!!target);
        if(now-lastMini>=160) {
            lastMini=now;
            const desiredCm=spaceContext.minimapScale(config.autoFloorPlan);
            view.minimapCm=(view.minimapCm ?? 50)+(desiredCm-(view.minimapCm ?? 50))*.25;
            const radius=miniWidth*view.minimapCm*.5+400;
            const nearby=q=>Math.abs(q.x-player.x)<=radius && Math.abs(q.y-player.y)<=radius;
            const nearbyPoints=collectionUpdates.index.rect(mapID,player.x,player.y,radius,radius)
                .concat(config.showTraversal?facilityIndex.rect(mapID,player.x,player.y,radius,radius):[]);
            const points=nearbyPoints.filter(q=>located(q) && world(q.mapID)===world(mapID) &&
                (q.id===target?.id || nearby(q)) &&
                (q.category==="bell" || Filter.visible(q,config) && (!config.remainingOnly || q.state!=="collected")));
            points.push(...model.locks.filter(q=>world(q.mapID)===world(mapID) && nearby(q)));
            for(const q of points)q.layer=atlas?.floor(q)??-1;
            if(target && local && !points.some(q=>q.id===target.id))points.unshift({...target,layer:atlas?.floor(target)??-1});
            coverage.sample(context,UE,()=>view.map.update(atlas,currentFloor,{...player,cm:view.minimapCm},player,points,local?target:undefined,coverage,config.exploredOnly,config.showFloors,config.showFog,local?target.id:"","",route));
        }
        const progress=view.map.textures.progress;
        const fraction=progress.total?progress.ready/progress.total:0;
        if(!atlas)loadingStatus(view,width,UE,text.offlineMissing,0,"unavailable");
        else if(atlas.surfaceFailed)loadingStatus(view,width,UE,text.floorDataFailed,0,"floor-failed");
        else if(atlas.pending)loadingStatus(view,width,UE,text.floorDataLoading,undefined,"floors");
        else if(progress.failed)loadingStatus(view,width,UE,text.minimapLoadRetry,fraction,"texture-retry");
        else if(progress.ready<progress.total)loadingStatus(view,width,UE,text.minimapLoading+" "+Math.floor(fraction*100)+"%",fraction,"textures");
        else if(view.map.pinJobs.size)loadingStatus(view,width,UE,text.mapIconsLoading,undefined,"icons");
        else loadingStatus(view,width,UE,"",1);
        if(target) {
            const detailTop=top+miniWidth+6;
            view.detailBg.slot.SetPosition(new UE.Vector2D(left,detailTop));view.detailBg.slot.SetSize(new UE.Vector2D(miniWidth,48));
            view.detailRule.slot.SetPosition(new UE.Vector2D(left,detailTop+4));view.detailRule.slot.SetSize(new UE.Vector2D(2,40));
            view.detail.slot.SetSize(new UE.Vector2D(Math.max(80,miniWidth-16),42));d.place(view.detail,left+10,detailTop+4);
            const detail=target.availability?Conditions.describe(target,text,UE,context):!located(target)?text.objectivePending:guidance || (local?text.straight+" "+distance+" m":text.targetOtherArea);
            const title=[autoFlash?text.autoAdvanced:"",target.label,text["objective_"+target.objectiveID]].filter(Boolean).join(" · ");
            d.textValue(view.detail,title+"\n"+detail);
        }
    } else {
        for(const node of [view.loadBg,view.loadStatus,view.loadBar])d.visible(node,false);
        view.map.clear();d.visible(view.heading,!!target);d.visible(view.detailBg,!!target);d.visible(view.detailRule,!!target);d.visible(view.detail,!!target);
        if(target) {
            const cardW=288,cardX=width-cardW-28,cardY=58;
            view.heading.widget.SetJustification(UE.ETextJustify.Right);view.detail.widget.SetJustification(UE.ETextJustify.Right);
            view.detailBg.slot.SetPosition(new UE.Vector2D(cardX,cardY));view.detailBg.slot.SetSize(new UE.Vector2D(cardW,66));
            view.detailRule.slot.SetPosition(new UE.Vector2D(cardX+cardW-2,cardY+6));view.detailRule.slot.SetSize(new UE.Vector2D(2,54));
            view.heading.slot.SetSize(new UE.Vector2D(cardW-20,22));d.place(view.heading,cardX+10,cardY+6);
            view.detail.slot.SetSize(new UE.Vector2D(cardW-20,36));d.place(view.detail,cardX+10,cardY+28);
            d.textValue(view.heading,target.label || text[target.category]);
            const statusLine=target.availability?Conditions.describe(target,text,UE,context):!located(target)?text.objectivePending:guidance || (local?text.straight+" "+distance+" m":
                text.targetOtherArea+" · "+(text["region_"+target.mapID.toLowerCase()] || target.mapID));
            d.textValue(view.detail,[autoFlash?text.autoAdvanced:"",statusLine].filter(Boolean).join(" · "));
        }
    }
    view.targetPoint=local && !nonSpatial?(route?.next || target):undefined;
    if(!target || target.native || !view.targetPoint){d.visible(view.box,false);view.motion=undefined;return;}
    view.markerEnabled=true;
    if(view.targetID!==target.id) {
        const name=target.category==="quest"?"T_Track_News":"T_Track_Collection",path="/Game/PBZContent/UI/InGame/Quest/"+name+"."+name;
        view.icon.SetBrushFromSoftTexture(UE.KismetSystemLibrary.Conv_SoftObjPathToSoftObjRef(UE.KismetSystemLibrary.MakeSoftObjectPath(path)));
        d.textValue(view.label,[target.label,text["objective_"+target.objectiveID]].filter(Boolean).join(" · "));view.targetID=target.id;view.motion=undefined;
    }
    d.textValue(view.label,route?.next?(route.next===route.transition?text.nextEntrance:text.routeNextStep):[target.label,text["objective_"+target.objectiveID]].filter(Boolean).join(" · "));
    view.distanceMeters=route?.next?Math.round(Math.hypot(route.next.x-player.x,route.next.y-player.y,route.next.z-player.z)/100):distance;
    const dz=view.targetPoint.z-player.z;
    d.textValue(view.elevation,text[dz>200?"above":dz< -200?"below":"sameLevel"]);
}
function renderMarker(view,current,geometry,deltaSeconds,UE) {
    if(!target || !view.targetPoint || !view.markerEnabled || view.targetID!==target.id)return;
    const point=view.targetPoint;
    const {pc}=current,d=view.drawing,puerts=require("puerts");
    const size=UE.SlateBlueprintLibrary.GetLocalSize(geometry),width=size.X,height=size.Y;
    if(width<400 || height<300){d.visible(view.box,false);view.motion=undefined;return;}
    // Project into full-viewport pixels, then convert once into THIS frame's
    // geometry. Player-relative coordinates and cached geometries can disagree
    // with the HUD's viewport origin/scale during camera and layout updates.
    const screen=puerts.$ref(new UE.Vector2D()),front=pc.ProjectWorldLocationToScreen(new UE.Vector(point.x,point.y,point.z),screen,false);
    let x,y;
    if(front) {
        const local=puerts.$ref(new UE.Vector2D());
        UE.SlateBlueprintLibrary.ScreenToWidgetLocal(pc,geometry,puerts.$unref(screen),local,false);
        const q=puerts.$unref(local);x=q.X;y=q.Y;
    } else {
        const camera=pc.PlayerCameraManager;
        if(!camera || !UE.KismetSystemLibrary.IsValid(camera)){d.visible(view.box,false);view.motion=undefined;return;}
        const p=camera.GetCameraLocation(),yaw=camera.GetCameraRotation().Yaw*Math.PI/180;
        const dx=point.x-p.X,dy=point.y-p.Y,side=-dx*Math.sin(yaw)+dy*Math.cos(yaw);
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
        const current=owner(UE,false);
        if(!current?.hud || !UE.KismetSystemLibrary.IsValid(current.hud) || frame.GetOwningPlayer()!==current.pc || !attached(view,current)){retire(frame,UE,"owner-changed");return;}
        if(!presentationAllowed(current,UE)){retire(frame,UE,"paused-or-hidden");return;}
        current.pc[SLOT]=current.hud[SLOT]=view;
        if(!view.frameObserved) {
            view.frameObserved=true;
            Diagnostics.record("hud-frame-renderer",{source:"widget-tick",target:target?.id || "",owner:current.key});
        }
        if(model.config.minimap && current.pawn && UE.KismetSystemLibrary.IsValid(current.pawn)) {
            let position;
            if(view.host==="boat")position=PlayerContext.read(current.context,UE,require("../Library/TsHelper").default);
            else {
                const location=current.pawn.K2_GetActorLocation();
                position={x:location.X,y:location.Y,yaw:current.pawn.K2_GetActorRotation().Yaw};
            }
            view.map.follow(position);
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
    autoReachability.bind(context);
    autoReachability.updateLocks(model.locks);
    const scanning=autoReachability.beginScan(player);
    automaticDirty=false;lastAutomatic=Date.now();automaticMap=player.mapID;
    const explored=p=>coverage?.visible(p) || false;
    const current=model.points.find(p=>p.id===target?.id);
    if(current && current.state!=="collected" && target?.state==="unknown")current.state="unknown";
    const UE=require("ue");
    if(current && AutoCollect.eligible(current,player,model.config,explored)) {
        if(autoReachability.check(context,current,player,UE,model.locks)!==false)return;
    }
    // Keep confirmed targets while walking, but discard a partial/blocked route.
    if(target){if(current?.state==="collected")lastAutoPickupAt=Date.now();stop();}
    if(!scanning)return;
    // Streaming can make a nearby failed route available while the player is
    // standing still. Reserve one retry every five seconds without restarting
    // the forward scan or starving candidates farther down a large catalog.
    let retry;
    if(!autoReachability.pendingID && Date.now()>=autoReachability.nextRetryAt && Date.now()>=autoReachability.nextQuery) {
        autoReachability.nextRetryAt=Date.now()+5000;
        retry=AutoCollect.nearest(model.points,player,model.config,explored,p=>
            autoReachability.scanned.has(p.id) && autoReachability.cached(p,player)===undefined);
    }
    for(let budget=0;budget<4;budget++) {
        const pending=model.points.find(p=>p.id===autoReachability.pendingID && AutoCollect.eligible(p,player,model.config,explored));
        if(!pending)autoReachability.pendingID="";
        const point=pending || retry || AutoCollect.nearest(model.points,player,model.config,explored,p=>
            !autoReachability.scanned.has(p.id) && autoReachability.cached(p,player)!==false);
        retry=undefined;
        // A pass outlives the short result cache. Expiring nearby failures must
        // not starve more distant candidates. Retry only after exhausting it.
        if(!point){autoReachability.finishScan();break;}
        if(point.state==="collected")continue;
        if(!AutoCollect.eligible(point,player,model.config,explored))continue;
        const reachable=autoReachability.check(context,point,player,UE,model.locks);
        if(reachable===undefined)break;
        if(!reachable)continue;
        const now=Date.now();if(now-lastAutoPickupAt<2500){autoAdvanceUntil=now+2000;lastAutoPickupAt=0;}
        select(context,point,text);Diagnostics.record("auto-collect-target",{id:point.id,mapID:point.mapID});break;
    }
    automaticDirty=false;
}
function autoStatus() {
    const active=!!model?.config.autoCollect;
    return {active,tracked:!!target,pending:active && !!autoReachability.pendingID,status:active?(model.catalogReady?(target?text.autoCollectRunning:text[autoReachability.status]):text.collectionScanning):text?.autoCollectStart};
}
function catalogSnapshot(context,region,profile,mapIDs) {
    if(!profile || !collectionWatch || !model?.catalogReady || model.profile!==profile || model.region!==region ||
        catalogWorlds.get(context.GetWorld())!==model)return;
    const UE=require("ue"),helper=require("../Library/TsHelper").default;
    const signature=ids=>ids.map(id=>String(id).toLowerCase()).sort().join("|");
    if(signature(mapIDs)!==signature(model.mapIDs || []) ||
        model.language!==String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()))return;
    const game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass()),now=Number(game?.GetGameTime());
    if(!Number.isFinite(now) || now<model.gameTime)return;
    return {mapIDs:model.mapIDs.slice(),points:model.points.filter(p=>p.category!=="quest" && p.category!=="marker")
        .map(p=>({...p,nodes:(p.nodes || []).slice(),requirements:p.requirements?.map(r=>({...r}))})),pending:[...collectionUpdates.pending]};
}
function publish(page,next,language) {
    const UE=require("ue");next.profile=next.profile || require("./Store").profile(UE,page);
    if(model?.profile!==next.profile){target=undefined;coverage=undefined;autoReachability.clear();}

    if(!coverage)coverage=new Exploration(next.coverage?.records || [],next.coverage?.visits || []);
    else coverage.refresh(next.coverage?.records || []);
    for(const p of next.coverage?.visits||[])coverage.visit(p);
    const game=require("../Library/TsHelper").default.getWorldSubsystem(page,UE.PBZGameSystem.StaticClass());
    model={profile:next.profile,gameTime:Number(game?.GetGameTime()),region:next.region,points:next.points.filter(Filter.hasContent).map(p=>({...p,nodes:(p.nodes||[]).slice()})),facilities:(next.facilities || []).map(p=>({...p})),locks:(next.locks || []).map(p=>({...p})),catalogReady:next.catalogReady,config:{...next.config}};text=language;automaticDirty=true;
    model.mapIDs=(next.mapIDs || []).slice();model.language=String(UE.KismetInternationalizationLibrary.GetCurrentLanguage());
    catalogWorlds.set(page.GetWorld(),model);
    const pending=[...(collectionUpdates?.pending || []),...(next.pending || [])];
    collectionUpdates=new CollectionRefresh(model.points,pending);facilityIndex=new PointIndex(model.facilities);

    if(model.config.questGuidance==="explore" && target?.category==="quest")stop();
    if(target && target.category!=="quest") {
        const point=next.points.find(p=>p.id===target.id);
        if(point?.state==="collected")stop();
        else if(point)target=copy(point,target.native);
        else if(!Filter.hasContent(target))stop();
    }
    if(!target && !model.config.autoCollect) {
        const saved=TrackingState.read(model.profile);
        if(saved && !(saved.category==="quest" && model.config.questGuidance==="explore")) {
            // Use the full catalog: filtering completed entries first makes an
            // old journal target look absent and resurrects its stale metadata.
            const point=saved.category==="quest"?require("./QuestChains").next(next.points,saved):next.points.find(p=>p.id===saved.id);
            if(point?.state==="collected")TrackingState.write(model.profile,undefined);
            else if(point){select(page,point,language,point.category==="quest" && require("./NativeTracking").track(page,UE,point));Diagnostics.record("tracking-restored",{id:target.id});}
            else if(!Filter.hasContent(saved))TrackingState.write(model.profile,undefined);
            else if(saved.category!=="quest" && !itemCompleted(page,UE,saved))select(page,{...saved,label:require("./SourceState").propName(UE,saved.reward)||language[saved.category]},language);
        }
    }
}
function select(page,point,language,native=false) {
    if(!Filter.hasContent(point))return;
    stop(false);target=copy(point,native);text=language;lastState=0;
    // Custom and automatic collection guidance must not erase native tracking.
    TrackingState.write(model?.profile || require("./Store").profile(require("ue"),page),target);
    Diagnostics.record("hud-target",{id:target.id,native,mapID:target.mapID});}
function mapOpened(){paused=true;suspend(true);detach();}
function mapClosed(reason) {
    if(!["finish","hidden","menu-exit","world-changed","slate-release"].includes(reason)){suspend();return;}
    paused=false;ownerKey="";
    Diagnostics.record("hud-resume",{reason,target:target?.id || "",minimap:!!model?.config.minimap,visible:!!model?.config.visibility,profile:!!model?.profile});
    resume();
}
module.exports={install,publish,select,stop,mapOpened,mapClosed,syncAutomatic,autoStatus,catalogSnapshot,currentID:()=>target?.id || "",current:()=>target?copy(target,target.native):undefined,
    saved:profile=>TrackingState.read(profile),visits:profile=>model?.profile===profile?coverage?.visits || []:[]};
