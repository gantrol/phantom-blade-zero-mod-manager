"use strict";
const UE = require("ue"), Model = require("./Model");
const helper = require("../Library/TsHelper").default;
const valid = object => !!object && UE.KismetSystemLibrary.IsValid(object);

// GA_Accessory_AnimMovement reads these reflected instance fields when it
// receives ApplyToBullet. Read the same state; never integrate movement twice
// or apply this contribution again to the game's settled damage.
class Movement {
    constructor() {
        this.tags = [1, 2].map(level => helper.constructGameplayTag("CharacterState.Accessory.AnimMovement.Lv" + level));
        this.clear();
    }
    clear() { this.actor = undefined; this.ability = undefined; this.level = undefined; this.bullets = new WeakMap(); }
    read(actor) {
        if (!valid(actor)) return;
        const asc = UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(actor);
        if (!valid(asc)) return;
        const level = asc.HasMatchingGameplayTag(this.tags[1]) ? 2 : asc.HasMatchingGameplayTag(this.tags[0]) ? 1 : undefined;
        if (this.actor !== actor || this.level !== level) { this.ability = undefined; this.actor = actor; this.level = level; }
        if (!level) return;
        if (!valid(this.ability)) {
            const specs = asc.ActivatableAbilities.Items;
            for (let i = 0; i < specs.Num() && !this.ability; i++) {
                const spec = specs.Get(i);
                if (spec.ActiveCount <= 0 || spec.PendingRemove || !valid(spec.Ability)) continue;
                const name = String(UE.KismetSystemLibrary.GetObjectName(spec.Ability.GetClass()));
                if (name !== "GA_Accessory_AnimMovement_Lv" + level + "_C") continue;
                // The spec's Ability is a class default object, not live state.
                for (const instances of [spec.NonReplicatedInstances, spec.ReplicatedInstances]) {
                    for (let j = 0; j < instances.Num(); j++) {
                        const instance = instances.Get(j);
                        if (valid(instance)) { this.ability = instance; break; }
                    }
                    if (this.ability) break;
                }
            }
        }
        if (!valid(this.ability)) return { level };
        const distance = this.ability["Total Distance"], perUnit = this.ability["Distance Damage Multiplier"], cap = this.ability["Max Amplification"];
        if (![distance, perUnit, cap].every(value => Model.finite(value) && value >= 0)) return { level };
        const attackTags = this.ability["Attack Tags"], tags = [];
        for (let i = 0; i < attackTags.Num(); i++) tags.push(String(attackTags.Get(i)));
        const bonus = Math.fround(Math.min(distance * perUnit, cap));
        return { level, distance: distance / 100, bonus, multiplier: 1 + bonus, cap, tags };
    }
    capture(actor, bullet) {
        if (!valid(bullet)) return;
        const state = this.read(actor);
        if (state) this.bullets.set(bullet, state);
        else this.bullets.delete(bullet);
        if (typeof bullet.FindModulesByClass === "function") {
            const modules = bullet.FindModulesByClass(UE.BulletAttackModule.StaticClass());
            for (let i = 0; i < modules.Num(); i++) {
                const module = modules.Get(i);
                if (!valid(module)) continue;
                if (state) this.bullets.set(module, state);
                else this.bullets.delete(module);
            }
        }
    }
    forHit(source, attack) {
        const state = source && this.bullets.get(source);
        if (!state?.tags || !attack) return;
        // Do not infer a contribution for another damage type or an attack
        // emitted before the monitor subscribed. Missing evidence stays blank.
        return Object.entries(attack).some(([tag, value]) => value > 0 && state.tags.includes(tag)) ? state : undefined;
    }
}
module.exports = { Movement };
