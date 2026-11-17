"use strict";
const UE = require("ue"), cpp = require("puerts");
const helper = require("../Library/TsHelper").default;
const Model = require("./Model");
const { Movement } = require("./Movement");
const valid = value => !!value && UE.KismetSystemLibrary.IsValid(value);
const attributes = new Map();
function attribute(name) {
    if (!attributes.has(name)) attributes.set(name, UE.PBZBlueprintFunctionLibrary.MakeAttribute(name, UE.BasicAttributeSet.StaticClass()));
    return attributes.get(name);
}
function read(actor, name) {
    if (!valid(actor)) return;
    const found = cpp.$ref(false);
    const value = UE.AbilitySystemBlueprintLibrary.GetFloatAttribute(actor, attribute(name), found);
    return cpp.$unref(found) && Model.finite(value) ? value : undefined;
}
function copyMap(map, keyName) {
    if (!map || typeof map.GetMaxIndex !== "function") return;
    const result = {};
    for (let i = 0, n = map.GetMaxIndex(); i < n; i++) {
        if (!map.IsValidIndex(i)) continue;
        const key = map.GetKey(i), value = map.Get(key), name = keyName(key);
        if (name && Model.finite(value)) result[name] = value;
    }
    return result;
}
const tagName = tag => String(tag.TagName);
const damageMap = map => copyMap(map, tagName);
function target(player) {
    const component = player.GetComponentByClass(UE.TargetingComponent.StaticClass());
    if (!valid(component) || !component.HasLockTarget()) return;
    const socket = component.LockTarget;
    return valid(socket?.Component) ? socket.Component.GetOwner() : undefined;
}
function identity(payload) {
    const p = payload.HitLocation;
    return { source: payload.DamageSource, target: payload.TargetActor, cause: tagName(payload.DamageCause), x: p.X, y: p.Y, z: p.Z };
}
function same(a, b) {
    return a.source === b.source && a.target === b.target && a.cause === b.cause && a.x === b.x && a.y === b.y && a.z === b.z;
}
function pendingIndex(entries, key) {
    for (let i = entries.length - 1; i >= 0; i--) if (same(entries[i].key, key)) return i;
    return -1;
}
class Telemetry {
    constructor() { this.bindings = []; this.pending = []; this.clock = 0; this.errors = new Set(); this.movement = new Movement(); }
    report(kind, error) {
        if (this.errors.has(kind)) return;
        this.errors.add(kind);
        console.log("[PhantomBladeDamageMonitor] " + kind + " " + String(error));
    }
    bind(player) {
        if (this.player === player) return;
        this.dispose(); this.player = player;
        if (!valid(player)) return;
        for (const [name, callback] of [
            ["GameplayEvent.Damage.Pretake", data => this.before(data)],
            ["GameplayEvent.Damage.Taken", data => this.taken(data)],
            ["GameplayEvent.Accessory.AnimMovement.ApplyToBullet", bullet => this.movement.capture(player, bullet)]
        ]) {
            const action = UE.AbilityAsync_WaitGameplayEvent.WaitGameplayEventToActor(player,
                helper.constructGameplayTag(name), false, true);
            if (!valid(action)) continue;
            const handler = event => {
                try { if (valid(event.OptionalObject)) callback(event.OptionalObject); }
                catch (error) {
                    this.report(name, error);
                    if (name.startsWith("GameplayEvent.Damage.")) this.pending = [];
                }
            };
            this.bindings.push({ action, handler });
            action.EventReceived.Add(handler); action.Activate();
        }
    }
    before(payload) {
        if (!valid(payload.TargetActor) || payload.TargetActor === this.player) return;
        const key = identity(payload), priority = Number(payload.CurrentPriority);
        let entry = this.pending[pendingIndex(this.pending, key)];
        if (priority === 0) {
            // Copy borrowed native maps during the callback. Payloads are reused
            // by the game; retaining them would mix successive hits or targets.
            const attacker = copyMap(payload.GetAttackerAttributes(), key => String(key.AttributeName));
            entry = { key, at: this.clock, stages: {}, player: {}, target: {} };
            entry.player.Attack = attacker?.Attack ?? read(this.player, "Attack");
            for (const name of ["ResistanceMin_Valid", "ResistanceMin", "Shield", "Shield_Max"])
                entry.target[name] = read(key.target, name);
            for (const group of Model.groups)
                for (const family of ["Physical", "PhysicalSharp", "PhysicalBlunt", "Elemental", "Fire", "Lightning", "Toxin", "Ice", "Aqua", "Blood", "Chaos"]) {
                    const name = group.attribute + "_" + family;
                    entry[group.side][name] = group.side === "player" ? attacker?.[name] ?? read(this.player, name) : read(key.target, name);
                }
            this.pending.push(entry);
            if (this.pending.length > 16) this.pending.shift();
        }
        if (entry && priority >= 0 && priority <= 2) entry.stages[priority] = damageMap(payload.GetOriginDamageData());
    }
    taken(payload) {
        if (payload.GetDamageSourceOwner() !== this.player || !valid(payload.TargetActor) || payload.TargetActor === this.player) return;
        const key = identity(payload), index = pendingIndex(this.pending, key);
        const entry = index >= 0 ? this.pending.splice(index, 1)[0] : undefined;
        const damage = damageMap(payload.GetDamageData());
        const attack = damageMap(payload.GetAttackData());
        if (!damage) return;
        const lanes = Object.entries(attack || {}).filter(([, value]) => value > 0)
            .map(([tag]) => Model.lane(tag)).filter(Boolean);
        // A sum of different damage types cannot be represented as one chain.
        // Keep its native total, but leave the single-lane formula unavailable.
        let single = lanes.length === 1 ? lanes[0] : undefined;
        const total = Model.healthDamage(damage);
        if (single) {
            const tag = single.id === "True" ? "Damage.Damage.Target.TrueDamage" : "Damage.Damage.Target.GeneralDamage." + single.id;
            if (!Model.finite(damage[tag]) || Math.abs(damage[tag] - total) > 0.001) single = undefined;
        }
        const snapshot = entry && single ? Model.factors((side, name) => entry[side][name], single.families) : { values: {}, attributes: {} };
        const damageTag = single?.id === "True" ? "Damage.Damage.Target.TrueDamage" : "Damage.Damage.Target.GeneralDamage." + single?.id;
        const origin = entry?.stages[0]?.[damageTag], final = damage[damageTag];
        snapshot.values.correction = Model.ratio(origin, Model.product([snapshot.values.attack, ...Model.groups.map(group => snapshot.values[group.id])]));
        snapshot.values.preTake = Model.ratio(final, origin);
        this.last = { ...snapshot, at: this.clock, target: key.target, lane: single?.id, total,
            hitMovement: this.movement.forHit(key.source, attack),
            damage, stages: entry?.stages, shield: entry?.target.Shield, shieldMax: entry?.target.Shield_Max,
            source: "hit" };
    }
    sample(deltaSeconds) {
        this.clock += Math.max(0, deltaSeconds);
        this.pending = this.pending.filter(item => this.clock - item.at < 1 && valid(item.key.target));
        if (!valid(this.player)) return;
        const locked = target(this.player);
        if (this.last && (!valid(this.last.target) || locked && locked !== this.last.target || this.clock - this.last.at > 4)) this.last = undefined;
        let currentMovement;
        try { currentMovement = this.movement.read(this.player); }
        catch (error) { this.report("movement", error); }
        if (this.last) return { ...this.last, currentMovement };
        const snapshot = Model.factors((side, name) => read(side === "player" ? this.player : locked, name), ["Physical"]);
        return { ...snapshot, target: locked, lane: "Physical", source: "live", total: undefined, currentMovement,
            shield: read(locked, "Shield"), shieldMax: read(locked, "Shield_Max") };
    }
    dispose() {
        for (const { action, handler } of this.bindings) if (valid(action)) {
            action.EventReceived.Remove(handler); action.Cancel();
        }
        this.bindings = []; this.pending = []; this.last = undefined; this.player = undefined;
        this.movement.clear();
    }
}
module.exports = { Telemetry, valid, attribute, read };
