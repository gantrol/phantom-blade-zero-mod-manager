#include "Battle.hpp"
#include "Resources.hpp"
#include "Loadout.hpp"
#include <algorithm>
#include <cmath>

namespace arena {
using namespace ue;
using Json = nlohmann::json;
namespace {
UObject* instance() { return UObjectGlobals::FindFirstOf(L"BP_GameInstance_C"); }
UObject* saves() { return UObjectGlobals::FindFirstOf(L"PBZSaveLoadSubsystem"); }
UObject* player() {
    Call c(library(L"Engine", L"GameplayStatics"), L"GetPlayerCharacter");
    c[L"WorldContextObject"].set(instance()); c[L"PlayerIndex"].set<int32_t>(0);
    return c.invoke().result<UObject*>();
}
UObject* game_system(UObject* context) {
    Call c(library(L"Engine", L"SubsystemBlueprintLibrary"), L"GetWorldSubsystem");
    c[L"ContextObject"].set(context);
    c[L"Class"].set(find(L"/Script/ProjectPBZ.PBZGameSystem"));
    return c.invoke().result<UObject*>();
}
Json snapshot_json() {
    Call capture(saves(), L"SaveJsonObjectToString"); capture.invoke();
    return Json::parse(utf8(capture[L"ReturnValue"].text()));
}
std::wstring short_level(std::wstring_view path) { return std::wstring(path.substr(path.find_last_of(L'/') + 1)); }
std::wstring transform(const Json& value) {
    const auto& p = value.at("Location");
    const auto yaw = value.at("Rotation").at("Yaw").get<double>() * 3.14159265358979323846 / 360.0;
    return L"(Rotation=(X=0,Y=0,Z=" + std::to_wstring(std::sin(yaw)) + L",W=" + std::to_wstring(std::cos(yaw))
        + L"),Translation=(X=" + std::to_wstring(p.at("X").get<double>()) + L",Y=" + std::to_wstring(p.at("Y").get<double>())
        + L",Z=" + std::to_wstring(p.at("Z").get<double>()) + L"),Scale3D=(X=1,Y=1,Z=1))";
}
void exclude_record(UObject* actor) {
    Call c(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"AddLooseGameplayTags");
    c[L"Actor"].set(actor);
    c[L"GameplayTags"].text(L"(GameplayTags=((TagName=\"CharacterType.AI.ExcludeFromBossRecord\")))");
    c.invoke();
}
}
void Battle::stage(Step value) { step_ = value; since_ = std::chrono::steady_clock::now(); }
bool Battle::player_ready(UObject* p) {
    if (!p || !valid(p) || field(instance(), L"IsLoadingLevel").get<bool>()) return false;
    if (ready_player_.Get() == p) return true;
    auto* nodes = object(p, L"PBZAbilityNodes");
    if (!nodes || field(nodes, L"LoadObjectJsonKey").text() != L"AbilityNode_RenewalHun"
        || field(nodes, L"NeedLoadItems").text() != L"()"
        || field(nodes, L"AbilityNodeMap").text() == L"()"
        || Attributes::read(p, L"Health_Max") <= 0) return false;
    ready_player_ = p;
    RC::Output::send(L"[PhantomBladeArena] Player ready; maximum health="
        + std::to_wstring(Attributes::read(p, L"Health_Max")) + L"\n");
    return true;
}
void Battle::sync_player_baseline(Session& session) {
    const bool first = !session.baseline_ready;
    if (active()) {
        session.player_max_health = static_cast<int32_t>(std::lround(baseline_max_health_));
        session.player_loadout = baseline_loadout_;
        session.owned_weapons = baseline_owned_;
        session.player_difficulty = checkpoint_.at("PBZGameSystem").at("DifficultModeByte").get<int32_t>();
        session.player_cycle = checkpoint_.at("PBZGameSystem").value("CycleCount", 0);
        if (session.current && !session.busy()) {
            const auto actual = loadout::capture();
            session.current->config.weapons = actual.weapons;
            session.current->config.active_main = actual.active_main;
            session.current->config.active_phantom = actual.active_phantom;
        }
    } else {
        session.baseline_ready = false;
        auto* p = player();
        if (!player_ready(p)) return;
        const auto current = loadout::capture();
        auto available = loadout::owned();
        session.player_loadout = current;
        session.owned_weapons = std::move(available);
        auto* system = game_system(p);
        session.player_difficulty = field(system, L"DifficultModeByte").get<uint8_t>();
        session.player_cycle = field(system, L"CycleCount").get<int32_t>();
        session.player_max_health = static_cast<int32_t>(std::lround(Attributes::read(p, L"Health_Max")));
    }
    session.baseline_ready = true;
    if (first && session.pending.config.mode == LoadoutMode::Custom)
        session.pending.config = session.player_loadout;
}
void Battle::snapshot() {
    auto* p = player();
    if (!player_ready(p) || Attributes::read(p, L"Health") <= 0) throw std::runtime_error("player_not_ready");
    return_resources_ = Attributes::capture(p);
    baseline_max_health_ = Attributes::read(p, L"Health_Max");
    Call slot(saves(), L"GetContinueSlotName"); slot.invoke(); return_slot_ = slot[L"ReturnValue"].text();
    checkpoint_ = snapshot_json();
    baseline_loadout_ = loadout::capture();
    baseline_owned_ = loadout::owned();
    session_.player_max_health = static_cast<int32_t>(std::lround(baseline_max_health_));
    session_.player_loadout = baseline_loadout_;
    session_.owned_weapons = baseline_owned_;
    session_.baseline_ready = true;
    session_.player_difficulty = checkpoint_.at("PBZGameSystem").at("DifficultModeByte").get<int32_t>();
    session_.player_cycle = checkpoint_.at("PBZGameSystem").value("CycleCount", 0);
    auto* system = game_system(p);
    return_spawn_.clear();
    for (auto name : {L"PlayerTransform", L"ReviveTransform", L"ReviveCameraRotation", L"StartGameTransform", L"LastSaveActorID"})
        return_spawn_.emplace(name, field(system, name).text());
}
void Battle::apply_entry_loadout() {
    loadout::apply(round_, checkpoint_, entering_.config);
}
void Battle::prepare_round(bool reset) {
    if (!reset) round_ = checkpoint_;
    else {
        const auto equipment = snapshot_json();
        // Preserve explicit in-practice equipment changes on reset; pending edits stay separate.
        const auto actual = loadout::capture();
        entering_.config.weapons = actual.weapons;
        entering_.config.active_main = actual.active_main;
        entering_.config.active_phantom = actual.active_phantom;
        for (auto key : {"Inventory", "AbilityNode_RenewalHun", "AbilityNode_RenewalHun_Climax", "PBZAppearanceComponent"})
            if (equipment.contains(key)) round_[key] = equipment[key];
    }
    apply_entry_loadout();
    const auto& scene = resources::scenes.at(entering_.scene);
    level_ = short_level(scene.level);
    auto& system = round_["PBZGameSystem"];
    system["CurrentLevelName"] = utf8(level_);
    system["LastSaveActorID"] = "None";
    system["PlayerTransform"] = {
        {"Location", {{"X",scene.position[0] + 800.0},{"Y",scene.position[1]},{"Z",scene.position[2] + 150.0}}},
        {"Rotation", {{"Roll",0},{"Yaw",180},{"Pitch",0}}}
    };
    system["ReviveTransform"] = system["PlayerTransform"];
    // Every spawn path belongs to the destination world. Keeping the previous
    // map's start point here sends the native fallback outside the arena.
    system["StartGameTransform"] = system["PlayerTransform"];
    round_["BellSaveID"] = "";
    spawn_transform_ = transform(system["PlayerTransform"]);
    transfer_ = round_;
}
void Battle::start(bool reset) {
    if (step_ != Step::Idle) return;
    if (reset && !session_.current) throw std::runtime_error("no_current_battle");
    if (!reset && !session_.startable()) throw std::runtime_error("loadout_mode_unavailable");
    isolation_.initialize();
    entering_ = reset ? *session_.current : session_.pending;
    if (entering_.practice != PracticeMode::Boss) Practice::initialize();
    practice_.stop();
    returning_ = false; first_entry_ = !active(); resetting_ = reset;
    attributes_.lock(player(), false);
    session_.phase = reset ? Phase::Resetting : Phase::Entering;
    if (isolation_.active()) session_.practice_session = true;
    old_player_ = player(); boss_.Reset(); spawner_.Reset(); spawning_ = false;
    stage(Step::Drain);
}
void Battle::end() {
    if (!active() || step_ != Step::Idle) return;
    practice_.stop();
    returning_ = true; first_entry_ = false;
    attributes_.lock(player(), false);
    transfer_ = checkpoint_;
    auto& system = transfer_["PBZGameSystem"];
    level_ = wide(system.at("CurrentLevelName").get<std::string>());
    spawn_transform_ = transform(system.at("PlayerTransform"));
    system["StartGameTransform"] = system["PlayerTransform"];
    session_.phase = Phase::Returning;
    old_player_ = player(); boss_.Reset(); spawner_.Reset();
    stage(Step::Drain);
}
void Battle::write() {
    Call replace(saves(), L"ReplaceSaveString");
    replace[L"SaveGameParam"].text(L"(SaveSlotName=\"ArenaCheckpoint\",bSaveAdvanced=False,bShowSaveTip=False,Reason=Debug)");
    replace[L"String"].text(wide(transfer_.dump())); replace.invoke();
    if (replace[L"ReturnValue"].get<uint8_t>() != 0) throw std::runtime_error("checkpoint_save_failed");
    stage(Step::Save);
}
void Battle::load() {
    Call loading(instance(), L"SetLoadingScreen"); loading[L"MapName"].text(L"PBZ_WP"); loading.invoke();
    Call restore(instance(), L"LoadGame"); restore[L"SlotName"].text(L"ArenaCheckpoint"); restore.invoke();
    auto result = restore[L"LoadGameResultParam"];
    auto* result_type = static_cast<UStruct*>(find(L"/Script/ProjectPBZ.LoadGameResultParam"));
    if (field(result_type, result.data, L"ResultType").get<uint8_t>() != 0) throw std::runtime_error("checkpoint_load_failed");
    Call spawn(saves(), L"OverrideLoadedPlayerSpawnTransform"); spawn[L"SpawnTransform"].text(spawn_transform_); spawn.invoke();
    Call open(library(L"Engine", L"GameplayStatics"), L"OpenLevel");
    open[L"WorldContextObject"].set(instance()); open[L"LevelName"].text(level_); open[L"bAbsolute"].text(L"True"); open.invoke();
    stage(Step::World);
}
void Battle::restore_return_spawn() {
    auto* system = game_system(player());
    for (const auto& [name, value] : return_spawn_)
        field(system, name.c_str()).text(value);
    Call restore(saves(), L"SaveObjectData");
    restore[L"ObjectId"].text(L"PBZGameSystem");
    restore[L"JsonObjectWrapper"].text(wide(checkpoint_.at("PBZGameSystem").dump()));
    restore.invoke();
}
bool Battle::world_ready() {
    auto* p = player();
    if (!p || p == old_player_.Get() || !valid(p)) return false;
    if (held_player_.Get() != p) {
        auto* movement = object(p, L"CharacterMovement");
        return_movement_mode_ = field(movement, L"MovementMode").get<uint8_t>();
        Call hold(movement, L"SetMovementMode"); hold[L"NewMovementMode"].text(L"MOVE_None"); hold.invoke();
        held_player_ = p;
    }
    if (!player_ready(p)) return false;
    if (returning_ && std::abs(Attributes::read(p, L"Health_Max") - baseline_max_health_) > 0.1f) return false;
    const auto& expected_system = transfer_.at("PBZGameSystem");
    auto* actual_system = game_system(p);
    if (field(actual_system, L"DifficultModeByte").get<uint8_t>() != expected_system.at("DifficultModeByte").get<int>()
        || field(actual_system, L"CycleCount").get<int32_t>() != expected_system.value("CycleCount", 0)) return false;
    Call current(library(L"Engine", L"GameplayStatics"), L"GetCurrentLevelName");
    current[L"WorldContextObject"].set(p); current[L"bRemovePrefixString"].text(L"True"); current.invoke();
    if (current[L"ReturnValue"].text() != level_) return false;
    Call partition(library(L"Engine", L"SubsystemBlueprintLibrary"), L"GetWorldSubsystem");
    partition[L"ContextObject"].set(p); partition[L"Class"].set(find(L"/Script/Engine.WorldPartitionSubsystem"));
    auto* streaming = partition.invoke().result<UObject*>();
    if (streaming && !Call(streaming, L"IsAllStreamingCompleted").invoke().result<bool>()) return false;
    struct Vector { double x, y, z; };
    const auto location = Call(p, L"K2_GetActorLocation").invoke().result<Vector>();
    Call ground(library(L"Engine", L"KismetSystemLibrary"), L"LineTraceSingle");
    ground[L"WorldContextObject"].set(p);
    ground[L"Start"].set(Vector{location.x, location.y, location.z});
    ground[L"End"].set(Vector{location.x, location.y, location.z - 400.0});
    ground[L"TraceChannel"].text(L"TraceTypeQuery1");
    ground[L"bIgnoreSelf"].set(true);
    if (!ground.invoke().result<bool>()) return false;
    auto* hit_type = static_cast<UStruct*>(find(L"/Script/Engine.HitResult"));
    return field(hit_type, ground[L"OutHit"].data, L"ImpactNormal").get<Vector>().z >= 0.5;
}
void Battle::release_player() {
    if (auto* p = held_player_.Get(); p && valid(p)) {
        Call release(object(p, L"CharacterMovement"), L"SetMovementMode");
        release[L"NewMovementMode"].set<uint8_t>(return_movement_mode_ ? return_movement_mode_ : 1);
        release.invoke();
    }
    held_player_.Reset();
}
UObject* Battle::find_spawner() {
    std::vector<UObject*> all; UObjectGlobals::FindAllOf(L"BP_AISpawner_C", all);
    const auto& scene = resources::scenes.at(entering_.scene);
    for (auto* candidate : all) {
        if (field(candidate, L"SpawnerID").text() == scene.spawner || candidate->GetName() == scene.spawner) return candidate;
    }
    return nullptr;
}
void Battle::set_lock(bool value) {
    if (session_.current && !session_.busy()) attributes_.lock(player(), value);
    session_.lock_health = value;
}
void Battle::fail() {
    practice_.stop();
    isolation_.cancel_restore();
    release_player();
    stage(Step::Idle);
    session_.phase = session_.current ? Phase::Ended : Phase::Ready;
    if (!isolation_.active()) { checkpoint_ = nullptr; session_.practice_session = false; }
}
Battle::Update Battle::tick() {
    if (step_ == Step::Idle) {
        if (!session_.continuable()) return Update::None;
        if (auto* p = player(); p && Attributes::read(p, L"Health") <= 0) {
            practice_.stop();
            session_.finish(true); return Update::Ended;
        }
        if (session_.current->practice != PracticeMode::Boss) {
            practice_.tick(); return Update::None;
        }
        if (!boss_.Get() || Attributes::read(boss_.Get(), L"Health") <= 0) {
            session_.finish(false); return Update::Ended;
        }
        return Update::None;
    }
    if (std::chrono::steady_clock::now() - since_ > std::chrono::seconds(90))
        throw std::runtime_error("arena_transition_timeout");
    switch (step_) {
    case Step::Drain:
        if (!isolation_.idle(saves())) break;
        if (returning_) { stage(Step::Write); break; }
        // Collect the live loadout once, then wait for any resulting native save.
        // Previously snapshot and prepare_round each requested a save in one tick.
        { Call sample(saves(), L"TrySave");
          sample[L"Reason"].text(L"QuiescentAuto");
          sample[L"SaveGameParam"].text(L"(bSaveAdvanced=True,bShowSaveTip=False)");
          sample.invoke();
          if (sample[L"ReturnValue"].get<uint8_t>() > 1) throw std::runtime_error("snapshot_save_rejected"); }
        stage(Step::Capture);
        break;
    case Step::Capture:
        if (!isolation_.idle(saves())) break;
        if (first_entry_) snapshot();
        prepare_round(resetting_);
        if (first_entry_ && !isolation_.active()) { isolation_.prepare(saves()); stage(Step::Copy); }
        else stage(Step::Write);
        break;
    case Step::Copy:
        if (isolation_.prepared() && isolation_.idle(saves())) {
            isolation_.activate(); session_.practice_session = true; stage(Step::Write);
        }
        break;
    case Step::Write:
        if (isolation_.idle(saves())) write();
        break;
    case Step::Save:
        if (isolation_.idle(saves())) stage(Step::Load);
        break;
    case Step::Load:
        if (isolation_.idle(saves())) load();
        break;
    case Step::World:
        if (!world_ready()) break;
        release_player();
        if (returning_) {
            Attributes::restore(player(), return_resources_);
            restore_return_spawn();
            stage(Step::Finish);
        } else {
            loadout::verify(entering_.config);
            if (entering_.config.max_health > 0)
                Attributes::limit_health(player(), static_cast<float>(entering_.config.max_health));
            Attributes::refill(player());
            attributes_.lock(player(), session_.lock_health);
            stage(Step::Spawn);
        }
        break;
    case Step::Spawn: {
        if (entering_.practice != PracticeMode::Boss) {
            if (!spawning_) {
                auto* anchor = find_spawner(); if (!anchor) break;
                practice_.begin(player(), anchor, entering_); spawning_ = true;
            }
            if (!practice_.tick()) break;
            session_.accept_started(entering_); stage(Step::Idle);
            return Update::Started;
        }
        if (!spawner_.Get()) {
            auto* spawner = find_spawner(); if (!spawner) break;
            spawner_ = spawner;
            // Keep the native domain, behavior configuration and spawn callbacks.
            Call set(spawner, L"SetClassToSpawn");
            set[L"InClassToSpawn"].text(resources::bosses.at(entering_.boss).actor_class); set.invoke();
            invoke(spawner, L"Respawn"); spawning_ = true;
        }
        auto* spawned = object(spawner_.Get(), L"SpawnedAI");
        if (!spawned || field(spawner_.Get(), L"IsSpawning").get<bool>()) break;
        if (Attributes::read(spawned, L"Health") <= 0) break;
        exclude_record(spawned);
        Attributes::tune_boss(spawned, entering_.config.boss_health_percent / 100.f, entering_.config.boss_attack_percent / 100.f);
        boss_ = spawned;
        session_.accept_started(entering_); stage(Step::Idle);
        RC::Output::send(L"[PhantomBladeArena] Battle ready: " + spawned->GetFullName() + L"\n");
        return Update::Started;
    }
    case Step::Finish:
        if (!isolation_.idle(saves())) break;
        isolation_.verify_original();
        stage(Step::Verify); break;
    case Step::Verify:
        if (!isolation_.prepared() || !isolation_.idle(saves())) break;
        isolation_.begin_restore_account(saves());
        stage(Step::Restore);
        break;
    case Step::Restore:
        if (!isolation_.idle(saves())) break;
        isolation_.finish_restore_account(saves(), return_slot_);
        checkpoint_ = nullptr; round_ = nullptr; transfer_ = nullptr; return_resources_.clear(); return_spawn_.clear();
        session_.current.reset(); session_.practice_session = false; session_.lock_health = false;
        session_.scene_preview.reset();
        { const auto& initial = resources::bosses[resources::initial_boss];
          session_.reset_default(initial.default_scene, resources::initial_boss); }
        session_.phase = Phase::Ready; stage(Step::Idle);
        return Update::Returned;
    default: break;
    }
    return Update::None;
}
std::string Battle::status() const {
    return "step=" + std::to_string(static_cast<int>(step_)) + " phase=" + std::to_string(static_cast<int>(session_.phase))
        + " current=" + (session_.current ? std::to_string(session_.current->boss) : "none")
        + " pending=" + std::to_string(session_.pending.boss)
        + " mode=" + std::to_string(static_cast<int>(session_.pending.config.mode))
        + " hp=" + std::to_string(session_.pending.config.max_health)
        + " main1=" + session_.pending.config.weapons[0].item_id + ":" + std::to_string(session_.pending.config.weapons[0].level)
        + " main2=" + session_.pending.config.weapons[1].item_id + ":" + std::to_string(session_.pending.config.weapons[1].level)
        + " phantom1=" + session_.pending.config.weapons[2].item_id + ":" + std::to_string(session_.pending.config.weapons[2].level)
        + " phantom2=" + session_.pending.config.weapons[3].item_id + ":" + std::to_string(session_.pending.config.weapons[3].level)
        + " player_ready=" + std::to_string(ready_player_.Get() && ready_player_.Get() == player());
}
}
