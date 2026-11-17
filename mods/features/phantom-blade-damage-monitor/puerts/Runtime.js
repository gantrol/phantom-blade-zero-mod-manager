"use strict";
const UE = require("ue"), cpp = require("puerts");
const helper = require("../Library/TsHelper").default;
const { Telemetry, valid } = require("./Telemetry"), { View } = require("./View");
const Effects = require("./Effects"), Controls = require("../PhantomBladeRuntime/Controls");
let current, frameClass;
const keys = new Map();
function key(name) {
    if (!keys.has(name)) { const value = new UE.Key(); value.KeyName = name; keys.set(name, value); }
    return keys.get(name);
}
function visible(widget) {
    for (let node = widget; node; node = node.GetParent()) if (!node.IsVisible() || node.GetRenderOpacity() <= 0) return false;
    return true;
}
function stop(state) {
    if (!state) return;
    if (state.timer !== undefined) clearInterval(state.timer);
    state.telemetry.dispose();
    if (valid(state.frame)) { state.frame.SetVisibility(UE.ESlateVisibility.Collapsed); state.frame.RemoveFromParent(); }
    if (current === state) current = undefined;
}
function fail(state, error) {
    stop(state); console.log("[PhantomBladeDamageMonitor] RUNTIME_ERROR " + String(error?.stack || error));
}
function frameType() {
    if (!frameClass) {
        class PhantomBladeDamageMonitorFrame extends UE.UserWidget {
            Tick(geometry, deltaSeconds) {
                if (current?.frame !== this) return;
                try { tick(current, geometry, deltaSeconds); } catch (error) { fail(current, error); }
            }
        }
        Object.defineProperty(PhantomBladeDamageMonitorFrame, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
        frameClass = cpp.makeUClass(PhantomBladeDamageMonitorFrame);
    }
    return frameClass;
}
function mount(state) {
    if (current !== state) return;
    if (!valid(state.source)) { stop(state); return; }
    const hud = UE.PBZHUDLayout.GetHUDLayout(state.source), pc = UE.GameplayStatics.GetPlayerController(state.source, 0);
    if (!valid(hud?.HudOverlay) || !valid(pc)) return;
    state.hud = hud; state.pc = pc;
    const frame = UE.WidgetBlueprintLibrary.Create(state.source, frameType(), pc); state.frame = frame;
    state.root = UE.NewObject(UE.CanvasPanel.StaticClass(), frame.WidgetTree); frame.WidgetTree.RootWidget = state.root;
    frame.SetVisibility(UE.ESlateVisibility.HitTestInvisible); state.root.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    hud.HudOverlay.AddChild(frame);
    const slot = UE.WidgetLayoutLibrary.SlotAsOverlaySlot(frame);
    slot.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill); slot.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);
    frame.ForceVolatile(true);
    state.view = new View(frame, state.root);
    state.sequence = helper.getWorldSubsystem(state.source, UE.PBZLevelSequenceManager.StaticClass());
    state.binding = Controls.read()["damage.details"] || "D";
    clearInterval(state.timer); state.timer = undefined;
}
function start(source) {
    if (current?.source === source) return;
    stop(current);
    const state = { source, telemetry: new Telemetry(), elapsed: 1, effectsElapsed: 1, effects: [] };
    current = state;
    state.timer = setInterval(() => { try { mount(state); } catch (error) { fail(state, error); } }, 250);
}
function input(state, shown) {
    const down = name => state.pc.IsInputKeyDown(key(name));
    const pressed = state.binding !== "None" && down(state.binding)
        && (down("LeftAlt") || down("RightAlt"))
        && !down("LeftControl") && !down("RightControl") && !down("LeftShift") && !down("RightShift");
    if (shown && pressed && !state.pressed) { state.view.cycle(); state.elapsed = 1; state.effectsElapsed = 1; }
    state.pressed = pressed;
}
function tick(state, geometry, deltaSeconds) {
    if (!valid(state.source) || !valid(state.hud) || !valid(state.pc)
        || UE.PBZHUDLayout.GetHUDLayout(state.source) !== state.hud || state.frame.GetParent() !== state.hud.HudOverlay) { stop(state); return; }
    const player = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(state.source), playerHUD = state.pc.GetHUD();
    state.telemetry.clock += Math.max(0, deltaSeconds);
    if (state.telemetry.player !== player) { state.telemetry.bind(player); state.effects = []; state.effectsElapsed = 1; state.elapsed = 1; }
    const shown = valid(player) && !player.bHidden && !player.IsActorBeingDestroyed()
        && state.hud.IsActivated() && visible(state.hud) && !state.pc.bCinematicMode
        && !(valid(state.sequence) && state.sequence.IsInSequence())
        && !UE.GameplayStatics.IsGamePaused(state.source) && (!playerHUD || playerHUD.bShowHUD);
    input(state, shown);
    state.root.SetRenderOpacity(shown ? 1 : 0);
    if (!shown) { state.elapsed = 1; return; }
    state.view.place(UE.SlateBlueprintLibrary.GetLocalSize(geometry));
    state.elapsed += deltaSeconds; state.effectsElapsed += deltaSeconds;
    if (state.elapsed < 0.1) return;
    state.elapsed = 0;
    const snapshot = state.telemetry.sample(0);
    if (state.effectsTarget !== snapshot?.target) { state.effects = []; state.effectsElapsed = 1; state.effectsTarget = snapshot?.target; }
    if (state.view.expanded && state.effectsElapsed >= 1) {
        state.effectsElapsed = 0;
        try { state.effects = [...Effects.read(player, "player"), ...Effects.read(snapshot?.target, "target")]; }
        catch (error) { state.effects = []; state.telemetry.report("effects", error); }
    }
    state.view.render(snapshot, state.effects, state.binding === "None" ? "" : "Alt + " + state.binding, state.telemetry.clock);
}
function after(proto, name, callback) {
    const original = proto[name];
    if (typeof original !== "function") throw Error("DAMAGE_MONITOR_INTERFACE_MISMATCH: " + name);
    proto[name] = function (...args) {
        const result = original.apply(this, args);
        try { callback(this); } catch (error) { fail(current, error); }
        return result;
    };
}
function install(Type) {
    const proto = Type.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__damageMonitorInstalled")) return;
    Object.defineProperty(proto, "__damageMonitorInstalled", { value: true });
    after(proto, "InitMaxBarValue", start);
    after(proto, "Destruct", source => { if (current?.source === source) stop(current); });
}
module.exports = { install };
