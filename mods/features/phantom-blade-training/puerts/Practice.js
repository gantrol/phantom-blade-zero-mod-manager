"use strict";
const E = require("./Engine"), A = require("./Attributes"), Assets = require("PhantomBladeRuntime/Assets");
const catalog = require("./PracticeCatalog.generated");
const { UE, cpp, valid, Helper } = E;
const emptySample = () => ({ damage: 0, ally: 0, other: 0, hits: 0, last: 0, maximum: 0, conversions: 0, elapsed: 0 });
const tags = names => { const out = new UE.GameplayTagContainer(); for (const name of names) out.GameplayTags.Add(Helper.constructGameplayTag(name)); return out; };
const exclude = actor => UE.AbilitySystemBlueprintLibrary.AddLooseGameplayTags(actor, tags(["CharacterType.AI.ExcludeFromBossRecord"]), false);
const controlled = actor => String(actor.GetFactionComponent().GetFaction().TagName) === "Faction.Player";
const controlPath = "/PBZCombatAsset/ActorData/Players/Hun/ShaqiWeapon/Lute/UpgradeData/GE_Weapon_Lute_ControlMonster.GE_Weapon_Lute_ControlMonster_C";
class Practice {
    constructor() { this.targets = []; this.props = []; this.bindings = []; this.clear(); }
    clear() {
        this.previous = this.sample || emptySample(); this.sample = emptySample(); this.since = null;
        for (const target of this.targets) { target.previous = target.sample; target.sample = emptySample(); }
    }
    load(selection, complete) {
        const paths = [controlPath];
        for (const config of selection.targets.slice(0, selection.practice === "dummy" ? 1 : 16)) {
            const kind = catalog.kinds.find(k => k.id === config.kind), motion = kind?.motions.find(m => m.id === config.motion);
            if (!motion) throw new Error("arena_practice_motion_invalid");
            paths.push(kind.actorClass, ...kind.motions.map(m => m.sequence));
        }
        // The live editor can change a target's kind without a level reload.
        // Preload its finite catalogue once; no synchronous asset loads in Tick.
        for (const kind of catalog.kinds) paths.push(kind.actorClass, ...kind.motions.map(m => m.sequence));
        for (const config of selection.props) paths.push(catalog.props.find(p => p.id === config.kind).actorClass);
        this.loadHandle = Assets.load(E.context(), [...new Set(paths)], loaded => {
            this.loadHandle = null;
            if (loaded.some(asset => !valid(asset))) { this.error = new Error("arena_practice_asset_missing"); return; }
            this.assets = new Map([...new Set(paths)].map((path, i) => [path, loaded[i]])); complete();
        });
    }
    begin(anchor, selection) {
        this.anchor = anchor; this.selection = selection; this.player = E.player();
        this.clear(); this.equipment = this.equipmentSignature(); anchor.ShouldSpawn = false;
        const gate = JSON.parse(cpp.loadModule("pbz-arena-v1/combat/arm"));
        if (gate.error) throw new Error(gate.error);
        // This attempted add is consumed before count/listener updates. The
        // shared bridge scopes the gate to this player's native tag container.
        UE.AbilitySystemBlueprintLibrary.AddLooseGameplayTags(this.player, tags(["CharacterState.InCombat"]), false);
        const gated = JSON.parse(cpp.loadModule("pbz-arena-v1/combat/result"));
        if (gated.error || A.asc(this.player).HasMatchingGameplayTag(Helper.constructGameplayTag("CharacterState.InCombat")))
            throw new Error("arena_practice_combat_gate_failed");
        if (valid(anchor.SpawnedAI)) anchor.SpawnedAI.K2_DestroyActor();
        this.player.Tags.Add("PhantomBladeArena.SkillEditing");
        const targets = selection.targets.slice(0, selection.practice === "dummy" ? 1 : 16);
        // Validate all ground positions before mutating the set of targets.
        const placements = targets.map(config => this.placement(config, 150));
        targets.forEach((config, i) => { const target = { config: E.copy(config), transform: placements[i], sample: emptySample() }; this.targets.push(target); this.spawn(target); });
        for (const config of selection.props) {
            const meta = catalog.props.find(p => p.id === config.kind), transform = this.placement(config, meta.groundOffset);
            const actor = UE.GameplayStatics.BeginDeferredActorSpawnFromClass(this.player, this.assets.get(meta.actorClass), transform,
                UE.ESpawnActorCollisionHandlingMethod.DontSpawnIfColliding, this.player, UE.ESpawnActorScaleMethod.MultiplyWithRoot);
            if (!valid(actor)) throw new Error("arena_prop_spawn_failed");
            actor.bRespawnOnPlayerRevive = false;
            UE.GameplayStatics.FinishSpawningActor(actor, transform, UE.ESpawnActorScaleMethod.MultiplyWithRoot);
            this.props.push({ config, actor });
        }
        this.listen(this.player);
        if (selection.fixedStart) this.resetPlayer();
    }
    placement(config, height) {
        const anchor = this.anchor.K2_GetActorTransform();
        const position = UE.KismetMathLibrary.TransformLocation(anchor, new UE.Vector(config.x, config.y, 0));
        const hit = E.trace(new UE.Vector(position.X, position.Y, position.Z + 500), new UE.Vector(position.X, position.Y, position.Z - 1500), E.actors(UE.PBZCharacter.StaticClass()));
        if (!hit || hit.ImpactNormal.Z < .5) throw new Error("arena_target_ground_missing");
        const rotation = this.anchor.K2_GetActorRotation(); rotation.Yaw += config.yaw;
        return UE.KismetMathLibrary.MakeTransform(new UE.Vector(hit.ImpactPoint.X, hit.ImpactPoint.Y, hit.ImpactPoint.Z + height), rotation, new UE.Vector(1, 1, 1));
    }
    spawn(target) {
        this.removeTargetActor(target);
        const type = this.anchor.GetClass(), kind = catalog.kinds.find(k => k.id === target.config.kind);
        const spawner = UE.GameplayStatics.BeginDeferredActorSpawnFromClass(this.player, type, target.transform,
            UE.ESpawnActorCollisionHandlingMethod.AdjustIfPossibleButAlwaysSpawn, this.player, UE.ESpawnActorScaleMethod.MultiplyWithRoot);
        if (!valid(spawner)) throw new Error("arena_target_spawn_failed");
        target.spawner = spawner; spawner.SpawnerID = "ArenaPractice_" + target.config.id;
        spawner.ShouldSpawn = false; spawner.bDisableNPCOptimization = true;
        const info = new UE.AISpawnInfo(); info.MobDomain = this.anchor.SpawnInfo.MobDomain;
        info.MobGroupName = "ArenaPractice"; info.bAutoStartAI = false; info.bStartAIOnPlayerEnterDomain = false;
        info.bStopAIOnPlayerExitDomain = false; info.bHiddenByDefault = false; spawner.SpawnInfo = info;
        spawner.SetClassToSpawn(UE.KismetSystemLibrary.Conv_ClassToSoftClassReference(this.assets.get(kind.actorClass)));
        UE.GameplayStatics.FinishSpawningActor(spawner, target.transform, UE.ESpawnActorScaleMethod.MultiplyWithRoot);
        spawner.ShouldSpawn = true; spawner.Respawn(); target.deadline = Date.now() + 90000; target.state = "targetSpawning";
    }
    listen(actor, target) {
        const action = UE.AbilityAsync_WaitGameplayEvent.WaitGameplayEventToActor(actor,
            Helper.constructGameplayTag("GameplayEvent.Damage.Taken"), false, true);
        const callback = event => {
            try {
                const payload = event.OptionalObject;
                if (!valid(payload) || payload.GetDamageSourceOwner() !== actor) return;
                const recipient = this.targets.find(t => t.actor === payload.TargetActor);
                if (!recipient) return;
                const map = payload.GetDamageData(); let amount = 0;
                for (const [tag, value] of map) {
                    const name = String(tag.TagName);
                    if (name.startsWith("Damage.Damage.Target.GeneralDamage.") || name.startsWith("Damage.Damage.Target.StatusDamage.")
                        || name === "Damage.Damage.Target.TrueDamage") amount += Number(value);
                }
                if (!(amount > 0 && Number.isFinite(amount))) return;
                const equipment = this.equipmentSignature();
                if (equipment !== this.equipment) { this.clear(); this.equipment = equipment; }
                if (this.since === null) this.since = UE.GameplayStatics.GetTimeSeconds(this.player);
                this.thaw(recipient); recipient.recoverUntil = Date.now() + 250;
                for (const sample of [this.sample, recipient.sample]) {
                    if (actor === this.player) { sample.damage += amount; sample.last = amount; sample.maximum = Math.max(sample.maximum, amount); sample.hits++; }
                    else if (controlled(actor)) sample.ally += amount;
                    else sample.other += amount;
                }
                if (target && controlled(actor)) target.output = (target.output || 0) + amount;
            } catch (error) { this.error = error; }
        };
        action.EventReceived.Add(callback); action.Activate();
        const binding = { action, callback }; this.bindings.push(binding); if (target) target.binding = binding;
    }
    thaw(target) {
        if (!valid(target.actor)) return;
        target.actor.Mesh.GlobalAnimRateScale = target.rate ?? 1;
        if (valid(target.montage)) target.anim.Montage_Stop(0, target.montage);
        target.montage = null;
    }
    tick() {
        if (this.error) throw this.error;
        let ready = this.targets.length > 0;
        for (const target of this.targets) {
            const actor = target.spawner.SpawnedAI;
            if (!valid(actor) || target.spawner.IsSpawning || (!target.actor && A.read(actor, "Health") <= 0)) {
                if (Date.now() > target.deadline) throw new Error("arena_target_spawn_timeout");
                ready = false; continue;
            }
            if (target.actor !== actor) {
                const ai = actor.GetComponentByClass(UE.SGameAIComponent.StaticClass());
                if (!valid(ai) || !ai.bInitialized) { ready = false; continue; }
                Object.assign(target, { actor, ai, anim: actor.Mesh.GetAnimInstance(), rate: actor.Mesh.GlobalAnimRateScale, ally: false, recoverUntil: Date.now() + 250 });
                ai.StopAI(); exclude(actor); A.tune(actor, this.selection.config); target.unlock = A.lock(actor); this.listen(actor, target);
            }
            const ally = controlled(actor), query = new UE.GameplayEffectQuery(); query.EffectDefinition = this.assets.get(controlPath);
            if (A.read(actor, "Health") <= 0 || target.ally && A.asc(actor).GetActiveEffects(query).Num() === 0) { this.spawn(target); ready = false; continue; }
            if (ally !== target.ally) {
                target.ally = ally; this.thaw(target); target.recoverUntil = Date.now() + 250;
                if (ally) { this.sample.conversions++; target.sample.conversions++; }
            }
            if (target.ai.bAIStarted !== ally) ally ? target.ai.StartAI() : target.ai.StopAI();
            const montage = target.anim.GetCurrentActiveMontage();
            const reacting = A.asc(actor).HasAnyMatchingGameplayTags(tags(["CharacterState.HitReact.State", "CharacterState.FallDown", "CharacterState.Space.Air"]));
            if (ally || reacting || valid(montage) && montage !== target.montage || Date.now() < target.recoverUntil) {
                target.state = ally ? "targetControlled" : "targetRecovering"; this.thaw(target); continue;
            }
            target.state = "targetFrozen"; actor.CharacterMovement.StopMovementImmediately();
            if (!valid(target.montage)) {
                const kind = catalog.kinds.find(k => k.id === target.config.kind), motion = kind.motions.find(m => m.id === target.config.motion);
                const animation = this.assets.get(motion.sequence), length = animation.GetPlayLength();
                target.montage = target.anim.PlaySlotAnimationAsDynamicMontage(animation, "DefaultSlot", 0, 0, 1, 1, -1,
                    Math.min(length * target.config.pose / 100, Math.max(0, length - .001)));
                if (!valid(target.montage)) throw new Error("arena_pose_rejected");
                target.anim.Montage_Pause(target.montage); actor.Mesh.GlobalAnimRateScale = 0;
            }
        }
        if (this.since !== null) this.sample.elapsed = Math.max(0, UE.GameplayStatics.GetTimeSeconds(this.player) - this.since);
        return ready;
    }
    edit(config) {
        const target = this.targets.find(t => t.config.id === config.id);
        if (!target || target.ally || target.state !== "targetFrozen") return false;
        const transform = this.placement(config, 150), respawn = ["kind", "x", "y", "yaw"].some(k => target.config[k] !== config[k]);
        target.config = E.copy(config); target.transform = transform;
        if (respawn) this.spawn(target); else this.thaw(target);
        return true;
    }
    equipmentSignature() {
        const bag = E.bag(), items = [];
        for (const main of [true, false]) {
            const weapons = main ? bag.GetEquipmentNormalWeapons() : bag.GetShaqiWeapons();
            items.push(Array.from(weapons, id => [String(id), bag.GetWeaponLevel(id, main)]));
        }
        items.push(Array.from(bag.AssemblyAccessory, id => [String(id), Number(bag.Accessories.Get(id)?.CurrentLevel || 0)]));
        items.push(String(bag.TopMedicineID), String(bag.BottomMedicineID));
        return JSON.stringify(items);
    }
    add(config) {
        if (this.targets.length >= 16) return false;
        const target = { config: E.copy(config), transform: this.placement(config, 150), sample: emptySample() };
        this.targets.push(target); this.spawn(target); return true;
    }
    remove(id) {
        if (this.targets.length <= 1) return false;
        const target = this.targets.find(target => target.config.id === id); if (!target) return false;
        this.removeTargetActor(target); this.targets = this.targets.filter(target => target.config.id !== id); return true;
    }
    resetPlayer() {
        const transform = this.placement({ x: -800, y: 0, yaw: 0 }, this.player.CapsuleComponent.GetScaledCapsuleHalfHeight() + 2);
        const rotation = UE.KismetMathLibrary.Quat_Rotator(transform.Rotation);
        const targeting = this.player.GetComponentByClass(UE.TargetingComponent.StaticClass());
        targeting.StopTryLockTarget(); targeting.StopLockTarget(); this.player.CharacterMovement.StopMovementImmediately();
        if (!this.player.K2_TeleportTo(transform.Translation, rotation)) throw new Error("arena_player_start_blocked");
        this.player.Controller.SetControlRotation(rotation); this.player.Controller.ClientSetRotation(rotation, true);
        this.player.GetComponentByClass(UE.NBCameraComponent.StaticClass()).NotifyTeleported();
    }
    removeBinding(binding) {
        if (!binding) return;
        if (valid(binding.action)) { binding.action.EventReceived.Remove(binding.callback); binding.action.EndAction(); }
        this.bindings = this.bindings.filter(item => item !== binding);
    }
    removeTargetActor(target) {
        this.removeBinding(target.binding); target.binding = null; this.thaw(target);
        if (valid(target.spawner)) target.spawner.ShouldSpawn = false;
        target.unlock?.(); target.unlock = null;
        if (valid(target.actor)) target.actor.K2_DestroyActor();
        if (valid(target.spawner)) target.spawner.K2_DestroyActor();
        target.actor = null;
    }
    stop() {
        cpp.loadModule("pbz-arena-v1/combat/release");
        Assets.cancel(this.loadHandle); this.loadHandle = null;
        for (const target of this.targets) this.removeTargetActor(target);
        for (const prop of this.props) if (valid(prop.actor)) prop.actor.K2_DestroyActor();
        for (const binding of [...this.bindings]) this.removeBinding(binding);
        if (valid(this.player)) { const index = Array.from(this.player.Tags).indexOf("PhantomBladeArena.SkillEditing"); if (index >= 0) this.player.Tags.RemoveAt(index); }
        this.targets = []; this.props = []; this.error = null;
    }
}
module.exports = { Practice, catalog, exclude, emptySample };
