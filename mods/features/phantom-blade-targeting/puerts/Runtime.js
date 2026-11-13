"use strict";
const UE = require("ue");
const puerts = require("puerts");
const Policy = require("./Policy");

const PREFIX = "/PBZCombatAsset/Prototypes/Targeting/";
const HANDLER = PREFIX + "PBZ_TargetingHandler_CombatLock.PBZ_TargetingHandler_CombatLock_C";
const VALIDATOR = PREFIX + "PBZ_LockTargetValidator_CombatDefault.PBZ_LockTargetValidator_CombatDefault_C";
const CHANGER_TAG = tag("Targeting.TargetChanger.CombatNormal");
const VALIDATOR_TAG = tag("Targeting.Validator.Combat.Default");
const DEAD = tag("CharacterState.Dead");
const SPECIAL_TAGS = ["CharacterState.AbilityState.InSpecialPerform",
    "CharacterState.Native.Sequence", "CharacterState.HitReact.State.Executing",
    "CharacterState.DeflectTaskActive", "CharacterState.Parry.Lock", "CharacterState.Deflect.Stance",
    "CharacterState.Stand.Invulnerable.Dodge", "CharacterState.Stand.Invulnerable.Execution",
    "Ability.Action.Hun.PerfectDodge", "Ability.Action.Execution", "Ability.Action.Hun.SimpleExecution"].map(tag);
const CHANGER_VALUES = {
    // Match reflected float32 values so restoration compares exact values.
    JoystickActiveValue: Math.fround(Policy.stickActive),
    JoystickDeactiveValue: Math.fround(Policy.stickRelease),
    JoystickChangeTargetQuickDelay: Math.fround(Policy.repeatDelay),
    JoystickChangeTargetQuickCooldown: Math.fround(Policy.repeatCooldown),
    // The wheel / explicit previous-next actions retain their native mappings.
    MouseActiveValue: 1e9
};

let timer, session = null, generated = null, failedPlayer = null;
let lastReport = "";
const visibility = new WeakMap();
const attached = new WeakMap();

function tag(name) { const value = new UE.GameplayTag(); value.TagName = name; return value; }
function valid(object) { return !!object && UE.KismetSystemLibrary.IsValid(object); }
function path(object) { return String(UE.KismetSystemLibrary.GetPathName(object)); }
function report(state, detail = "") {
    const message = state + (detail ? " " + detail : "");
    if (message === lastReport) return;
    lastReport = message;
    console.log("[PhantomBladeTargeting] " + message);
}
function abilitySystem(actor) { return UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(actor); }
function specialAction(player) {
    const asc = abilitySystem(player);
    if (!valid(asc)) return true;
    return SPECIAL_TAGS.some(value => asc.HasMatchingGameplayTag(value));
}
function ordinary(component) {
    // Aim, execution, combat snap, dodge and soft-focus handlers have their own
    // rules. Never replace their target, validator or candidate list.
    return valid(component) && (!valid(component.CurrentHandler)
        || component.CurrentHandler === component.DefaultTargetHandler);
}
function active(owner, component) {
    return session && !session.failed && owner === session.player
        && component === session.component && component.DefaultTargetHandler === session.handler
        && ordinary(component) && !specialAction(owner);
}
function enemy(actor, player) {
    if (!valid(actor) || !actor.IsA(UE.Pawn.StaticClass())) return false;
    const asc = abilitySystem(actor);
    return valid(asc) && !asc.HasMatchingGameplayTag(DEAD)
        && UE.FactionUtils.GetFactionRelation(player, actor) === 2; // ETeamAttitude::Hostile
}

function preferEnemies(handler, targets) {
    const count = targets.Num();
    if (!count || count > Policy.maxCandidates) return;
    const preferred = [];
    for (let i = 0; i < count; i++) {
        const candidate = targets.Get(i);
        const actor = UE.ComponentSocketHelper.GetActor(candidate.Target);
        if (enemy(actor, handler.Avatar)) {
            const copy = new UE.TargetingRatedTarget();
            const socket = new UE.ComponentSocket();
            socket.Component = candidate.Target.Component;
            socket.SocketName = candidate.Target.SocketName;
            copy.Target = socket;
            copy.Score = candidate.Score;
            copy.SocketScore = candidate.SocketScore;
            preferred.push({ target: copy, rank: 0 });
        }
    }
    // Only rank candidates that already passed the game's distance, angle,
    // visibility, ignored-actor and state checks. Environmental targets remain
    // available when there is no eligible enemy.
    if (!preferred.length) return;
    const acquiring = !handler.TargetingComponent.HasLockTarget();
    const camera = acquiring && UE.GameplayStatics.GetPlayerCameraManager(handler.Avatar, 0);
    const origin = valid(camera) ? camera.GetCameraLocation() : null;
    const forward = origin && UE.KismetMathLibrary.GetForwardVector(camera.GetCameraRotation());
    for (const candidate of preferred) {
        if (forward) {
            const actor = UE.ComponentSocketHelper.GetActor(candidate.target.Target);
            const point = actor.K2_GetActorLocation();
            const dx = point.X - origin.X, dy = point.Y - origin.Y, dz = point.Z - origin.Z;
            const distance = Math.hypot(dx, dy, dz);
            const alignment = distance > 1 ? (dx * forward.X + dy * forward.Y + dz * forward.Z) / distance : 1;
            candidate.rank = alignment * 100 - Math.min(distance / 2800, 1);
        }
    }
    // FindTarget takes the first result. Sort initial acquisition explicitly;
    // keep native Score / SocketScore and directional-switch ordering intact.
    if (forward) preferred.sort((a, b) => b.rank - a.rank);
    targets.Empty();
    for (const candidate of preferred) targets.Add(candidate.target);
}

function createClasses(handlerClass, validatorClass) {
    const HandlerBase = puerts.blueprint.tojs(handlerClass);
    const ValidatorBase = puerts.blueprint.tojs(validatorClass);
    // Inherited mixins leave the original classes and all other instances intact.
    // The intermediate prototypes make super calls use the original UFunctions.
    class HandlerParent {}
    Object.setPrototypeOf(HandlerParent.prototype, HandlerBase.prototype);
    class Handler extends HandlerParent {
        FindTargetsRated(ignoredActors, targetsRef) {
            super.FindTargetsRated(ignoredActors, targetsRef);
            try {
                if (active(this.Avatar, this.TargetingComponent))
                    preferEnemies(this, puerts.$unref(targetsRef));
            }
            catch (error) { fail(error); }
        }
    }
    class ValidatorParent {}
    Object.setPrototypeOf(ValidatorParent.prototype, ValidatorBase.prototype);
    class Validator extends ValidatorParent {
        StartValidating(target) {
            visibility.delete(this);
            super.StartValidating(target);
        }
        StopValidating(target) {
            visibility.delete(this);
            super.StopValidating(target);
        }
        ["Path Check Timer"]() {
            try {
                if (!active(this.Avatar, this.TargetingComponent)) {
                    visibility.delete(this);
                    return super["Path Check Timer"]();
                }
                const actor = UE.ComponentSocketHelper.GetActor(this.Target);
                if (!valid(actor) || !valid(this["NB Camera"])) {
                    visibility.delete(this);
                    return super["Path Check Timer"]();
                }
                // Death, unselectable tags, faction changes and destruction are
                // still handled by the original validator and its delegates.
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
    return {
        handler: puerts.blueprint.mixin(HandlerBase, Handler, { inherit: true, objectTakeByNative: true }).StaticClass(),
        validator: puerts.blueprint.mixin(ValidatorBase, Validator, { inherit: true, objectTakeByNative: true }).StaticClass()
    };
}

function restoreChanger() {
    if (!session?.changerValues) return;
    const values = session.changerValues;
    session.changerValues = null;
    if (!valid(session.changer)) return;
    for (const [name, value] of Object.entries(values))
        if (session.changer[name] === CHANGER_VALUES[name]) session.changer[name] = value;
}
function fail(error) {
    if (session) session.failed = true;
    try { restoreChanger(); } catch (_) {}
    report("disabled-error", String(error?.stack || error));
}
function release() {
    if (!session) return;
    restoreChanger();
    const s = session;
    session = null;
    if (!valid(s.component)) return;
    // Do not tear down a validator while its original delegates/timer are active.
    // Its mixin falls back to the original implementation after session ends.
    if (!s.component.HasLockTarget()) {
        if (s.component.DefaultTargetHandler === s.handler) s.component.DefaultTargetHandler = s.originalHandler;
        if (s.component.Validators.Get(VALIDATOR_TAG) === s.validator)
            s.component.Validators.Add(VALIDATOR_TAG, s.originalValidator);
        attached.delete(s.component);
    }
}

function attach(player, component) {
    const previous = attached.get(component);
    if (previous && previous.player === player && component.DefaultTargetHandler === previous.handler
        && component.Validators.Get(VALIDATOR_TAG) === previous.validator) {
        session = previous;
        return true;
    }
    const originalHandler = component.DefaultTargetHandler;
    const originalValidator = component.Validators.Get(VALIDATOR_TAG);
    const changer = component.TargetChangers.Get(CHANGER_TAG);
    if (!valid(originalHandler) || !valid(originalValidator) || !valid(changer)) return false;
    if (path(originalHandler.GetClass()) !== HANDLER || path(originalValidator.GetClass()) !== VALIDATOR)
        throw new Error("TARGETING_INTERFACE_MISMATCH: default handler/validator (or another targeting mod)");
    for (const name of Object.keys(CHANGER_VALUES))
        if (!Number.isFinite(changer[name])) throw new Error("TARGETING_INTERFACE_MISMATCH: " + name);
    for (const [object, names] of [[originalHandler, ["Initialize", "FindTargetsRated"]],
        [originalValidator, ["StartValidating", "StopValidating", "IsTargetValid", "InvalidateTarget", "Path Check Timer"]]])
        for (const name of names)
            if (typeof object[name] !== "function") throw new Error("TARGETING_INTERFACE_MISMATCH: " + name);
    if (!generated) generated = createClasses(originalHandler.GetClass(), originalValidator.GetClass());
    const validator = UE.NewObject(generated.validator, component);
    const handler = UE.NewObject(generated.handler, component);
    if (!valid(validator) || !valid(handler)) throw new Error("TARGETING_INSTANCE_FAILED");
    validator.Avatar = player;
    validator.TargetingComponent = component;
    validator.ForbidTagQuery = originalValidator.ForbidTagQuery;
    validator["Path Condition"] = originalValidator["Path Condition"];
    handler.ParamsList = originalHandler.ParamsList;
    handler.ValidatorTag = originalHandler.ValidatorTag;
    handler.TargetChangerTag = originalHandler.TargetChangerTag;
    component.Validators.Add(VALIDATOR_TAG, validator);
    try {
        handler.Initialize(component, player);
        if (handler.TargetingComponent !== component || handler.Avatar !== player || handler.Validator !== validator)
            throw new Error("TARGETING_INITIALIZE_FAILED");
        component.DefaultTargetHandler = handler;
    } catch (error) {
        component.Validators.Add(VALIDATOR_TAG, originalValidator);
        throw error;
    }
    session = { player, component, changer, originalHandler, originalValidator, handler, validator,
        changerValues: null, failed: false };
    attached.set(component, session);
    report("ready", "build=26367565 policy=" + JSON.stringify(Policy));
    return true;
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
            try { if (!attach(player, component)) return; }
            catch (error) { failedPlayer = player; throw error; }
        }
        if (session.failed) return;
        if (session.component.DefaultTargetHandler !== session.handler
            || session.component.Validators.Get(VALIDATOR_TAG) !== session.validator)
            throw new Error("TARGETING_INTERFACE_CHANGED: another targeting modifier");
        const pc = UE.GameplayStatics.GetPlayerController(player, 0);
        const usable = valid(pc) && !pc.bCinematicMode && !pc.IsLookInputIgnored()
            && !UE.GameplayStatics.IsGamePaused(player) && ordinary(session.component) && !specialAction(player);
        if (!usable) { restoreChanger(); return; }
        if (!session.changerValues) {
            session.changerValues = {};
            for (const [name, value] of Object.entries(CHANGER_VALUES)) {
                session.changerValues[name] = session.changer[name];
                session.changer[name] = value;
            }
        }
    } catch (error) { fail(error); }
}

function install() {
    if (timer !== undefined) return;
    timer = setInterval(tick, Policy.tickMilliseconds);
    report("waiting-player");
}
module.exports = { install };
