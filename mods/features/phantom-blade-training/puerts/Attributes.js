"use strict";
const { UE, cpp, valid } = require("./Engine");
const resources = ["Health", "Shachi", "UltimateCharge", "UltimateChargeCount", "SpiritShadow"];
const attribute = name => UE.PBZBlueprintFunctionLibrary.MakeAttribute(name, UE.BasicAttributeSet.StaticClass());
const read = (actor, name) => valid(actor) ? UE.AttributeUtils.GetCharacterAttributeValue(actor, attribute(name)) : 0;
const write = (actor, name, value) => UE.AttributeUtils.ModifyCharacterAttributeValue(actor, attribute(name), UE.EGameplayModOp.Override, value);
const asc = actor => UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(actor);
let effectType;
function type() {
    if (effectType) return effectType;
    class PhantomBladeArenaAttributes extends UE.GameplayEffect {
        Constructor() {
            this.DurationPolicy = UE.EGameplayEffectDurationType.Infinite;
            const chance = new UE.ScalableFloat(); chance.Value = 1; this.ChanceToApplyToTarget = chance;
            // SetByCaller defaults to the neutral multiplier for scaling. Health
            // overrides use separate transient classes so unrelated fields never
            // receive an absent SetByCaller value.
            for (const name of ["Health_Max", "Attack", "ShachiAttack"]) {
                const modifier = new UE.GameplayModifierInfo(); modifier.Attribute = attribute(name);
                modifier.ModifierOp = UE.EGameplayModOp.MultiplyCompound;
                const magnitude = new UE.GameplayEffectModifierMagnitude();
                magnitude.MagnitudeCalculationType = UE.EGameplayEffectMagnitudeCalculation.SetByCaller;
                const caller = new UE.SetByCallerFloat(); caller.DataName = "Arena." + name;
                magnitude.SetByCallerMagnitude = caller; modifier.ModifierMagnitude = magnitude;
                this.Modifiers.Add(modifier);
            }
        }
    }
    Object.defineProperty(PhantomBladeArenaAttributes, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
    effectType = cpp.makeUClass(PhantomBladeArenaAttributes);
    return effectType;
}
const overrides = new Map();
function overrideType(name) {
    if (overrides.has(name)) return overrides.get(name);
    class PhantomBladeArenaOverride extends UE.GameplayEffect {
        Constructor() {
            this.DurationPolicy = UE.EGameplayEffectDurationType.Infinite;
            const chance = new UE.ScalableFloat(); chance.Value = 1; this.ChanceToApplyToTarget = chance;
            const modifier = new UE.GameplayModifierInfo(); modifier.Attribute = attribute(name); modifier.ModifierOp = UE.EGameplayModOp.Override;
            const magnitude = new UE.GameplayEffectModifierMagnitude(); magnitude.MagnitudeCalculationType = UE.EGameplayEffectMagnitudeCalculation.SetByCaller;
            const caller = new UE.SetByCallerFloat(); caller.DataName = "Arena.Value";
            magnitude.SetByCallerMagnitude = caller; modifier.ModifierMagnitude = magnitude; this.Modifiers.Add(modifier);
        }
    }
    Object.defineProperty(PhantomBladeArenaOverride, "name", { value: "PhantomBladeArenaOverride_" + name });
    Object.defineProperty(PhantomBladeArenaOverride, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
    const result = cpp.makeUClass(PhantomBladeArenaOverride); overrides.set(name, result); return result;
}
function apply(actor, effect, values) {
    const component = asc(actor), spec = component.MakeOutgoingSpec(effect, 1, component.MakeEffectContext());
    for (const [name, amount] of Object.entries(values)) UE.AbilitySystemBlueprintLibrary.AssignSetByCallerMagnitude(spec, "Arena." + name, amount);
    const handle = component.BP_ApplyGameplayEffectSpecToSelf(spec);
    if (handle.Handle === -1) throw new Error("arena_effect_rejected");
    return handle;
}
function limit(actor, amount) {
    const handle = apply(actor, overrideType("Health_Max"), { Value: amount });
    if (Math.abs(read(actor, "Health_Max") - amount) > .1) throw new Error("arena_health_override_failed");
    return handle;
}
function tune(actor, config) {
    if (config.bossHealth !== 100 || config.bossAttack !== 100)
        apply(actor, type(), { Health_Max: config.bossHealth / 100, Attack: config.bossAttack / 100, ShachiAttack: config.bossAttack / 100 });
    write(actor, "Health", read(actor, "Health_Max"));
}
function lock(actor) {
    const amount = read(actor, "Health"), handle = apply(actor, overrideType("Health"), { Value: amount });
    return () => { if (valid(actor)) { write(actor, "Health", amount); asc(actor).RemoveActiveGameplayEffect(handle, -1); } };
}
const capture = actor => Object.fromEntries(resources.map(name => [name, read(actor, name)]));
const refill = actor => resources.forEach(name => write(actor, name, read(actor, name + "_Max")));
const restore = (actor, values) => Object.entries(values).forEach(([name, value]) => write(actor, name, value));
module.exports = { read, write, asc, capture, refill, restore, limit, tune, lock };
