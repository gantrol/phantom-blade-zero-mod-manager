"use strict";
const E = require("./Engine"), A = require("./Attributes"), Loadout = require("./Loadout");
const Isolation = require("./Isolation"), { Practice, exclude } = require("./Practice");
const catalog = require("./Catalog.generated"), Assets = require("PhantomBladeRuntime/Assets");
const { UE, cpp, valid } = E;
const spawnFields = ["PlayerTransform", "ReviveTransform", "ReviveCameraRotation", "StartGameTransform", "LastSaveActorID"];
const snapshot = () => JSON.parse(E.saves().SaveJsonObjectToString());
function copySpawn(name, value) {
    if (name === "LastSaveActorID") return String(value);
    if (name === "ReviveCameraRotation") return { Pitch: value.Pitch, Yaw: value.Yaw, Roll: value.Roll };
    const position = value.Translation, rotation = value.Rotation, scale = value.Scale3D;
    return { position: [position.X, position.Y, position.Z], rotation: [rotation.X, rotation.Y, rotation.Z, rotation.W], scale: [scale.X, scale.Y, scale.Z] };
}
function restoreSpawn(name, value) {
    if (name === "LastSaveActorID") return value;
    if (name === "ReviveCameraRotation") return new UE.Rotator(value.Pitch, value.Yaw, value.Roll);
    const transform = new UE.Transform(); transform.Translation = new UE.Vector(...value.position);
    transform.Rotation = new UE.Quat(...value.rotation); transform.Scale3D = new UE.Vector(...value.scale); return transform;
}
function defaults(config) {
    const boss = catalog.bosses.find(b => b.id === "BP_Monster_MobMan_Elite_Sword") || catalog.bosses[0];
    return { boss: boss.id, scene: boss.defaultSceneId, config, practice: "boss", fixedStart: false, props: [],
        targets: [-250, 0, 250].map((y, i) => ({ id: i + 1, kind: "sword", motion: "idle", pose: 25, x: 0, y, yaw: 0 })) };
}
class Battle {
    constructor() {
        this.isolation = new Isolation(); this.practice = new Practice(); this.phase = "ready";
        this.pending = defaults(Loadout.profile(Loadout.template(), "template")); this.drafts = new Map();
    }
    get busy() { return !!this.step; }
    get active() { return !!this.checkpoint; }
    open(fromTitle) {
        if (this.busy) return;
        if (!this.active) {
            this.fromTitle = fromTitle;
            if (!fromTitle && this.playerReady(E.player())) {
                this.playerBase = snapshot(); this.playerConfig = Loadout.profile(this.playerBase, "player");
                Loadout.liveWeapons(this.playerConfig); this.playerPool = Loadout.owned(this.playerBase, true);
                if (!this.opened || this.pending.config.source === "player") this.pending.config = E.copy(this.playerConfig);
            } else { this.playerBase = null; this.playerPool = null; }
        }
        this.opened = true;
    }
    pool(config = this.pending.config) { return config.source === "template" ? Loadout.owned(Loadout.template()) : this.playerPool || []; }
    useSource(source) {
        if (this.busy || source === "player" && !this.playerBase) return;
        this.drafts.set(this.pending.boss + "/" + this.pending.config.source, E.copy(this.pending.config));
        this.pending.config = E.copy(this.drafts.get(this.pending.boss + "/" + source)
            || (source === "template" ? Loadout.profile(Loadout.template(), source) : this.playerConfig));
    }
    selectBoss(id) {
        const boss = catalog.bosses.find(b => b.id === id); if (!boss || this.busy) return;
        const source = this.pending.config.source;
        this.drafts.set(this.pending.boss + "/" + source, E.copy(this.pending.config));
        this.pending.boss = id; this.pending.scene = boss.defaultSceneId;
        this.pending.config = E.copy(this.drafts.get(id + "/" + source)
            || (source === "template" ? Loadout.profile(Loadout.template(), source) : this.playerConfig));
    }
    stage(step) { this.step = step; this.deadline = Date.now() + 90000; }
    playerReady(player) {
        return valid(player) && !E.instance().IsLoadingLevel && player.PBZAbilityNodes
            && String(player.PBZAbilityNodes.LoadObjectJsonKey) === "AbilityNode_RenewalHun"
            && player.PBZAbilityNodes.NeedLoadItems.Num() === 0 && player.PBZAbilityNodes.AbilityNodeMap.Num() > 0 && A.read(player, "Health_Max") > 0;
    }
    start(reset = false) {
        if (this.busy || reset && !this.current) return;
        if (!this.isolation.status().idle) { this.message = "saveBusy"; return; }
        this.entering = E.copy(reset ? this.current : this.pending);
        if (!this.entering.config.weapons[0].id) throw new Error("arena_empty_primary_slot");
        this.returning = false; this.resetting = reset; this.first = !this.active; this.failure = null;
        this.oldPlayer = E.player(); this.setLock(false); this.practice.stop(); this.spawner = null; this.boss = null; this.spawning = false;
        this.phase = reset ? "resetting" : "entering"; this.stage("drain");
    }
    end() {
        if (this.busy || !this.active) return;
        this.returning = true; this.failure = null; this.setLock(false); this.practice.stop();
        this.oldPlayer = E.player(); this.phase = "returning";
        if (!this.fromTitle) {
            this.transfer = E.copy(this.checkpoint); this.level = this.transfer.PBZGameSystem.CurrentLevelName;
            this.spawn = E.transform(this.transfer.PBZGameSystem.PlayerTransform);
            this.transfer.PBZGameSystem.StartGameTransform = E.copy(this.transfer.PBZGameSystem.PlayerTransform);
        }
        this.stage("drain");
    }
    prepare() {
        const config = this.entering.config, base = config.source === "template" ? Loadout.template() : this.checkpoint;
        if (!this.resetting) this.round = E.copy(base);
        else {
            const actual = snapshot(); Loadout.liveWeapons(config);
            for (const key of ["Inventory", "AbilityNode_RenewalHun", "AbilityNode_RenewalHun_Climax", "PBZAppearanceComponent"])
                if (actual[key]) this.round[key] = E.copy(actual[key]);
        }
        Loadout.apply(this.round, base, config, this.pool(config));
        const scene = catalog.scenes.find(s => s.id === this.entering.scene);
        if (!scene) throw new Error("arena_scene_missing");
        this.level = scene.level.slice(scene.level.lastIndexOf("/") + 1);
        const system = this.round.PBZGameSystem;
        system.CurrentLevelName = this.level; system.LastSaveActorID = "None";
        system.PlayerTransform = { Location: { X: scene.position[0] + 800, Y: scene.position[1], Z: scene.position[2] + 150 }, Rotation: { Roll: 0, Yaw: 180, Pitch: 0 } };
        system.ReviveTransform = E.copy(system.PlayerTransform); system.StartGameTransform = E.copy(system.PlayerTransform);
        this.round.BellSaveID = ""; this.spawn = E.transform(system.PlayerTransform); this.transfer = E.copy(this.round);
    }
    capture() {
        const save = E.saves();
        if (this.first) {
            this.continueSlot = save.HasAnyLoadableSave() ? String(save.GetContinueSlotName()) : "";
            if (this.fromTitle) this.checkpoint = E.copy(Loadout.template());
            else {
                if (!this.playerReady(E.player()) || A.read(E.player(), "Health") <= 0) throw new Error("arena_player_not_ready");
                this.checkpoint = snapshot(); this.playerBase = E.copy(this.checkpoint); this.resources = A.capture(E.player());
                this.maxHealth = A.read(E.player(), "Health_Max"); this.playerPool = Loadout.owned(this.checkpoint, true);
                // Property wrappers borrow their owning world. Keep plain values
                // across travel, then construct fresh native structs on return.
                this.returnSpawn = Object.fromEntries(spawnFields.map(name => [name, copySpawn(name, E.system()[name])]));
            }
        }
        this.prepare();
        if (this.first) { this.isolation.prepare(); this.stage("copy"); }
        else this.stage("assets");
    }
    write() {
        const param = new UE.SaveGameParam(); param.SaveSlotName = "ArenaCheckpoint";
        param.bSaveAdvanced = false; param.bShowSaveTip = false; param.Reason = UE.EPBZSaveReason.Debug;
        if (E.saves().ReplaceSaveString(param, JSON.stringify(this.transfer)) !== 0) throw new Error("arena_checkpoint_save_failed");
        this.stage("save");
    }
    load() {
        E.instance().SetLoadingScreen("PBZ_WP");
        const out = cpp.$ref(new UE.LoadGameResultParam()); E.instance().LoadGame("ArenaCheckpoint", out);
        if (cpp.$unref(out).ResultType !== 0) throw new Error("arena_checkpoint_load_failed");
        E.saves().OverrideLoadedPlayerSpawnTransform(this.spawn);
        UE.GameplayStatics.SetGamePaused(E.context(), false);
        UE.GameplayStatics.OpenLevel(E.context(), this.level, true, ""); this.stage("world");
    }
    worldReady() {
        const player = E.player();
        if (!valid(player) || player === this.oldPlayer) return false;
        if (this.held !== player) {
            this.held = player; this.movementMode = player.CharacterMovement.MovementMode;
            if (!this.returning && !player.ActorHasTag("PhantomBladeArena.Session")) player.Tags.Add("PhantomBladeArena.Session");
            player.CharacterMovement.SetMovementMode(UE.EMovementMode.MOVE_None, 0);
        }
        if (!this.playerReady(player) || UE.GameplayStatics.GetCurrentLevelName(player, true) !== this.level) return false;
        if (this.returning && Math.abs(A.read(player, "Health_Max") - this.maxHealth) > .1) return false;
        const expected = this.transfer.PBZGameSystem, system = E.system();
        if (system.DifficultModeByte !== expected.DifficultModeByte || system.CycleCount !== (expected.CycleCount || 0)) return false;
        const streaming = E.Helper.getWorldSubsystem(player, UE.WorldPartitionSubsystem.StaticClass());
        if (valid(streaming) && !streaming.IsAllStreamingCompleted()) return false;
        const p = player.K2_GetActorLocation(), hit = E.trace(p, new UE.Vector(p.X, p.Y, p.Z - 400), [player]);
        return !!hit && hit.ImpactNormal.Z >= .5;
    }
    releasePlayer() {
        if (valid(this.held)) this.held.CharacterMovement.SetMovementMode(this.movementMode || UE.EMovementMode.MOVE_Walking, 0);
        this.held = null;
    }
    anchor() {
        const scene = catalog.scenes.find(s => s.id === this.entering.scene);
        const out = cpp.$ref(UE.NewArray(UE.Actor));
        UE.GameplayStatics.GetAllActorsWithInterface(E.context(), UE.SGameAISpawnerInterface.StaticClass(), out);
        return Array.from(cpp.$unref(out)).find(actor => String(UE.KismetSystemLibrary.GetObjectName(actor)) === scene.actor
            || String(actor.SpawnerID) === scene.spawner);
    }
    setLock(value) { this.unlock?.(); this.unlock = null; this.lockHealth = false; if (value && this.active && valid(E.player())) { this.unlock = A.lock(E.player()); this.lockHealth = true; } }
    finishReturn() {
        this.isolation.release(this.continueSlot);
        this.current = null; this.checkpoint = null; this.round = null; this.transfer = null;
        this.resources = null; this.returnSpawn = null; this.roundPlayer = null; this.oldPlayer = null;
        this.step = null; this.phase = "ready"; this.pending = defaults(Loadout.profile(Loadout.template(), "template")); this.opened = false;
        console.log("[PhantomBladeArena] original-save-and-account-restored");
    }
    fail(error) {
        this.failure = error; this.message = "actionFailed"; E.report(error);
        try { this.isolation.cancel(); } catch (cancelError) { E.report(cancelError); }
        try { if (!this.isolation.status().isolated) { this.checkpoint = null; this.current = null; } } catch (statusError) { E.report(statusError); }
        try { this.practice.stop(); this.releasePlayer(); } catch (cleanupError) { E.report(cleanupError); }
        this.step = null; this.phase = "ended";
    }
    tick() {
        if (!this.step) {
            if (!this.current || this.failure) return;
            if (valid(E.player()) && E.player() !== this.roundPlayer) throw new Error("arena_player_replaced");
            if (A.read(E.player(), "Health") <= 0) { this.phase = "ended"; this.message = "playerDefeated"; return; }
            if (this.current.practice !== "boss") this.practice.tick();
            else if (!valid(this.boss) || A.read(this.boss, "Health") <= 0) { this.phase = "ended"; this.message = "bossDefeated"; }
            return;
        }
        if (Date.now() > this.deadline) throw new Error("arena_transition_timeout:" + this.step);
        if (!valid(E.context()) || !valid(E.instance()) || !valid(E.saves())) return;
        const state = this.isolation.status();
        switch (this.step) {
        case "drain":
            if (!state.idle) break;
            if (this.returning) { this.stage(this.fromTitle ? "finish" : "write"); break; }
            if (!(this.first && this.fromTitle)) {
                const param = new UE.SaveGameParam(); param.bSaveAdvanced = true; param.bShowSaveTip = false;
                if (E.saves().TrySave(UE.EPBZSaveReason.QuiescentAuto, param, undefined) > 1) throw new Error("arena_snapshot_save_rejected");
            }
            this.stage("capture"); break;
        case "capture": if (state.idle) this.capture(); break;
        case "copy":
            if (state.idle && state.ready) { this.isolation.activate(); this.stage("baseline"); } break;
        case "baseline": if (state.idle) { this.isolation.baseline(); this.stage("baselineReady"); } break;
        case "baselineReady": if (state.idle && state.ready) this.stage("assets"); break;
        case "assets":
            this.assetsReady = false; this.stage("assetsReady");
            if (this.entering.practice !== "boss") this.practice.load(this.entering, () => { this.assetsReady = true; });
            else {
                const boss = catalog.bosses.find(b => b.id === this.entering.boss);
                this.loadHandle = Assets.load(E.context(), [boss.classPath], loaded => { this.bossClass = loaded[0]; this.assetsReady = valid(this.bossClass); });
            }
            break;
        case "assetsReady": if (this.practice.error) throw this.practice.error; if (this.assetsReady) this.stage("write"); break;
        case "write": if (state.idle) this.write(); break;
        case "save": if (state.idle) this.stage("load"); break;
        case "load": if (state.idle) this.load(); break;
        case "world":
            if (!this.worldReady()) break;
            this.releasePlayer();
            if (this.returning) {
                A.restore(E.player(), this.resources);
                for (const [name, value] of Object.entries(this.returnSpawn)) E.system()[name] = restoreSpawn(name, value);
                E.saves().SaveObjectData("PBZGameSystem", E.wrapper(this.checkpoint.PBZGameSystem)); this.stage("finish");
            } else {
                Loadout.verify(this.entering.config);
                if (this.entering.config.maxHealth > 0) A.limit(E.player(), this.entering.config.maxHealth);
                A.refill(E.player()); this.stage("spawn");
            }
            break;
        case "spawn": {
            if (!this.spawner) { this.spawner = this.anchor(); if (!this.spawner) break; }
            if (this.entering.practice !== "boss") {
                if (!this.spawning) { this.practice.begin(this.spawner, this.entering); this.spawning = true; }
                if (!this.practice.tick()) break;
            } else {
                if (!this.spawning) { this.spawner.SetClassToSpawn(UE.KismetSystemLibrary.Conv_ClassToSoftClassReference(this.bossClass)); this.spawner.Respawn(); this.spawning = true; }
                const boss = this.spawner.SpawnedAI;
                if (!valid(boss) || this.spawner.IsSpawning || A.read(boss, "Health") <= 0) break;
                exclude(boss); A.tune(boss, this.entering.config); this.boss = boss;
            }
            this.roundPlayer = E.player(); this.current = E.copy(this.entering); this.phase = "fighting"; this.step = null; this.message = "";
            console.log("[PhantomBladeArena] ready=" + this.current.practice + " scene=" + this.current.scene); break;
        }
        case "finish": if (state.idle) { this.isolation.verify(); this.stage("verify"); } break;
        case "verify": if (state.idle && state.ready) { this.isolation.restore(); this.stage("restore"); } break;
        case "restore":
            if (!state.idle) break;
            if (this.fromTitle) {
                this.isolation.verifyCache(this.continueSlot);
                E.instance().ChangeGameType(UE.EStartGameType.StartMenu);
                const map = cpp.$ref(""); UE.PBZBlueprintFunctionLibrary.GetGameDataPolicy().StartMenuPolicy.GetCorrectStartMenuMapName(map);
                this.titleLevel = String(cpp.$unref(map)).split("/").pop();
                UE.GameplayStatics.OpenLevel(E.context(), cpp.$unref(map), true, "");
                // Keep writes blocked while the practice world's EndPlay and
                // travel callbacks run. Only the title world can release them.
                this.stage("title");
            } else this.finishReturn();
            break;
        case "title":
            if (state.idle && !E.instance().IsLoadingLevel
                && UE.GameplayStatics.GetCurrentLevelName(E.context(), true) === this.titleLevel) this.finishReturn();
            break;
        }
    }
}
module.exports = { Battle, catalog, defaults };
