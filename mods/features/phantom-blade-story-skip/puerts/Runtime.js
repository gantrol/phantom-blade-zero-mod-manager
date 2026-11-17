"use strict";
const UE = require("ue");
const { $ref, $unref } = require("puerts");
const Assets = require("../PhantomBladeRuntime/Assets");
const { Guard, valid, path, active } = require("./Guard");

const FLOW = "/Game/PBZContent/Blueprint/FlowGraph/";
const CLASS_PATHS = {
    sequence: "UI/WBP_FlowSequencer.WBP_FlowSequencer_C",
    video: "UI/WBP_FlowVideo.WBP_FlowVideo_C",
    node: "FlowGraphNode/PlayLevelSequence.PlayLevelSequence_C",
    qte: "FlowGraphNode/PlayLevelSequence_QTE.PlayLevelSequence_QTE_C",
    videoNode: "FlowGraphNode/PlayVideo.PlayVideo_C"
};
const TRANSITION_MS = 2000;
const ACTION_TIMEOUT_MS = 5000;
const BATCH_TIMEOUT_MS = 120000;
let timer, classes, guard, loading = false, failed = false;
let owner = null, world = null, widgets = [], nextScan = 0, scannedSession = "";
let lastTick = 0;
let needsRelease = true, wasDown = false, sceneKey = "", batch = null, lastReport = "";
const skipKey = new UE.Key();
skipKey.KeyName = "Gamepad_FaceButton_Right";

function report(state, detail = "") {
    const message = state + (detail ? " " + detail : "");
    if (message === lastReport) return;
    lastReport = message;
    console.log("[PhantomBladeStorySkip] " + message);
}

function stop(reason) {
    if (batch) report("stopped", reason);
    batch = null;
    needsRelease = true;
    sceneKey = "";
}

function loadClasses(context) {
    if (loading || classes || failed) return;
    loading = true;
    const names = Object.keys(CLASS_PATHS);
    Assets.load(UE.GameplayStatics.GetGameInstance(context), names.map(name => FLOW + CLASS_PATHS[name]), loaded => {
        loading = false;
        if (loaded.some(type => !valid(type))) {
            failed = true;
            report("disabled", "missing-story-classes");
            return;
        }
        classes = Object.fromEntries(names.map((name, index) => [name, loaded[index]]));
        guard = new Guard(classes);
        report("ready", "tap-B / guarded-native-skip");
    });
}

function findScenes(context, state, now, fresh = false) {
    const session = state.sessionId;
    if (fresh || now >= nextScan || session !== scannedSession) {
        nextScan = now + 250;
        scannedSession = session;
        widgets = [];
        for (const kind of ["sequence", "video"]) {
            const found = $ref(UE.NewArray(UE.UserWidget));
            UE.WidgetBlueprintLibrary.GetAllWidgetsOfClass(context, found, classes[kind], false);
            const list = $unref(found);
            for (let index = 0; index < list.Num(); index++) {
                const widget = list.Get(index);
                if (active(widget)) widgets.push({ kind, widget });
            }
        }
    }
    const scenes = [];
    for (const { kind, widget } of widgets) {
        if (!active(widget)) continue;
        const player = kind === "sequence" ? widget.InLevelSeqPlayer : widget.LevelSequencePlayer;
        if (!valid(player)) return { reason: "unloaded-player" };
        const sequence = player.GetSequence();
        const node = kind === "sequence" ? widget["In Level Sequence Node"] : state.node;
        const scene = { kind, widget, player, sequence, node };
        const reason = guard.scene(scene, state);
        if (reason === "inactive-scene") { nextScan = 0; continue; }
        // A blocked scene vetoes the whole selection, even if another widget looks skippable.
        if (reason) return { reason };
        scene.key = [path(widget), path(player), path(sequence), session].join("|");
        scenes.push(scene);
    }
    if (scenes.length > 1) return { reason: "overlapping-scenes" };
    return { scene: scenes[0], reason: "" };
}

function playingAnimation(widget) {
    for (const name of ["popup", "BlendIn", "BlendOut", "PopIn"]) {
        const animation = widget[name];
        if (valid(animation) && widget.IsAnimationPlaying(animation)) return true;
    }
    return false;
}

function playTime(widget) {
    const time = $ref(0);
    widget.GetCurrentPlayTime(time);
    return Number($unref(time));
}

function skipTime(widget) {
    if (widget.SkipTimes.Num() === 0) return Number(widget.LastShotSectionStatyTime);
    const time = $ref(0);
    widget.GetCurrentSkipTime(time);
    return Number($unref(time));
}

function advance(scene, context, controller, now) {
    if (batch.lastKey !== scene.key) {
        batch.lastKey = scene.key;
        batch.pending = null;
        batch.lastTarget = -Infinity;
    }
    if (scene.kind === "video" && batch.pending) {
        if (now - batch.pending.started > ACTION_TIMEOUT_MS) stop("native-skip-timeout");
        return;
    }
    const current = scene.kind === "sequence" ? playTime(scene.widget) : 0;
    if (!Number.isFinite(current)) { stop("invalid-play-time"); return; }
    if (batch.pending) {
        if (current + 0.05 < batch.pending.target) {
            if (now - batch.pending.started > ACTION_TIMEOUT_MS) stop("native-skip-timeout");
            return;
        }
        if (playingAnimation(scene.widget)) return;
        batch.pending = null;
    }
    if (playingAnimation(scene.widget) || scene.player.IsPaused()) return;
    const target = scene.kind === "sequence" ? skipTime(scene.widget) : Infinity;
    // The last shot plays out normally. Never fabricate a flow completion event.
    if (scene.kind === "sequence" && (!Number.isFinite(target) || target <= current + 0.05 || target <= batch.lastTarget + 0.05)) return;
    const state = guard.context(context, controller);
    if (state.reason) { stop(state.reason); return; }
    // A newly activated QTE widget must veto an older cached ordinary widget immediately.
    const latest = findScenes(context, state, now, true);
    if (latest.reason || latest.scene?.key !== scene.key) { stop(latest.reason || "scene-changed"); return; }
    const reason = guard.scene(scene, state, true);
    if (reason) { stop(reason); return; }
    // Recheck ownership immediately before the only mutating operation. No global flag,
    // persistent CanSkip override, GetCurrentSkipTime hook or direct player seek is used.
    batch.pending = { target, started: now };
    batch.lastTarget = target;
    if (scene.kind === "sequence") {
        const original = scene.widget.CanSkip;
        try {
            scene.widget.CanSkip = true;
            scene.widget.ActionProgress(1);
        } finally {
            if (!original && valid(scene.widget) && scene.widget.CanSkip === true && scene.widget.InLevelSeqPlayer === scene.player
                && scene.widget["In Level Sequence Node"] === scene.node) scene.widget.CanSkip = original;
        }
    } else scene.widget["Action Progress_Event"](1);
    report("skipped", path(scene.sequence));
}

function tick() {
    const context = UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
    if (!valid(context)) { stop("no-world"); owner = world = null; widgets = []; return; }
    loadClasses(context);
    if (!guard || failed) return;
    const controller = UE.GameplayStatics.GetPlayerController(context, 0);
    const currentWorld = UE.SubsystemBlueprintLibrary.GetWorldSubsystem(context, UE.PBZLevelSequenceManager.StaticClass());
    if (owner !== controller || world !== currentWorld) {
        stop("world-changed");
        owner = controller; world = currentWorld;
        widgets = []; nextScan = 0; scannedSession = ""; wasDown = false;
        guard = new Guard(classes);
    }
    const state = guard.context(context, controller);
    if (state.reason) { stop(state.reason); widgets = []; nextScan = 0; return; }
    const now = Date.now();
    if (lastTick && (now < lastTick || now - lastTick > 1000)) stop("tick-gap");
    lastTick = now;
    const down = controller.IsInputKeyDown(skipKey);
    const pressed = !needsRelease && !wasDown && (down || controller.WasInputKeyJustPressed(skipKey));
    wasDown = down;
    const result = findScenes(context, state, now);
    if (result.reason) { stop(result.reason); return; }
    const scene = result.scene;
    if (!scene) {
        needsRelease = true;
        sceneKey = "";
        if (batch && (!state.locked || now - batch.lastSceneAt > TRANSITION_MS)) stop("story-ended");
        return;
    }
    const changedScene = sceneKey !== scene.key;
    if (changedScene) {
        sceneKey = scene.key;
        needsRelease = true;
    }
    if (!down) needsRelease = false;
    if (!batch && !changedScene && pressed && !needsRelease) {
        batch = { started: now, lastSceneAt: now, lastKey: "", lastTarget: -Infinity, pending: null };
        report("started");
    }
    if (!batch) return;
    if (now - batch.started > BATCH_TIMEOUT_MS) { stop("batch-timeout"); return; }
    batch.lastSceneAt = now;
    advance(scene, context, controller, now);
}

function install() {
    if (timer !== undefined) return;
    // PuerTS's ticker runs on the game thread; a zero interval samples each frame.
    timer = setInterval(() => {
        try { tick(); }
        catch (error) {
            stop("inspection-error");
            report("blocked", String(error?.stack || error));
        }
    }, 0);
}

module.exports = { install };
