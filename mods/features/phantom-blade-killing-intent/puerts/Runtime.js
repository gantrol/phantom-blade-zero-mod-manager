"use strict";
const UE = require("ue"), cpp = require("puerts");
const { Meter } = require("./Meter"), config = require("./Config");
const bosses = new Set();
const valid = object => !!object && UE.KismetSystemLibrary.IsValid(object);
let current, frameClass, attributes, combatTag;

function statics() {
    if (attributes) return;
    attributes = Object.fromEntries(["Health", "Shachi", "Shachi_Max"].map(name =>
        [name, UE.PBZBlueprintFunctionLibrary.MakeAttribute(name, UE.BasicAttributeSet.StaticClass())]));
    combatTag = require("../Library/TsHelper").default.constructGameplayTag("AI.State.Combat");
}
function read(actor, name) { return UE.AttributeUtils.GetCharacterAttributeValue(actor, attributes[name]); }
function branchVisible(widget) {
    for (let node = widget; node; node = node.GetParent())
        if (!node.IsVisible() || node.GetRenderOpacity() <= 0) return false;
    return true;
}
function stop(state) {
    if (!state) return;
    if (state.timer !== undefined) clearInterval(state.timer);
    if (valid(state.frame)) { state.frame.SetVisibility(UE.ESlateVisibility.Collapsed); state.frame.RemoveFromParent(); }
    state.nodes.clear();
    if (current === state) current = undefined;
}
function fail(state, error) {
    stop(state);
    console.log("[PhantomBladeKillingIntent] RUNTIME_ERROR " + String(error));
}
function frameType() {
    if (!frameClass) {
        class PhantomBladeKillingIntentFrame extends UE.UserWidget {
            Tick(geometry, deltaSeconds) {
                const state = current;
                if (!state || state.frame !== this) return;
                try { tick(state, geometry, deltaSeconds); } catch (error) { fail(state, error); }
            }
        }
        Object.defineProperty(PhantomBladeKillingIntentFrame, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
        frameClass = cpp.makeUClass(PhantomBladeKillingIntentFrame);
    }
    return frameClass;
}
function mount(state) {
    if (!valid(state.source)) { stop(state); return; }
    const hud = UE.PBZHUDLayout.GetHUDLayout(state.source);
    const pc = UE.GameplayStatics.GetPlayerController(state.source, 0);
    const source = state.source.AnimBreakmeterBar?.MID_Bar;
    if (!valid(hud?.HudOverlay) || !valid(pc) || !valid(source)) return;
    statics();
    const frame = UE.WidgetBlueprintLibrary.Create(state.source, frameType(), pc);
    state.frame = frame; state.hud = hud; state.pc = pc;
    state.root = UE.NewObject(UE.CanvasPanel.StaticClass(), frame.WidgetTree);
    frame.WidgetTree.RootWidget = state.root;
    frame.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    state.root.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    hud.HudOverlay.AddChild(frame);
    const slot = UE.WidgetLayoutLibrary.SlotAsOverlaySlot(frame);
    slot.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill);
    slot.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);
    frame.ForceVolatile(true);
    state.materialSource = source;
    state.playerMeter = new Meter(frame, state.root, source, config.playerHeight);
    clearInterval(state.timer); state.timer = undefined;
}
function start(source) {
    if (current?.source === source) return;
    stop(current);
    const state = { source, nodes: new Map(), elapsed: config.sampleInterval };
    current = state;
    // Blueprint Construct completes before creating our tree; retry only while
    // the HUD and its native material are being initialized, never during play.
    state.timer = setInterval(() => {
        try { mount(state); } catch (error) { fail(state, error); }
    }, 250);
}
function sample(state, player) {
    const playerMax = read(player, "Shachi_Max");
    state.playerReady = playerMax > 0 && read(player, "Health") > 0;
    if (state.playerReady) state.playerMeter.setRatio(read(player, "Shachi") / playerMax);
    const active = new Set();
    for (const page of bosses) {
        if (!valid(page) || !valid(page.SelfActor)) { bosses.delete(page); continue; }
        const actor = page.SelfActor;
        if (actor.GetWorld() !== player.GetWorld() || !page.isBoss() || actor.bHidden || actor.IsActorBeingDestroyed()) continue;
        if (actor.GetDistanceTo(player) > config.bossDistance) continue;
        const asc = UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(actor);
        if (!asc?.HasMatchingGameplayTag(combatTag) || read(actor, "Health") <= 0) continue;
        const max = read(actor, "Shachi_Max");
        if (max <= 0) continue;
        let node = state.nodes.get(page);
        if (!node) {
            node = { actor, meter: new Meter(state.frame, state.root, state.materialSource, config.bossHeight) };
            state.nodes.set(page, node);
        }
        node.meter.setRatio(read(actor, "Shachi") / max);
        active.add(page);
        if (active.size === config.maxBossMeters) break;
    }
    for (const [page, node] of state.nodes) if (!active.has(page)) { node.meter.dispose(); state.nodes.delete(page); }
}
function project(state, geometry, actor, meter, size) {
    if (!valid(actor)) { meter.show(false); return; }
    const screen = cpp.$ref(new UE.Vector2D());
    if (!state.pc.ProjectWorldLocationToScreen(actor.K2_GetActorLocation(), screen, false)) { meter.show(false); return; }
    const local = cpp.$ref(new UE.Vector2D());
    UE.SlateBlueprintLibrary.ScreenToWidgetLocal(state.pc, geometry, cpp.$unref(screen), local, false);
    const point = cpp.$unref(local), x = point.X + config.sideOffset, y = point.Y;
    // Behind-camera/offscreen actors disappear instead of pinning bars to edges.
    const visible = x > meter.width / 2 && x < size.X - meter.width / 2
        && y > meter.height / 2 && y < size.Y - meter.height / 2;
    if (visible) meter.place(x, y);
    meter.show(visible);
}
function tick(state, geometry, deltaSeconds) {
    if (!valid(state.source) || !valid(state.hud) || !valid(state.pc)
        || UE.PBZHUDLayout.GetHUDLayout(state.source) !== state.hud
        || state.frame.GetParent() !== state.hud.HudOverlay) { stop(state); return; }
    const player = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(state.source);
    const playerHUD = state.pc.GetHUD();
    const shown = valid(player) && state.hud.IsActivated() && branchVisible(state.hud)
        && !state.pc.bCinematicMode && !UE.GameplayStatics.IsGamePaused(state.source) && (!playerHUD || playerHUD.bShowHUD);
    // Keep the frame ticking when hidden so resume/fade-in restores the meters.
    state.root.SetRenderOpacity(shown ? 1 : 0);
    if (!shown) return;
    state.elapsed += deltaSeconds;
    if (state.elapsed >= config.sampleInterval) { state.elapsed = 0; sample(state, player); }
    const size = UE.SlateBlueprintLibrary.GetLocalSize(geometry);
    if (state.playerReady) project(state, geometry, player, state.playerMeter, size);
    else state.playerMeter.show(false);
    for (const { actor, meter } of state.nodes.values()) project(state, geometry, actor, meter, size);
}
function after(proto, name, callback) {
    const original = proto[name];
    if (typeof original !== "function") throw Error("KILLING_INTENT_INTERFACE_MISMATCH: " + name);
    proto[name] = function (...args) {
        const result = original.apply(this, args);
        try { callback(this); } catch (error) { fail(current, error); }
        return result;
    };
}
function install(Type, kind) {
    const proto = Type.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__killingIntentInstalled")) return;
    Object.defineProperty(proto, "__killingIntentInstalled", { value: true });
    if (kind === "player") {
        after(proto, "InitMaxBarValue", start);
        after(proto, "Destruct", page => { if (current?.source === page) stop(current); });
    } else {
        const track = page => { if (page.isBoss()) bosses.add(page); else bosses.delete(page); };
        after(proto, "IsBossType", track);
        after(proto, "syncBossTakeover", track);
        after(proto, "cleanup", page => {
            bosses.delete(page);
            const node = current?.nodes.get(page);
            if (node) { node.meter.dispose(); current.nodes.delete(page); }
        });
    }
}
module.exports = { install };
