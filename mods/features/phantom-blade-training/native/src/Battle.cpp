#include "Battle.hpp"
#include "Resources.hpp"
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
    Call sample(saves(), L"TrySave"); sample[L"Reason"].text(L"QuiescentAuto"); sample.invoke();
    Call capture(saves(), L"SaveJsonObjectToString"); capture.invoke();
    return Json::parse(utf8(capture[L"ReturnValue"].text()));
}
int32_t extract_weapon_level(const Json& save_json) {
    int32_t max_wpn = 0;
    if (save_json.contains("Inventory") && save_json["Inventory"].is_object()) {
        const auto& inv = save_json["Inventory"];
        if (inv.contains("MainWeaponInfos") && inv["MainWeaponInfos"].is_array()) {
            for (const auto& item : inv["MainWeaponInfos"]) {
                if (item.is_object() && item.contains("WeaponLevel") && item["WeaponLevel"].is_number_integer())
                    max_wpn = std::max(max_wpn, item["WeaponLevel"].get<int32_t>());
            }
        }
    }
    return max_wpn;
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
    if (active() && baseline_max_health_ > 0.f) {
        session.player_max_health = static_cast<int32_t>(std::lround(baseline_max_health_));
        session.player_weapon_level = baseline_weapon_level_;
    } else if (auto* p = player(); p && valid(p)) {
        const float hp = Attributes::read(p, L"Health_Max");
        if (hp > 0.f) session.player_max_health = static_cast<int32_t>(std::lround(hp));
    }
    if (session.pending.config.mode == LoadoutMode::Current) {
        session.pending.config.max_health = session.player_max_health;
        session.pending.config.weapon_level = session.player_weapon_level;
    }
}
void Battle::snapshot() {
    auto* p = player();
    if (!player_ready(p) || Attributes::read(p, L"Health") <= 0) throw std::runtime_error("player_not_ready");
    return_resources_ = Attributes::capture(p);
    baseline_max_health_ = Attributes::read(p, L"Health_Max");
    Call slot(saves(), L"GetContinueSlotName"); slot.invoke(); return_slot_ = slot[L"ReturnValue"].text();
    checkpoint_ = snapshot_json();
    baseline_weapon_level_ = extract_weapon_level(checkpoint_);
    session_.player_max_health = static_cast<int32_t>(std::lround(baseline_max_health_));
    session_.player_weapon_level = baseline_weapon_level_;
    if (entering_.config.mode == LoadoutMode::Current) {
        entering_.config.max_health = session_.player_max_health;
        entering_.config.weapon_level = session_.player_weapon_level;
    }
    auto* system = game_system(p);
    return_spawn_.clear();
    for (auto name : {L"PlayerTransform", L"ReviveTransform", L"ReviveCameraRotation", L"StartGameTransform", L"LastSaveActorID"})
        return_spawn_.emplace(name, field(system, name).text());
}
void Battle::apply_entry_loadout() {
    if (entering_.config.mode == LoadoutMode::Current) {
        if (checkpoint_.contains("Inventory") && round_.contains("Inventory")) {
            if (checkpoint_["Inventory"].contains("MainWeaponInfos"))
                round_["Inventory"]["MainWeaponInfos"] = checkpoint_["Inventory"]["MainWeaponInfos"];
            if (checkpoint_["Inventory"].contains("ShaChiWeaponInfo"))
                round_["Inventory"]["ShaChiWeaponInfo"] = checkpoint_["Inventory"]["ShaChiWeaponInfo"];
        }
        if (checkpoint_.contains("AbilityNode_RenewalHun"))
            round_["AbilityNode_RenewalHun"] = checkpoint_["AbilityNode_RenewalHun"];
        return;
    }
    const int32_t target_wpn = std::clamp(entering_.config.weapon_level, 0, 30);
    if (round_.contains("Inventory") && round_["Inventory"].is_object()) {
        auto& inv = round_["Inventory"];
        if (inv.contains("MainWeaponInfos") && inv["MainWeaponInfos"].is_array()) {
            for (auto& item : inv["MainWeaponInfos"]) {
                if (item.is_object()) {
                    item["WeaponLevel"] = target_wpn;
                    if (target_wpn > 15) item["AlreadySnapSoul"] = 1;
                }
            }
        }
        if (inv.contains("ShaChiWeaponInfo") && inv["ShaChiWeaponInfo"].is_array()) {
            for (auto& item : inv["ShaChiWeaponInfo"]) {
                if (item.is_object()) {
                    item["ShaChiWeaponLevel"] = target_wpn;
                    if (target_wpn > 15) item["AlreadySnapSoul"] = true;
                }
            }
        }
    }
    const int32_t stones = std::clamp((entering_.config.max_health - 500) / 50, 0, 8);
    if (round_.contains("AbilityNode_RenewalHun") && round_["AbilityNode_RenewalHun"].is_object()) {
        auto& ability = round_["AbilityNode_RenewalHun"];
        if (ability.contains("AbilityNodes") && ability["AbilityNodes"].is_object()) {
            auto& nodes = ability["AbilityNodes"];
            for (int i = 1; i <= 8; ++i)
                nodes["HeiShi" + std::to_string(i)] = (i <= stones) ? 1 : 0;
        }
    }
}
void Battle::prepare_round(bool reset) {
    const auto equipment = snapshot_json();
    if (!reset) round_ = checkpoint_;
    // A reset restores the encounter, retaining the loadout selected in practice.
    for (auto key : {"Inventory", "AbilityNode_RenewalHun", "AbilityNode_RenewalHun_Climax", "PBZAppearanceComponent"})
        if (equipment.contains(key)) round_[key] = equipment[key];
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
    isolation_.initialize();
    if (!isolation_.idle(saves())) throw std::runtime_error("save_busy");
    entering_ = reset ? *session_.current : session_.pending;
    returning_ = false; first_entry_ = !active();
    if (first_entry_) snapshot();
    prepare_round(reset);
    attributes_.lock(player(), false);
    session_.phase = reset ? Phase::Resetting : Phase::Entering;
    if (isolation_.active()) session_.practice_session = true;
    old_player_ = player(); boss_.Reset(); spawner_.Reset(); spawning_ = false;
    stage(Step::Drain);
}
void Battle::end() {
    if (!active() || step_ != Step::Idle) return;
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
    if (!p || p == old_player_.Get() || !player_ready(p)) return false;
    Call current(library(L"Engine", L"GameplayStatics"), L"GetCurrentLevelName");
    current[L"WorldContextObject"].set(p); current[L"bRemovePrefixString"].text(L"True"); current.invoke();
    return current[L"ReturnValue"].text() == level_ && Attributes::read(p, L"Health_Max") > 0;
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
    stage(Step::Idle);
    session_.phase = session_.current ? Phase::Ended : Phase::Ready;
    if (!isolation_.active()) { checkpoint_ = nullptr; session_.practice_session = false; }
}
Battle::Update Battle::tick() {
    if (step_ == Step::Idle) {
        if (!session_.continuable()) return Update::None;
        if (auto* p = player(); p && Attributes::read(p, L"Health") <= 0) {
            session_.finish(true); return Update::Ended;
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
        if (first_entry_ && !isolation_.active()) { isolation_.prepare(); stage(Step::Copy); }
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
        if (returning_) {
            if (baseline_max_health_ > 0.f)
                Attributes::write(player(), L"Health_Max", baseline_max_health_);
            Attributes::restore(player(), return_resources_);
            restore_return_spawn();
            stage(Step::Finish);
        } else {
            const float target_max_hp = (entering_.config.mode == LoadoutMode::Current && baseline_max_health_ > 0.f)
                ? baseline_max_health_
                : static_cast<float>(std::clamp(entering_.config.max_health, 100, 2000));
            Attributes::write(player(), L"Health_Max", target_max_hp);
            Attributes::refill(player());
            attributes_.lock(player(), session_.lock_health);
            stage(Step::Spawn);
        }
        break;
    case Step::Spawn: {
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
        exclude_record(spawned); boss_ = spawned;
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
        isolation_.restore_account(saves());
        { Call mark(saves(), L"MarkLoadedSlotAsContinue"); mark[L"SlotName"].text(return_slot_); mark.invoke(); }
        checkpoint_ = nullptr; round_ = nullptr; transfer_ = nullptr; return_resources_.clear(); return_spawn_.clear();
        session_.current.reset(); session_.practice_session = false; session_.lock_health = false;
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
        + " wpn=" + std::to_string(session_.pending.config.weapon_level)
        + " player_ready=" + std::to_string(ready_player_.Get() && ready_player_.Get() == player());
}
}
