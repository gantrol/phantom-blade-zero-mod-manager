"use strict";
const UE = require("ue");
const { valid } = require("./Telemetry"), Model = require("./Model");
const library = UE.AbilitySystemBlueprintLibrary;
function groupFor(name) {
    if (name === "Attack") return "attack";
    return Model.groups.find(group => name.startsWith(group.attribute + "_"))?.id;
}
function read(actor, side) {
    if (!valid(actor)) return [];
    const asc = library.GetAbilitySystemComponent(actor);
    if (!valid(asc)) return [];
    const handles = asc.GetActiveEffects(new UE.GameplayEffectQuery()), rows = [];
    for (let i = 0, count = Math.min(handles.Num(), 128); i < count; i++) {
        const handle = handles.Get(i);
        if (!library.IsActiveGameplayEffectHandleActive(handle)) continue;
        const effect = library.GetGameplayEffectFromActiveEffectHandle(handle);
        if (!valid(effect)) continue;
        const source = String(UE.KismetSystemLibrary.GetObjectName(effect)).replace(/^Default__/, "").replace(/_C$/, "");
        const remaining = library.GetActiveGameplayEffectRemainingDuration(actor, handle);
        const stacks = library.GetActiveGameplayEffectStackCount(handle);
        const seen = new Set();
        for (let j = 0; j < effect.Modifiers.Num(); j++) {
            const modifier = effect.Modifiers.Get(j), name = String(modifier.Attribute.AttributeName), group = groupFor(name);
            if (!group || seen.has(name)) continue;
            if ((group === "attack" ? "player" : Model.groups.find(item => item.id === group).side) !== side) continue;
            seen.add(name);
            rows.push({ group, source, side, attribute: name, op: Number(modifier.ModifierOp),
                value: asc.GetGameplayEffectMagnitude(handle, modifier.Attribute), remaining, stacks });
        }
        // These effects carry conditional rules. Their presence does not prove
        // that their weapon slot, hit type or target condition matched a hit.
        for (let j = 0; j < effect.GEComponents.Num(); j++) {
            const component = effect.GEComponents.Get(j);
            if (!valid(component)) continue;
            const type = String(UE.KismetSystemLibrary.GetObjectName(component.GetClass()));
            const group = /WeaponBuff|PreCalculation/.test(type) ? "correction" : /PreTake/.test(type) ? "preTake" : undefined;
            if (group && !seen.has(group)) {
                seen.add(group); rows.push({ group, source, side, remaining, stacks, conditional: true });
            }
        }
    }
    return rows;
}
module.exports = { read };
