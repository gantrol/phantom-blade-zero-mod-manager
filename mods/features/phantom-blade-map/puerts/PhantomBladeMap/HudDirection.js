"use strict";
const Diagnostics = require("./Diagnostics");
const SLOT="__phantomBladeMapGuide";
let target, timer, paused=true, controllerPath="", worldPath="", lastStateRead=0, text;

// Only an explicitly selected target starts this timer. Global state contains
// primitives: every update reacquires the current controller and native HUD.
// Widgets belong to that HUD's tree and are released with it during travel.
function remove(hud,UE) {
    if(!hud || !UE.KismetSystemLibrary.IsValid(hud))return;
    const view=hud?.[SLOT];
    if(!view)return;
    hud[SLOT]=undefined;
    if(view.icon && UE.KismetSystemLibrary.IsValid(view.icon))view.icon.SetBrushFromTexture(undefined);
    if(UE.KismetSystemLibrary.IsValid(view.root))view.root.RemoveFromParent();
}
function current(UE) {
    const context=UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
    if(!context || !UE.KismetSystemLibrary.IsValid(context))return undefined;
    const pc=UE.GameplayStatics.GetPlayerController(context,0);
    if(!pc || !UE.KismetSystemLibrary.IsValid(pc) || String(UE.KismetSystemLibrary.GetPathName(pc))!==controllerPath ||
        String(UE.KismetSystemLibrary.GetPathName(pc.GetWorld()))!==worldPath)return undefined;
    return {context,pc,hud:UE.PBZHUDLayout.GetHUDLayout(context)};
}
function stop(detach=true) {
    if(timer!==undefined)clearInterval(timer);
    timer=undefined;target=undefined;
    try { if(detach) {
        const UE=require("ue"),owner=current(UE);
        if(owner?.hud)remove(owner.hud,UE);
    } } finally { controllerPath="";worldPath=""; }
}
function create(hud,UE,cpp) {
    const parent=hud.WidgetTree.RootWidget;
    const root=UE.NewObject(UE.CanvasPanel.StaticClass(),hud);
    const view={root};hud[SLOT]=view;
    parent.AddChild(root);root.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    const rootSlot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(root);
    if(rootSlot) {
        rootSlot.SetAnchors(new UE.Anchors(new UE.Vector2D(0,0),new UE.Vector2D(1,1)));
        rootSlot.SetOffsets(new UE.Margin(0,0,0,0));rootSlot.SetZOrder(15);
    } else {
        const overlay=UE.WidgetLayoutLibrary.SlotAsOverlaySlot(root);
        if(overlay){overlay.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill);overlay.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);}
    }
    const box=UE.NewObject(UE.CanvasPanel.StaticClass(),hud);root.AddChild(box);
    const boxSlot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(box);boxSlot.SetSize(new UE.Vector2D(200,100));boxSlot.SetAlignment(new UE.Vector2D(.5,0));
    const icon=UE.NewObject(UE.Image.StaticClass(),hud);box.AddChild(icon);
    view.icon=icon;
    const iconSlot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(icon);iconSlot.SetPosition(new UE.Vector2D(80,0));iconSlot.SetSize(new UE.Vector2D(40,40));
    const texture=target.category==="quest"?"T_Track_News":"T_Track_Collection";
    const path="/Game/PBZContent/UI/InGame/Quest/"+texture+"."+texture;
    const soft=UE.KismetSystemLibrary.Conv_SoftObjPathToSoftObjRef(UE.KismetSystemLibrary.MakeSoftObjectPath(path));
    icon.SetBrushFromSoftTexture(soft);
    function line(y,size) {
        const widget=UE.NewObject(UE.TextBlock.StaticClass(),hud);box.AddChild(widget);
        const slot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);slot.SetPosition(new UE.Vector2D(0,y));slot.SetSize(new UE.Vector2D(200,24));
        const font=widget.Font;font.Size=size;widget.SetFont(font);widget.SetJustification(UE.ETextJustify.Center);
        const color=new UE.SlateColor();color.SpecifiedColor=new UE.LinearColor(.9,.82,.51,1);widget.SetColorAndOpacity(color);
        widget.SetShadowOffset(new UE.Vector2D(1,1));widget.SetShadowColorAndOpacity(new UE.LinearColor(0,0,0,.9));
        return widget;
    }
    const distance=line(40,14),elevation=line(62,11),label=line(82,11);
    label.SetText(cpp.FText.FromString(target.label));
    Object.assign(view,{boxSlot,distance,elevation});
    return view;
}
function completed(context,UE) {
    const helper=require("../Library/TsHelper").default;
    if(target.category==="quest") {
        const narrative=UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(context);
        for(const q of narrative?.QuestList || []) {
            if(!q || String(UE.KismetSystemLibrary.GetPathName(q.GetClass()))!==target.questKey)continue;
            if(q.QuestCompletion!==UE.EQuestCompletion.QC_Started || q.IsQuestBlocked())return true;
            for(const branch of q.Branches)if(String(branch.ID)===target.branch)
                return branch.GetBranchState()!==UE.EPBZQuestBranchState.InProcess;
            return true;
        }
        return true;
    }
    if(target.save) {
        const manager=helper.getWorldSubsystem(context,UE.PBZLevelActorManager.StaticClass());
        if(manager && require("./SourceState").savedState(manager,target.save,UE,require("puerts"))==="collected")return true;
    }
    if(target.category==="bell")return !!helper.getWorldSubsystem(context,UE.BellManager.StaticClass())?.GetAllActivateBellIDs().Contains(target.reward);
    if(target.inventory && target.reward) {
        const bag=helper.getWorldSubsystem(context,UE.PBZBackPackSubsystem.StaticClass());
        const game=helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass());
        const item=bag?.[target.inventory].GetRef(target.reward);
        return !!item && item.AcquiredCycle===game?.GetCycleCount();
    }
    if(target.domain && helper.getWorldSubsystem(context,UE.PBZGameSystem.StaticClass())?.CompleteAddItemMobDomainNames.Contains(target.domain))return true;
    if(target.nodes?.length) {
        const events=helper.getWorldSubsystem(context,UE.EventPipelineManager.StaticClass());
        if(events && target.nodes.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined))return true;
    }
    return false;
}
function update() {
    if(!target || paused)return;
    const UE=require("ue"),puerts=require("puerts"),cpp=require("cpp"),owner=current(UE);
    if(!owner) { stop(false);return; }
    const {context,pc,hud}=owner;
    if(!hud || !UE.KismetSystemLibrary.IsValid(hud))return;
    const helper=require("../Library/TsHelper").default;
    const map=helper.getWorldSubsystem(context,UE.MapSubsystem.StaticClass());
    const isLocal=String(map?.GetCurrentMapID()).toLowerCase()===target.mapID.toLowerCase();
    const pawn=UE.GameplayStatics.GetPlayerCharacter(context,0);
    if(!pawn || !UE.KismetSystemLibrary.IsValid(pawn) || pawn.IsActorBeingDestroyed() || !isLocal || !hud.IsActivated() || !hud.IsVisible() || pc.bCinematicMode ||
        UE.GameplayStatics.IsGamePaused(context) || (hud.playerhud && !hud.playerhud.IsVisible())) {
        remove(hud,UE);return;
    }
    if(Date.now()-lastStateRead>=1000) {
        lastStateRead=Date.now();
        if(completed(context,UE)) { stop();return; }
    }
    const p=pawn.K2_GetActorLocation(),world=new UE.Vector(target.x,target.y,target.z);
    const size=UE.WidgetLayoutLibrary.GetViewportSize(context),scale=UE.WidgetLayoutLibrary.GetViewportScale(context);
    const width=size.X/scale,height=size.Y/scale;if(width<240 || height<200)return;
    const screen=puerts.$ref(new UE.Vector2D());
    const inFront=UE.WidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(pc,world,screen,true);
    const q=puerts.$unref(screen);let x=q.X,y=q.Y;
    if(!inFront) {
        const rot=pc.GetControlRotation(),yaw=rot.Yaw*Math.PI/180;
        const side=-(target.x-p.X)*Math.sin(yaw)+(target.y-p.Y)*Math.cos(yaw);
        x=side<0?44:width-44;y=height*.6;
    }
    if(!Number.isFinite(x) || !Number.isFinite(y))return;
    const edge=x<108 || x>width-108 || y<45 || y>height-145;
    const view=hud[SLOT] || create(hud,UE,cpp);
    view.boxSlot.SetPosition(new UE.Vector2D(Math.max(108,Math.min(width-108,x)),Math.max(45,Math.min(height-145,y))));
    const distance=Math.round(Math.hypot(target.x-p.X,target.y-p.Y,target.z-p.Z)/100),dz=Math.round((target.z-p.Z)/100);
    view.distance.SetText(cpp.FText.FromString((edge?text.offscreen+" · ":"")+distance+" m"));
    view.elevation.SetText(cpp.FText.FromString(Math.abs(dz)>2?(dz>0?text.above:text.below)+" "+Math.abs(dz)+" m":text.sameLevel));
}
function select(page,point,language) {
    stop();
    const UE=require("ue"),pc=UE.GameplayStatics.GetPlayerController(page,0);
    if(!pc)return;
    target={};
    for(const key of ["id","x","y","z","mapID","category","label","save","inventory","reward","questKey","branch","domain"])
        if(point[key]!==undefined)target[key]=point[key];
    target.nodes=(point.nodes || []).slice();
    text=language;controllerPath=String(UE.KismetSystemLibrary.GetPathName(pc));worldPath=String(UE.KismetSystemLibrary.GetPathName(pc.GetWorld()));lastStateRead=0;
    Diagnostics.record("hud-target",{id:target.id,category:target.category,mapID:target.mapID});
}
function mapOpened() { paused=true;if(timer!==undefined)clearInterval(timer);timer=undefined;const UE=require("ue"),owner=current(UE);if(owner?.hud)remove(owner.hud,UE); }
function mapClosed(reason) {
    if(!["finish","hidden"].includes(reason)) { stop(false);return; }
    paused=false;
    if(target && timer===undefined)timer=setInterval(()=>{
        try { update(); } catch(error) {
            Diagnostics.record("hud-error",{error:String(error)});
            try { stop(); } catch { stop(false); }
        }
    },80);
}
module.exports={select,stop,mapOpened,mapClosed,currentID:()=>target?.id || ""};
