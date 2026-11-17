"use strict";
const UE = require("ue");
const puerts = require("puerts");
const Policy = require("./Policy");
const Selection = require("./Selection");

const PREFIX = "/PBZCombatAsset/Prototypes/Targeting/";
const MODES = [
    { id: "normal", handler: "PBZ_TargetingHandler_CombatLock",
        validator: "PBZ_LockTargetValidator_CombatDefault", validatorTag: "Targeting.Validator.Combat.Default",
        changerTag: "Targeting.TargetChanger.CombatNormal" },
    { id: "bow", handler: "PBZ_TargetingHandler_CombatAim_UnnamedBow",
        handlerTag: "Targeting.Handler.CombatAim.UnnamedBow",
        validator: "PBZ_LockTargetValidator_CombatWithEnvironmentalObject",
        validatorTag: "Targeting.Validator.Combat.WithEnvironmentalObject",
        changerTag: "Targeting.TargetChanger.CombatAim" }
];
const SPECIAL_TAGS = ["CharacterState.AbilityState.InSpecialPerform",
    "CharacterState.Native.Sequence", "CharacterState.HitReact.State.Executing",
    "CharacterState.DeflectTaskActive", "CharacterState.Parry.Lock", "CharacterState.Deflect.Stance",
    "CharacterState.Stand.Invulnerable.Dodge", "CharacterState.Stand.Invulnerable.Execution",
    "Ability.Action.Hun.PerfectDodge", "Ability.Action.Execution", "Ability.Action.Hun.SimpleExecution"].map(tag);
// Native input switches immediately, then schedules held repeats. A finite
// sentinel disables those repeats without replacing input bindings or polling
// physical keys, so game-side remapping and native input gates still apply.
const DISABLED_INPUT = 1e9;
const CHANGER_VALUES = {
    JoystickActiveValue: Math.fround(Policy.stickActive),
    JoystickDeactiveValue: Math.fround(Policy.stickRelease),
    JoystickChangeTargetQuickDelay: DISABLED_INPUT,
    JoystickChangeTargetQuickCooldown: DISABLED_INPUT,
    MouseActiveValue: DISABLED_INPUT
};

let timer, session = null, failedPlayer = null, lastReport = "";
const generated = new Map();
const visibility = new WeakMap();
const environmentalTargets = new WeakMap();
const attached = new WeakMap();

function tag(name) { const value = new UE.GameplayTag(); value.TagName = name; return value; }
function valid(object) { return !!object && UE.KismetSystemLibrary.IsValid(object); }
function path(object) { return String(UE.KismetSystemLibrary.GetPathName(object)); }
function classPath(name) { return PREFIX + name + "." + name + "_C"; }
function report(state, detail = "") {
    const message = state + (detail ? " " + detail : "");
    if (message === lastReport) return;
    lastReport = message;
    console.log("[PhantomBladeTargeting] " + message);
}
function specialAction(player) {
    const asc = UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(player);
    return !valid(asc) || SPECIAL_TAGS.some(value => asc.HasMatchingGameplayTag(value));
}
function currentRecord(s) {
    const component = s.component;
    const current = valid(component.CurrentHandler) ? component.CurrentHandler : component.DefaultTargetHandler;
    return s.records.find(record => record.handler === current);
}
function activeRecord(owner, component) {
    const s = session;
    if (!s || s.failed || s.player !== owner || s.component !== component || specialAction(owner)) return null;
    const pc = s.controller;
    if (!valid(pc) || pc.bCinematicMode || pc.IsLookInputIgnored() || UE.GameplayStatics.IsGamePaused(owner)) return null;
    return currentRecord(s);
}

function createHandlerClass(originalClass) {
    const key = path(originalClass);
    if (generated.has(key)) return generated.get(key);
    const Base = puerts.blueprint.tojs(originalClass);
    class Parent {}
    Object.setPrototypeOf(Parent.prototype, Base.prototype);
    class Handler extends Parent {
        FindTargetsRated(ignoredActors, targetsRef) {
            super.FindTargetsRated(ignoredActors, targetsRef);
            try {
                const record = activeRecord(this.Avatar, this.TargetingComponent);
                if (record?.handler === this) Selection.arrange(this, puerts.$unref(targetsRef));
                else Selection.reset(this);
            } catch (error) { fail(error); }
        }
    }
    const result = puerts.blueprint.mixin(Base, Handler, { inherit: true, objectTakeByNative: true }).StaticClass();
    generated.set(key, result);
    return result;
}

function createValidatorClass(originalClass) {
    const key = path(originalClass);
    if (generated.has(key)) return generated.get(key);
    const Base = puerts.blueprint.tojs(originalClass);
    class Parent {}
    Object.setPrototypeOf(Parent.prototype, Base.prototype);
    class Validator extends Parent {
        StartValidating(target) {
            visibility.delete(this);
            environmentalTargets.delete(this);
            super.StartValidating(target);
            try {
                const actor = UE.ComponentSocketHelper.GetActor(this.Target);
                // Remember the kind while the actor is alive; destruction
                // delegates may run after IsValid(actor) has become false.
                environmentalTargets.set(this, valid(actor) && !actor.IsA(UE.Pawn.StaticClass()));
            } catch (error) { fail(error); }
        }
        StopValidating(target) {
            visibility.delete(this);
            environmentalTargets.delete(this);
            super.StopValidating(target);
        }
        ["Lock Next"]() {
            try {
                const record = activeRecord(this.Avatar, this.TargetingComponent);
                if (record?.validator === this && environmentalTargets.get(this)) {
                    // Barrel destruction must not start the native 0.4-second
                    // automatic retarget search. Enemy death keeps that path.
                    this.TargetingComponent.StopTryLockTarget();
                    this.InvalidateTarget(false);
                    return;
                }
            } catch (error) { fail(error); }
            super["Lock Next"]();
        }
        ["Path Check Timer"]() {
            try {
                const record = activeRecord(this.Avatar, this.TargetingComponent);
                if (record?.validator !== this) {
                    visibility.delete(this);
                    return super["Path Check Timer"]();
                }
                const actor = UE.ComponentSocketHelper.GetActor(this.Target);
                if (!valid(actor) || !valid(this["NB Camera"])) {
                    visibility.delete(this);
                    return super["Path Check Timer"]();
                }
                const visible = UE.TargetingUtils.IsLockPathValidAdvanced(this.Avatar, this.Target,
                    this["NB Camera"].GetCameraLocationWithCollisionTest(), true, this["Path Condition"]);
                const now = UE.GameplayStatics.GetTimeSeconds(this.Avatar);
                let state = visibility.get(this);
                if (visible) {
                    this["Path Invalid Counter"] = 0;
                    visibility.delete(this);
                } else {
                    if (!state || state.actor !== actor || now < state.since) {
                        state = { actor, since: now };
                        visibility.set(this, state);
                    }
                    if (now - state.since >= Policy.occlusionGraceSeconds) {
                        visibility.delete(this);
                        this.InvalidateTarget(false);
                    }
                }
            } catch (error) {
                fail(error);
                super["Path Check Timer"]();
            }
        }
    }
    const result = puerts.blueprint.mixin(Base, Validator, { inherit: true, objectTakeByNative: true }).StaticClass();
    generated.set(key, result);
    return result;
}

function restoreChanger(record) {
    const values = record.changerValues;
    record.changerValues = null;
    if (!values || !valid(record.changer)) return;
    for (const [name, value] of Object.entries(values))
        if (record.changer[name] === CHANGER_VALUES[name]) record.changer[name] = value;
    record.changer.SortedTargets.Empty();
}
function applyChanger(record) {
    if (record.changerValues) return;
    record.changerValues = {};
    for (const [name, value] of Object.entries(CHANGER_VALUES)) {
        record.changerValues[name] = record.changer[name];
        record.changer[name] = value;
    }
    record.changer.SortedTargets.Empty();
}
function fail(error) {
    if (session) {
        session.failed = true;
        for (const record of session.records) {
            Selection.reset(record.handler);
            try { restoreChanger(record); } catch (_) {}
        }
    }
    report("disabled-error", String(error?.stack || error));
}

function slotHandler(component, record) {
    if (record.index < 0) return component.DefaultTargetHandler;
    const entries = component.TargetingHandlers;
    if (record.index >= entries.Num()) return null;
    const entry = entries.Get(record.index);
    return String(entry.Tag.TagName) === record.mode.handlerTag ? entry.Handler : null;
}
function setSlot(component, record, handler) {
    if (record.index < 0) component.DefaultTargetHandler = handler;
    else {
        const entry = new UE.TagTargetingHandler();
        entry.Tag = tag(record.mode.handlerTag);
        entry.Handler = handler;
        component.TargetingHandlers.Set(record.index, entry);
    }
}
function ownsSlots(s) {
    return s.records.every(record => slotHandler(s.component, record) === record.handler
        && record.handler.Validator === record.validator
        && s.component.TargetChangers.Get(tag(record.mode.changerTag)) === record.changer);
}
function release() {
    if (!session) return;
    const s = session;
    session = null;
    for (const record of s.records) { restoreChanger(record); Selection.reset(record.handler); }
    if (!valid(s.component)) return;
    // Active validators own native delegates/timers. Leave their instances in
    // place, falling back to the original methods, until the player is idle.
    if (!s.component.HasLockTarget() && !valid(s.component.CurrentHandler)) {
        for (const record of s.records)
            if (slotHandler(s.component, record) === record.handler) setSlot(s.component, record, record.originalHandler);
        attached.delete(s.component);
    }
}

function requireMethods(object, names) {
    for (const name of names)
        if (typeof object[name] !== "function") throw new Error("TARGETING_INTERFACE_MISMATCH: " + name);
}
function readRecord(component, mode) {
    let index = -1;
    if (mode.handlerTag) {
        for (let i = 0; i < component.TargetingHandlers.Num(); i++) {
            if (String(component.TargetingHandlers.Get(i).Tag.TagName) === mode.handlerTag) { index = i; break; }
        }
        if (index < 0) throw new Error("TARGETING_INTERFACE_MISMATCH: " + mode.handlerTag);
    }
    const originalHandler = index < 0 ? component.DefaultTargetHandler : component.TargetingHandlers.Get(index).Handler;
    const originalValidator = component.Validators.Get(tag(mode.validatorTag));
    const changer = component.TargetChangers.Get(tag(mode.changerTag));
    if (!valid(originalHandler) || !valid(originalValidator) || !valid(changer)
        || path(originalHandler.GetClass()) !== classPath(mode.handler)
        || path(originalValidator.GetClass()) !== classPath(mode.validator)
        || String(originalHandler.ValidatorTag.TagName) !== mode.validatorTag
        || String(originalHandler.TargetChangerTag.TagName) !== mode.changerTag)
        throw new Error("TARGETING_INTERFACE_MISMATCH: " + mode.id);
    for (const name of Object.keys(CHANGER_VALUES))
        if (!Number.isFinite(changer[name])) throw new Error("TARGETING_INTERFACE_MISMATCH: " + name);
    requireMethods(changer.SortedTargets, ["Empty"]);
    requireMethods(originalHandler, ["Initialize", "FindTargetsRated"]);
    requireMethods(originalValidator, ["StartValidating", "StopValidating", "IsTargetValid",
        "InvalidateTarget", "Path Check Timer", "Lock Next"]);
    return { mode, index, originalHandler, originalValidator, changer, changerValues: null };
}

function prepareRecord(player, component, record) {
    const { originalHandler, originalValidator, mode } = record;
    const validator = UE.NewObject(createValidatorClass(originalValidator.GetClass()), component);
    const handler = UE.NewObject(createHandlerClass(originalHandler.GetClass()), component);
    if (!valid(validator) || !valid(handler)) throw new Error("TARGETING_INSTANCE_FAILED: " + mode.id);
    validator.Avatar = player;
    validator.TargetingComponent = component;
    validator.ForbidTagQuery = originalValidator.ForbidTagQuery;
    validator["Path Condition"] = originalValidator["Path Condition"];
    handler.ParamsList = originalHandler.ParamsList;
    handler.HasFilter = originalHandler.HasFilter;
    handler.IgnoredActors = originalHandler.IgnoredActors;
    handler.ValidatorTag = originalHandler.ValidatorTag;
    handler.TargetChangerTag = originalHandler.TargetChangerTag;
    // Initialize resolves ValidatorTag once. Restore the shared map immediately
    // so gun, execution and other handlers retain their original validators.
    const validatorTag = tag(mode.validatorTag);
    component.Validators.Add(validatorTag, validator);
    try {
        handler.Initialize(component, player);
        if (handler.TargetingComponent !== component || handler.Avatar !== player || handler.Validator !== validator)
            throw new Error("TARGETING_INITIALIZE_FAILED: " + mode.id);
    } finally { component.Validators.Add(validatorTag, originalValidator); }
    Object.assign(record, { handler, validator });
}

function attach(player, component) {
    const previous = attached.get(component);
    if (previous && previous.player === player && ownsSlots(previous)) { session = previous; return; }
    requireMethods(component, ["StopTryLockTarget", "GetTargetHandler"]);
    requireMethods(component.TargetingHandlers, ["Set"]);
    const records = MODES.map(mode => readRecord(component, mode));
    for (const record of records) prepareRecord(player, component, record);
    try {
        for (const record of records) setSlot(component, record, record.handler);
        for (const record of records) {
            if (slotHandler(component, record) !== record.handler
                || record.mode.handlerTag && component.GetTargetHandler(tag(record.mode.handlerTag)) !== record.handler)
                throw new Error("TARGETING_HANDLER_REGISTRATION_FAILED: " + record.mode.id);
        }
    } catch (error) {
        for (const record of records)
            if (slotHandler(component, record) === record.handler) setSlot(component, record, record.originalHandler);
        throw error;
    }
    session = { player, component, records, controller: UE.GameplayStatics.GetPlayerController(player, 0), failed: false };
    attached.set(component, session);
    report("ready", "build=26367565 modes=normal,bow policy=" + JSON.stringify(Policy));
}

function tick() {
    try {
        const context = UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
        const player = valid(context) && UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context);
        if (!valid(player) || !player.IsPlayerControlled()) { release(); failedPlayer = null; return; }
        if (session && session.player !== player) release();
        if (failedPlayer === player) return;
        if (!session) {
            const component = player.GetComponentByClass(UE.TargetingComponent.StaticClass());
            if (!valid(component)) return;
            if (!attached.has(component) && (component.HasLockTarget() || valid(component.CurrentHandler)
                || specialAction(player))) return;
            try { attach(player, component); }
            catch (error) { failedPlayer = player; throw error; }
        }
        if (session.failed) return;
        if (!ownsSlots(session)) throw new Error("TARGETING_INTERFACE_CHANGED: another targeting modifier");
        session.controller = UE.GameplayStatics.GetPlayerController(player, 0);
        const active = activeRecord(player, session.component);
        for (const record of session.records) {
            if (record === active) applyChanger(record);
            else restoreChanger(record);
            if (record !== active || !session.component.HasLockTarget()) Selection.reset(record.handler);
        }
    } catch (error) { fail(error); }
}

function install() {
    if (timer !== undefined) return;
    timer = setInterval(tick, Policy.tickMilliseconds);
    report("waiting-player");
}
module.exports = { install };
