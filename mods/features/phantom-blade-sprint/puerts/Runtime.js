"use strict";
const UE = require("ue");
const cpp = require("cpp");
const TsHelper = require("../Library/TsHelper").default;
const UIHelper = require("../Library/UIHelper").default;
const Controls = require("../PhantomBladeRuntime/Controls");
const Assets = require("../PhantomBladeRuntime/Assets");
const Locales = require("./Locales");

const START_THRESHOLD = 0.85;
const STOP_THRESHOLD = 0.65;
const MANUAL_COOLDOWN_SECONDS = 0.4;
const TICK_INTERVAL_MS = 33;
const ACTION_PATHS = ["IA_Sprint", "IA_KeyboardMoveInput", "IA_Walk"]
    .map(name => "/SGameLocomotionDriver/Input/" + name + "." + name);

let enabled = true;
let autoSprinting = false;
let manualCooldownRemaining = 0;
let lastMagnitude = 0;
let cachedIaSprint = null;
let keyboardAction = null, walkAction = null;
let movementKeys = [], manualKeys = [], lastMappingCheckMs = 0;
let actionGeneration = 0;
let actionRequested = false;
let injectedSubsystem = null;
let injectedAction = null;
let timer, ownerPath = "", lastState = "";
let cachedValues = null;
let lastControlsCheckMs = 0;
const keyCache = new Map();
const wasKeyDown = new Map();

function report(state, detail = {}) {
    if (state === lastState) return;
    lastState = state;
    console.log("[PhantomBladeSprint] " + JSON.stringify({ state, ...detail }));
}

function isNativeObjectAlive(obj) {
    if (!obj) return false;
    try {
        return UE.KismetSystemLibrary.IsValid(obj);
    } catch (error) {
        if (error?.message === "passing a invalid object") return false;
        return false;
    }
}

function getKey(name) {
    let key = keyCache.get(name);
    if (!key) {
        key = new UE.Key();
        key.KeyName = name;
        keyCache.set(name, key);
    }
    return key;
}

function isKeyDown(pc, name) {
    if (!name || name === "None") return false;
    try {
        return !!pc.IsInputKeyDown(getKey(name));
    } catch (_) {
        return false;
    }
}

function hasAnyModifier(pc) {
    return isKeyDown(pc, "LeftAlt") || isKeyDown(pc, "RightAlt")
        || isKeyDown(pc, "LeftControl") || isKeyDown(pc, "RightControl")
        || isKeyDown(pc, "LeftShift") || isKeyDown(pc, "RightShift");
}

function checkJustPressed(pc, id, keyName) {
    if (!keyName || keyName === "None") {
        wasKeyDown.set(id, false);
        return false;
    }
    const down = !hasAnyModifier(pc) && isKeyDown(pc, keyName);
    const prev = !!wasKeyDown.get(id);
    wasKeyDown.set(id, down);
    return down && !prev;
}

function getSprintAction(player) {
    if (cachedIaSprint && isNativeObjectAlive(cachedIaSprint)) return cachedIaSprint;
    if (!actionRequested) {
        actionRequested = true;
        const generation = actionGeneration;
        Assets.load(UE.GameplayStatics.GetGameInstance(player), ACTION_PATHS, loaded => {
            if (generation !== actionGeneration) return;
            cachedIaSprint = loaded[0];
            keyboardAction = loaded[1];walkAction = loaded[2];lastMappingCheckMs = 0;
            report(cachedIaSprint ? "action-ready" : "action-missing", {
                paths: ACTION_PATHS, loaded: loaded.map(Boolean)
            });
        });
    }
    return null;
}

function mappedKeys(subsystem, action) {
    if (!isNativeObjectAlive(action)) return [];
    const keys = subsystem.QueryKeysMappedToAction(action), names = [];
    for (let i = 0; i < keys.Num(); i++) names.push(String(keys.Get(i).KeyName));
    return names;
}

function refreshMappings(subsystem) {
    if (Date.now() - lastMappingCheckMs < 1000) return;
    lastMappingCheckMs = Date.now();
    // Query active mappings so game-side rebinding works for both input devices.
    movementKeys = mappedKeys(subsystem, keyboardAction).filter(key => !key.startsWith("Gamepad_"));
    manualKeys = [...new Set([...mappedKeys(subsystem, cachedIaSprint), ...mappedKeys(subsystem, walkAction)])];
}

function stopInjection() {
    const subsystem = injectedSubsystem, action = injectedAction;
    injectedSubsystem = injectedAction = null;
    if (isNativeObjectAlive(subsystem) && isNativeObjectAlive(action))
        subsystem.StopContinuousInputInjectionForAction(action);
}

function injectSprint(sub, action) {
    if (sub === injectedSubsystem && action === injectedAction) return true;
    stopInjection();
    const value = UE.EnhancedInputLibrary.MakeInputActionValueOfType(1, 0, 0, action.ValueType);
    // Enhanced Input holds this value on every game frame, until explicitly stopped.
    sub.StartContinuousInputInjectionForAction(action, value, UE.NewArray(UE.InputModifier), UE.NewArray(UE.InputTrigger));
    injectedSubsystem = sub;
    injectedAction = action;
    return true;
}

function refreshControlsIfNeeded() {
    const now = Date.now();
    if (!cachedValues || now - lastControlsCheckMs > 3000) {
        lastControlsCheckMs = now;
        try {
            cachedValues = Controls.read();
        } catch (_) {
            if (!cachedValues) cachedValues = { "sprint.toggle": "F11", "sprint.diagnostics": "F5" };
        }
    }
    return cachedValues;
}

function stepSprint(player) {
    const pc = UE.GameplayStatics.GetPlayerController(player, 0);
    if (!isNativeObjectAlive(pc) || UE.GameplayStatics.IsGamePaused(player) || pc.bCinematicMode || pc.IsMoveInputIgnored()) {
        stopInjection();autoSprinting = false;report("paused");return;
    }

    const values = refreshControlsIfNeeded();
    if (checkJustPressed(pc, "sprint.toggle", values["sprint.toggle"])) {
        enabled = !enabled;
        autoSprinting = false;
        manualCooldownRemaining = 0;
        const msg = Locales.text(enabled ? "enabled" : "disabled");
        console.log("[PhantomBladeSprint] " + msg);
        try { UIHelper.showTipsText(player, cpp.FText.FromString(msg)); } catch (_) {}
    }

    if (checkJustPressed(pc, "sprint.diagnostics", values["sprint.diagnostics"])) {
        console.log("[PhantomBladeSprint] " + JSON.stringify({enabled, autoSprinting, magnitude:lastMagnitude,
            cooldown:manualCooldownRemaining, movementKeys, manualKeys, action:!!cachedIaSprint}));
    }

    if (!enabled) {
        autoSprinting = false;
        stopInjection();report("disabled");
        return;
    }

    const action = getSprintAction(player);
    const sub = TsHelper.getlocalPlayerSubsystem(player, UE.EnhancedInputLocalPlayerSubsystem.StaticClass());
    if (!sub) { stopInjection();report("waiting-input");return; }
    refreshMappings(sub);
    const dt = TICK_INTERVAL_MS / 1000;
    if (manualKeys.some(key => isKeyDown(pc, key))) {
        autoSprinting = false;
        manualCooldownRemaining = MANUAL_COOLDOWN_SECONDS;
        stopInjection();report("manual-input");
        return;
    }
    if (manualCooldownRemaining > 0) {
        manualCooldownRemaining = Math.max(0, manualCooldownRemaining - dt);
        autoSprinting = false;
        stopInjection();
        return;
    }

    const lx = Number(pc.GetInputAnalogKeyState(getKey("Gamepad_LeftX"))) || 0;
    const ly = Number(pc.GetInputAnalogKeyState(getKey("Gamepad_LeftY"))) || 0;
    const keyboardMoving = movementKeys.some(key => isKeyDown(pc, key));
    const mag = Math.max(keyboardMoving ? 1 : 0, Math.hypot(lx, ly));
    lastMagnitude = mag;

    if (autoSprinting) {
        autoSprinting = mag >= STOP_THRESHOLD;
    } else {
        autoSprinting = mag >= START_THRESHOLD;
    }

    if (autoSprinting) {
        if (action && injectSprint(sub, action)) report("sprinting", { magnitude: mag, input:keyboardMoving ? "keyboard" : "gamepad" });
    } else {
        stopInjection();report("ready");
    }
}

function install() {
    if (timer !== undefined) return;
    // Resolve only the currently controlled pawn; pre-spawned substitutes do not
    // own a second timer, and installation does not depend on BeginPlay timing.
    timer = setInterval(() => {
        try {
            const context = UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
            const player = isNativeObjectAlive(context) && UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context);
            if (!isNativeObjectAlive(player) || !player.IsPlayerControlled()) {
                stopInjection();autoSprinting = false;ownerPath = "";report("waiting-player");return;
            }
            const path = String(UE.KismetSystemLibrary.GetPathName(player));
            if (path !== ownerPath) {
                stopInjection();ownerPath = path;autoSprinting = false;manualCooldownRemaining = 0;
                cachedValues = null;wasKeyDown.clear();actionRequested = false;actionGeneration++;
                cachedIaSprint = keyboardAction = walkAction = null;
                movementKeys = [];manualKeys = [];lastMappingCheckMs = 0;
                report("player-ready", { player: path });
            }
            stepSprint(player);
        } catch (error) {
            try { stopInjection(); } catch (_) {}
            report("error", { error: String(error), stack: String(error?.stack || "") });
        }
    }, TICK_INTERVAL_MS);
    report("timer-ready");
}

module.exports = { install };
