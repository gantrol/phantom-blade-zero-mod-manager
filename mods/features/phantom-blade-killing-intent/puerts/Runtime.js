"use strict";
const UE = require("ue"), cpp = require("puerts");
const { OverheadMeters } = require("./Meter"), config = require("./Config");
const helper = require("../Library/TsHelper").default;
const valid = object => !!object && UE.KismetSystemLibrary.IsValid(object);
let current, frameClass, attributes, combatTag;

function statics() {
    if (attributes) return;
    attributes = Object.fromEntries(["Health", "Shachi", "Shachi_Max"].map(name =>
        [name, UE.PBZBlueprintFunctionLibrary.MakeAttribute(name, UE.BasicAttributeSet.StaticClass())]));
    combatTag = helper.constructGameplayTag("CharacterState.InCombat");
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
    if (current !== state) return;
    if (!valid(state.source)) { stop(state); return; }
    const hud = UE.PBZHUDLayout.GetHUDLayout(state.source);
    const pc = UE.GameplayStatics.GetPlayerController(state.source, 0);
    if (!valid(hud?.HudOverlay) || !valid(pc)) return;
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
    state.playerMeter = new OverheadMeters(frame, state.root);
    state.sequence = helper.getWorldSubsystem(state.source, UE.PBZLevelSequenceManager.StaticClass());
    clearInterval(state.timer); state.timer = undefined;
}
function start(source) {
    if (current?.source === source) return;
    stop(current);
    const state = { source, elapsed: config.sampleInterval };
    current = state;
    // Blueprint Construct completes before creating our tree; retry only while
    // the HUD is being initialized, never during play.
    state.timer = setInterval(() => {
        try { mount(state); } catch (error) { fail(state, error); }
    }, 250);
}
function sample(state, player) {
    state.playerReady = read(player, "Health") > 0
        && state.playerMeter.setIntent(read(player, "Shachi"), read(player, "Shachi_Max"));
    state.playerMeter.setTension(state.playerReady ? tension(state) : undefined);
}
function tension(state) {
    // Use the game's default active encounter. ForBoss is deliberately null,
    // not the player pawn: sessions are keyed by the opponent. Never create or
    // reset sessions, patch profiles, or synthesize a score in this HUD.
    if (UE.KismetSystemLibrary.GetConsoleVariableIntValue("pbz.Tension.Enable") === 0
        || !UE.TensionLibrary.HasActiveSession(state.source)) return;
    const result = UE.TensionLibrary.GetTensionReadout(state.source, null);
    if (result && result.Confidence > 0 && Number.isFinite(result.Raw)) return result.Raw;
}
function projectPlayer(state, geometry, player, size) {
    const meter = state.playerMeter, mesh = player.Mesh;
    let location;
    if (valid(mesh) && mesh.DoesSocketExist(config.headSocket)) location = mesh.GetSocketLocation(config.headSocket);
    else {
        const capsule = player.CapsuleComponent;
        if (!valid(capsule)) { meter.show(false); return; }
        location = player.K2_GetActorLocation();
        location.Z += capsule.GetScaledCapsuleHalfHeight();
    }
    location.Z += config.headLift;
    const screen = cpp.$ref(new UE.Vector2D());
    if (!state.pc.ProjectWorldLocationToScreen(location, screen, false)) { meter.show(false); return; }
    const local = cpp.$ref(new UE.Vector2D());
    UE.SlateBlueprintLibrary.ScreenToWidgetLocal(state.pc, geometry, cpp.$unref(screen), local, false);
    const point = cpp.$unref(local), x = point.X, y = point.Y - config.headScreenGap;
    const visible = Number.isFinite(x) && Number.isFinite(y)
        && x >= meter.width / 2 && x <= size.X - meter.width / 2
        && y >= meter.height && y <= size.Y;
    if (visible) meter.place(x, y);
    meter.show(visible);
}
function tick(state, geometry, deltaSeconds) {
    if (!valid(state.source) || !valid(state.hud) || !valid(state.pc)
        || UE.PBZHUDLayout.GetHUDLayout(state.source) !== state.hud
        || state.frame.GetParent() !== state.hud.HudOverlay) { stop(state); return; }
    const player = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(state.source);
    const playerHUD = state.pc.GetHUD();
    const asc = valid(player) ? UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(player) : undefined;
    const shown = valid(player) && !player.bHidden && !player.IsActorBeingDestroyed()
        && valid(asc) && asc.HasMatchingGameplayTag(combatTag)
        && state.hud.IsActivated() && branchVisible(state.hud)
        && !state.pc.bCinematicMode && !(valid(state.sequence) && state.sequence.IsInSequence())
        && !UE.GameplayStatics.IsGamePaused(state.source) && (!playerHUD || playerHUD.bShowHUD);
    // Keep ticking out of combat; re-entry samples fresh values before display.
    state.root.SetRenderOpacity(shown ? 1 : 0);
    if (!shown) { state.elapsed = config.sampleInterval; return; }
    state.elapsed += deltaSeconds;
    if (state.player !== player || state.elapsed >= config.sampleInterval) {
        state.player = player; state.elapsed = 0; sample(state, player);
    }
    const size = UE.SlateBlueprintLibrary.GetLocalSize(geometry);
    if (state.playerReady) projectPlayer(state, geometry, player, size);
    else state.playerMeter.show(false);
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
function install(Type, kind = "player") {
    // Older shared bootstraps may still request the retired enemy hook.
    if (kind !== "player") return;
    const proto = Type.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__killingIntentInstalled")) return;
    Object.defineProperty(proto, "__killingIntentInstalled", { value: true });
    after(proto, "InitMaxBarValue", start);
    after(proto, "Destruct", page => { if (current?.source === page) stop(current); });
}
module.exports = { install };
