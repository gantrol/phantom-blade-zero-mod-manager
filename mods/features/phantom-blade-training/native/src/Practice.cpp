#include "Practice.hpp"
#include "Attributes.hpp"
#include "Loadout.hpp"
#include "Resources.hpp"
#include "PracticeCatalog.hpp"
#include <MinHook.h>
#include <Windows.h>
#include <atomic>
#include <cmath>
#include <format>
#include <set>

namespace arena {
using namespace ue;
namespace {
struct Vector { double x, y, z; };
void validate(const Selection& selection) {
    if (selection.targets.empty() || selection.targets.size() > max_practice_targets)
        throw std::runtime_error("invalid_practice_count");
    std::set<uint32_t> ids;
    for (const auto& target : selection.targets) {
        if (!target.id || !ids.insert(target.id).second || target.pose_percent < 0 || target.pose_percent > 100
            || std::abs(target.x) > 1500 || std::abs(target.y) > 1500 || target.yaw < 0 || target.yaw >= 360)
            throw std::runtime_error("invalid_practice_target");
        practice_catalog::motion(target);
    }
}
constexpr auto combat_tag = L"(TagName=\"CharacterState.InCombat\")";
std::atomic<void*> gated_counts{};
uint64_t combat_name{};
using UpdateTags = bool (*)(void*, const uint64_t*, int32_t, bool, uint8_t);
using LooseTag = void (*)(UObject*, const uint64_t*, int32_t, uint8_t);
UpdateTags update_tags{};
LooseTag loose_tag{};

bool filter_combat(void* counts, const uint64_t* tag, int32_t delta, bool deferred, uint8_t replication) {
    // Filter before explicit counts, parent counts and listeners are updated.
    // This is limited to the current practice player's ASC; enemy tags are untouched.
    if (counts == gated_counts.load(std::memory_order_acquire) && tag && *tag == combat_name) return false;
    return update_tags(counts, tag, delta, deferred, replication);
}
UObject* component(UObject* actor, const wchar_t* type) {
    Call get(actor, L"GetComponentByClass"); get[L"ComponentClass"].set(find(type));
    return get.invoke().result<UObject*>();
}
bool in_combat(UObject* actor) {
    Call has(object(actor, L"AbilitySystemComponent"), L"HasMatchingGameplayTag");
    has[L"TagToCheck"].text(combat_tag); return has.invoke().result<bool>();
}
void gate(UObject* player) {
    auto* asc = object(player, L"AbilitySystemComponent");
    if (!asc) throw std::runtime_error("practice_ability_system_missing");
    Frame tag(static_cast<UStruct*>(find(L"/Script/GameplayTags.GameplayTag")));
    tag[L"TagName"].text(L"CharacterState.InCombat");
    combat_name = tag[L"TagName"].get<uint64_t>();
    // Build 26367565: the native loose-tag path passes ASC+0xee8 to
    // FGameplayTagCountContainer. The executable and both entry points are checked.
    auto* counts = reinterpret_cast<std::byte*>(asc) + 0xee8;
    struct Entry { uint64_t name; int32_t count; uint8_t replication; uint8_t padding[3]; };
    struct Array { Entry* data; int32_t size, capacity; };
    Array entries{}; std::memcpy(&entries, counts, sizeof(entries));
    if (entries.size < 0 || entries.size > 65536 || entries.capacity < entries.size || (entries.size && !entries.data))
        throw std::runtime_error("practice_tag_layout_changed");
    for (int32_t i = 0; i < entries.size; ++i) {
        const auto entry = entries.data[i];
        if (entry.name == combat_name && entry.count > 0) {
            loose_tag(asc, &combat_name, -entry.count, entry.replication);
            break;
        }
    }
    if (in_combat(player)) throw std::runtime_error("practice_combat_tag_not_cleared");
    gated_counts.store(counts, std::memory_order_release);
}
bool controlled(UObject* actor) {
    auto* faction = Call(actor, L"GetFactionComponent").invoke().result<UObject*>();
    if (!faction) throw std::runtime_error("practice_faction_missing");
    Call get(faction, L"GetFaction"); get.invoke();
    return field(static_cast<UStruct*>(find(L"/Script/GameplayTags.GameplayTag")),
        get[L"ReturnValue"].data, L"TagName").text() == L"Faction.Player";
}
bool lute_control_active(UObject* actor) {
    auto* effect = load_class(L"/PBZCombatAsset/ActorData/Players/Hun/ShaqiWeapon/Lute/UpgradeData/GE_Weapon_Lute_ControlMonster.GE_Weapon_Lute_ControlMonster_C");
    if (!effect) throw std::runtime_error("practice_control_effect_missing");
    Call effects(object(actor, L"AbilitySystemComponent"), L"GetActiveEffects");
    auto* query = static_cast<UStruct*>(find(L"/Script/GameplayAbilities.GameplayEffectQuery"));
    field(query, effects[L"Query"].data, L"EffectDefinition").set(effect);
    effects.invoke();
    struct Handles { void* data; int32_t size, capacity; };
    return effects[L"ReturnValue"].get<Handles>().size > 0;
}
bool reacting(UObject* actor) {
    Call has(object(actor, L"AbilitySystemComponent"), L"HasAnyMatchingGameplayTags");
    has[L"TagContainer"].text(L"(GameplayTags=((TagName=\"CharacterState.HitReact.State\"),(TagName=\"CharacterState.FallDown\"),(TagName=\"CharacterState.Space.Air\")))");
    return has.invoke().result<bool>();
}
bool dead(UObject* actor) {
    if (Attributes::read(actor, L"Health") <= 0) return true;
    Call has(object(actor, L"AbilitySystemComponent"), L"HasAnyMatchingGameplayTags");
    has[L"TagContainer"].text(L"(GameplayTags=((TagName=\"CharacterState.HitReact.State.Dead\"),(TagName=\"CharacterState.Hitreact.State.DeadLoop\")))");
    return has.invoke().result<bool>();
}
void destroy(UObject* actor) { if (actor && valid(actor)) invoke(actor, L"K2_DestroyActor"); }
}

void Practice::initialize() {
    if (update_tags) return;
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    const auto* pe = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    constexpr uint8_t count_signature[]{0x48,0x89,0x5c,0x24,0x18,0x44,0x88,0x4c,0x24,0x20,0x48,0x89,0x54,0x24,0x10};
    constexpr uint8_t loose_signature[]{0x48,0x8b,0xc4,0x48,0x83,0xec,0x58,0x48,0x89,0x58,0x08};
    auto* address = reinterpret_cast<void*>(base + 0x4d2bb30);
    auto* loose = reinterpret_cast<void*>(base + 0x4ca8a10);
    if (pe->FileHeader.TimeDateStamp != 0x6ae7ae3f || pe->OptionalHeader.SizeOfImage != 0x1e0bb000
        || std::memcmp(address, count_signature, sizeof(count_signature))
        || std::memcmp(loose, loose_signature, sizeof(loose_signature)))
        throw std::runtime_error("unsupported_practice_build");
    const auto init = MH_Initialize();
    if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) throw std::runtime_error("practice_hook_init");
    if (MH_CreateHook(address, reinterpret_cast<void*>(&filter_combat), reinterpret_cast<void**>(&update_tags)) != MH_OK)
        throw std::runtime_error("practice_hook_create");
    if (MH_EnableHook(address) != MH_OK) {
        MH_RemoveHook(address); update_tags = nullptr;
        throw std::runtime_error("practice_hook_enable");
    }
    loose_tag = reinterpret_cast<LooseTag>(loose);
}

double Practice::time() const {
    Call get(library(L"Engine", L"GameplayStatics"), L"GetTimeSeconds");
    get[L"WorldContextObject"].set(player_.Get()); return get.invoke().result<double>();
}
std::wstring Practice::equipment() const {
    const auto loadout = loadout::capture();
    std::wstring result;
    // Active weapon indices are deliberately excluded: swapping equipped weapons
    // is part of a combo, not a new loadout.
    for (const auto& slot : loadout.weapons) result += wide(slot.item_id) + L":" + std::to_wstring(slot.level) + L";";
    auto* bag = UObjectGlobals::FindFirstOf(L"PBZBackPackSubsystem");
    for (const auto* name : {L"AssemblyAccessory", L"Accessories", L"TopMedicineID", L"BottomMedicineID"})
        result += field(bag, name).text() + L";";
    return result;
}
void Practice::clear_stats() {
    const bool archive = session_.sample.hits || session_.sample.ally_damage || session_.sample.other_damage || session_.sample.conversions;
    if (archive) {
        session_.previous_sample = session_.sample;
        ++session_.sample_number;
    }
    session_.sample = {}; first_hit_ = -1;
    for (auto& [id, report] : session_.target_reports) {
        if (archive) {
            report.previous_sample = report.sample; report.previous_ally_output = report.ally_output;
        }
        report.sample = {}; report.ally_output = 0;
    }
    if (active()) equipment_ = equipment();
}
void Practice::begin(UObject* player, UObject* anchor, const Selection& selection) {
    stop();
    validate(selection);
    initialize(); player_ = player;
    selection_ = selection;
    session_.sample = {}; session_.previous_sample = {}; session_.sample_number = 1;
    session_.target_reports.clear();
    first_hit_ = -1; equipment_ = equipment();
    gate(player);
    Call language(library(L"Engine", L"KismetInternationalizationLibrary"), L"GetCurrentLanguage");
    language.invoke(); chinese_ = static_cast<FString*>(language[L"ReturnValue"].data)->str().starts_with(L"zh");
    auto* hud_class = load_class(L"/Game/PhantomBladeArena/W_ArenaStats.W_ArenaStats_C");
    if (!hud_class) throw std::runtime_error("practice_hud_missing");
    Call create(library(L"UMG", L"WidgetBlueprintLibrary"), L"Create");
    create[L"WorldContextObject"].set(player); create[L"WidgetType"].set(hud_class);
    hud_ = create.invoke().result<UObject*>();
    if (!hud_.Get()) throw std::runtime_error("practice_hud_create_failed");
    invoke(hud_.Get(), L"AddToViewport");
    Call visibility(hud_.Get(), L"SetVisibility"); visibility[L"InVisibility"].text(L"Collapsed"); visibility.invoke();
    hud_update_ = -1; hud_hidden_ = true;
    // Retire the scene's story encounter; our spawners have no story callbacks,
    // boss behavior overrides or persistent enemy IDs.
    field(anchor, L"ShouldSpawn").text(L"False");
    destroy(object(anchor, L"SpawnedAI"));
    Call origin(anchor, L"K2_GetActorTransform"); origin.invoke();
    anchor_ = anchor; anchor_transform_ = origin[L"ReturnValue"].text();
    configure(selection);
}
void Practice::configure(const Selection& selection) {
    validate(selection);
    if (!active()) return;
    const auto count = selection.target_count();
    std::map<uint32_t, std::wstring> placements;
    // Validate placements before touching any live target. A rejected edit must
    // not destroy the selected actor or interrupt the rest of the group.
    for (size_t i = 0; i < count; ++i) {
        const auto& config = selection.targets[i];
        const auto found = std::ranges::find_if(targets_, [&](const Target& t) { return t.config.id == config.id; });
        if (found == targets_.end() || found->config.x != config.x || found->config.y != config.y || found->config.yaw != config.yaw)
            placements.emplace(config.id, placement(config));
    }
    selection_ = selection;
    const auto wanted = [&](uint32_t id) {
        return std::find_if(selection.targets.begin(), selection.targets.begin() + count, [&](const auto& t) { return t.id == id; });
    };
    std::erase_if(targets_, [&](Target& target) {
        if (wanted(target.config.id) != selection.targets.begin() + count) return false;
        destroy_target(target); return true;
    });
    for (size_t i = 0; i < count; ++i) {
        const auto& config = selection.targets[i];
        auto found = std::ranges::find_if(targets_, [&](const Target& target) { return target.config.id == config.id; });
        if (found != targets_.end()) {
            found->config = config;
            if (const auto p = placements.find(config.id); p != placements.end()) found->spawn_transform = p->second;
            continue;
        }
        targets_.emplace_back(); targets_.back().config = config;
        targets_.back().spawn_transform = placements.at(config.id);
        spawn(targets_.back());
    }
}
std::wstring Practice::placement(const TargetConfig& config) const {
    auto* transform_type = static_cast<UStruct*>(find(L"/Script/CoreUObject.Transform"));
    Call transform(anchor_.Get(), L"K2_GetActorTransform"); transform.invoke();
    auto output = transform[L"ReturnValue"]; output.text(anchor_transform_);
    auto location = field(transform_type, output.data, L"Translation");
    auto position = location.get<Vector>();
    auto rotation = field(transform_type, output.data, L"Rotation");
    struct Quaternion { double x, y, z, w; };
    const auto q = rotation.get<Quaternion>();
    const double yaw = std::atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z));
    position.x += config.x * std::cos(yaw) - config.y * std::sin(yaw);
    position.y += config.x * std::sin(yaw) + config.y * std::cos(yaw);
    Call ground(library(L"Engine", L"KismetSystemLibrary"), L"LineTraceSingle");
    ground[L"WorldContextObject"].set(player_.Get());
    ground[L"Start"].set(Vector{position.x, position.y, position.z + 500});
    ground[L"End"].set(Vector{position.x, position.y, position.z - 1500});
    ground[L"TraceChannel"].text(L"TraceTypeQuery1"); ground[L"bIgnoreSelf"].set(true);
    Call actors(library(L"Engine", L"GameplayStatics"), L"GetAllActorsOfClass");
    actors[L"WorldContextObject"].set(player_.Get()); actors[L"ActorClass"].set(find(L"/Script/ProjectPBZ.PBZCharacter")); actors.invoke();
    ground[L"ActorsToIgnore"].copy(actors[L"OutActors"]);
    if (!ground.invoke().result<bool>()) throw std::runtime_error("practice_target_ground_missing");
    auto* hit_type = static_cast<UStruct*>(find(L"/Script/Engine.HitResult"));
    if (field(hit_type, ground[L"OutHit"].data, L"ImpactNormal").get<Vector>().z < .5)
        throw std::runtime_error("practice_target_ground_invalid");
    position.z = field(hit_type, ground[L"OutHit"].data, L"ImpactPoint").get<Vector>().z + 150;
    location.set(position);
    const auto angle = (yaw + config.yaw * 3.14159265358979323846 / 180.0) / 2;
    rotation.set(Quaternion{0, 0, std::sin(angle), std::cos(angle)});
    return output.text();
}
void Practice::spawn(Target& target) {
    destroy_target(target);
    auto* anchor = anchor_.Get();
    if (!anchor) throw std::runtime_error("practice_anchor_lost");
    const auto& config = target.config;
    target.awaiting_spawn = true; target.spawn_since = time();
    session_.target_reports[config.id].state = TargetState::Spawning;
    auto* info_type = static_cast<UStruct*>(find(L"/Script/SGameAI.AISpawnInfo"));
    Call begin(library(L"Engine", L"GameplayStatics"), L"BeginDeferredActorSpawnFromClass");
    begin[L"WorldContextObject"].set(player_.Get()); begin[L"ActorClass"].set(anchor->GetClassPrivate());
    begin[L"SpawnTransform"].text(target.spawn_transform);
    begin[L"CollisionHandlingOverride"].text(L"AdjustIfPossibleButAlwaysSpawn");
    auto* spawner = begin.invoke().result<UObject*>();
    if (!spawner) throw std::runtime_error("practice_spawn_failed");
    target.spawner = spawner;
    field(spawner, L"SpawnerID").text(L"ArenaPractice_" + std::to_wstring(config.id));
    field(spawner, L"ShouldSpawn").text(L"False");
    field(spawner, L"bDisableNPCOptimization").text(L"True");
    Frame info(info_type);
    info[L"MobDomain"].copy(field(info_type, field(anchor, L"SpawnInfo").data, L"MobDomain"));
    info[L"MobGroupName"].text(L"ArenaPractice");
    info[L"bAutoStartAI"].text(L"False");
    info[L"bStartAIOnPlayerEnterDomain"].text(L"False");
    info[L"bStopAIOnPlayerExitDomain"].text(L"False");
    info[L"bHiddenByDefault"].text(L"False");
    field(spawner, L"SpawnInfo").copy({field(spawner, L"SpawnInfo").property, info.data()});
    Call set(spawner, L"SetClassToSpawn"); set[L"InClassToSpawn"].text(practice_catalog::kind(config.kind).actor_class); set.invoke();
    Call finish(library(L"Engine", L"GameplayStatics"), L"FinishSpawningActor");
    finish[L"Actor"].set(spawner); finish[L"SpawnTransform"].copy(begin[L"SpawnTransform"]); finish.invoke();
    field(spawner, L"ShouldSpawn").text(L"True");
    invoke(spawner, L"Respawn");
    target.spawned_config = config;
}
void Practice::prepare(Target& target, UObject* actor) {
    auto* ai = component(actor, L"/Script/SGameAI.SGameAIComponent");
    if (!ai || !field(ai, L"bInitialized").get<bool>()) return;
    if (auto* label = target.label.Get()) invoke(label, L"RemoveFromParent");
    target.label.Reset();
    invoke(ai, L"StopAI");
    // Only health is replenished. Native guard, break, execution, elemental
    // buildup, faction changes and the lute's control effect remain intact.
    target.actor = actor; target.ai = ai; target.controlled = false;
    target.awaiting_spawn = false;
    target.mesh = object(actor, L"Mesh");
    target.anim = Call(target.mesh.Get(), L"GetAnimInstance").invoke().result<UObject*>();
    if (!target.anim.Get()) throw std::runtime_error("practice_animation_missing");
    target.animation_rate = field(target.mesh.Get(), L"GlobalAnimRateScale").get<float>();
    target.recover_until = time() + 0.25;
    target.pose_montage.Reset();
    target.motion.clear(); target.pose_percent = -1;
    auto* label_class = load_class(L"/Game/PhantomBladeArena/W_ArenaTargetLabel.W_ArenaTargetLabel_C");
    if (!label_class) throw std::runtime_error("practice_target_label_missing");
    Call create(library(L"UMG", L"WidgetBlueprintLibrary"), L"Create");
    create[L"WorldContextObject"].set(player_.Get()); create[L"WidgetType"].set(label_class);
    target.label = create.invoke().result<UObject*>();
    if (!target.label.Get()) throw std::runtime_error("practice_target_label_failed");
    Call add(target.label.Get(), L"AddToViewport"); add[L"ZOrder"].set<int32_t>(1); add.invoke();
    Call align(target.label.Get(), L"SetAlignmentInViewport"); align[L"Alignment"].text(L"(X=0.5,Y=1)"); align.invoke();
    Call size(target.label.Get(), L"SetDesiredSizeInViewport"); size[L"Size"].text(L"(X=180,Y=30)"); size.invoke();
    Call hide(target.label.Get(), L"SetVisibility"); hide[L"InVisibility"].text(L"Collapsed"); hide.invoke();
    Attributes::tune_boss(actor, selection_.config.boss_health_percent / 100.f, selection_.config.boss_attack_percent / 100.f);
    Call tags(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"AddLooseGameplayTags");
    tags[L"Actor"].set(actor);
    tags[L"GameplayTags"].text(L"(GameplayTags=((TagName=\"CharacterType.AI.ExcludeFromBossRecord\")))"); tags.invoke();
}
void Practice::thaw(Target& target) {
    if (auto* mesh = target.mesh.Get()) field(mesh, L"GlobalAnimRateScale").set(target.animation_rate);
    if (auto* montage = target.pose_montage.Get(); montage && target.anim.Get()) {
        Call stop(target.anim.Get(), L"Montage_Stop");
        stop[L"InBlendOutTime"].set(0.f); stop[L"Montage"].set(montage); stop.invoke();
    }
    target.pose_montage.Reset(); target.motion.clear(); target.pose_percent = -1;
}
void Practice::freeze(Target& target) {
    const auto& config = target.config;
    // Stop locomotion without pausing the ASC's status/recovery timers.
    invoke(object(target.actor.Get(), L"CharacterMovement"), L"StopMovementImmediately");
    if (target.motion == config.motion && target.pose_percent == config.pose_percent && target.pose_montage.Get()) return;
    thaw(target);
    auto* sequence = load_object(practice_catalog::motion(config).sequence);
    if (!sequence) throw std::runtime_error("practice_pose_missing");
    const auto length = Call(sequence, L"GetPlayLength").invoke().result<float>();
    Call pose(target.anim.Get(), L"PlaySlotAnimationAsDynamicMontage");
    pose[L"Asset"].set(sequence); pose[L"SlotNodeName"].text(L"DefaultSlot");
    pose[L"BlendInTime"].set(0.f); pose[L"BlendOutTime"].set(0.f);
    pose[L"InPlayRate"].set(1.f); pose[L"LoopCount"].set<int32_t>(1);
    pose[L"BlendOutTriggerTime"].set(-1.f);
    pose[L"InTimeToStartMontageAt"].set(std::min(length * config.pose_percent / 100.f, std::max(0.f, length - .001f)));
    auto* montage = pose.invoke().result<UObject*>();
    if (!montage) throw std::runtime_error("practice_pose_rejected");
    target.pose_montage = montage;
    Call pause(target.anim.Get(), L"Montage_Pause"); pause[L"Montage"].set(montage); pause.invoke();
    // The visual montage never advances and no attack ability is activated.
    // Keep the native animation instance so hit reactions can replace the pose.
    field(target.mesh.Get(), L"GlobalAnimRateScale").set(0.f);
    target.motion = config.motion; target.pose_percent = config.pose_percent;
}
bool Practice::tick() {
    if (!active()) {
        if (!targets_.empty()) { stop(); throw std::runtime_error("practice_player_lost"); }
        return false;
    }
    if (in_combat(player_.Get())) throw std::runtime_error("practice_combat_invariant_failed");
    bool ready = !targets_.empty();
    for (auto& target : targets_) {
        auto& report = session_.target_reports[target.config.id];
        auto* spawner = target.spawner.Get();
        if (!spawner) throw std::runtime_error("practice_spawner_lost");
        auto* actor = object(spawner, L"SpawnedAI");
        if (field(spawner, L"IsSpawning").get<bool>() || !actor) {
            if (!actor && !target.awaiting_spawn) spawn(target);
            if (time() - target.spawn_since > 90) throw std::runtime_error("practice_target_spawn_timeout");
            ready = false; continue;
        }
        if (target.actor.Get() != actor) {
            // SpawnedAI is published before asynchronous attributes/AI finish
            // loading. Zero health here is not yet a defeated practice target.
            if (Attributes::read(actor, L"Health") <= 0) {
                if (time() - target.spawn_since > 90) throw std::runtime_error("practice_target_attributes_timeout");
                ready = false; continue;
            }
            prepare(target, actor);
        }
        auto* ai = target.ai.Get();
        if (!ai) {
            if (time() - target.spawn_since > 90) throw std::runtime_error("practice_target_ai_timeout");
            ready = false; continue;
        }
        if (dead(actor)) {
            spawn(target); ready = false; continue;
        }
        const bool ally = controlled(actor);
        if (target.controlled && !lute_control_active(actor)) {
            // The native lute effect kills its subject on expiry. Replace the
            // subject even if a health override prevented that kill; never leave
            // an indefinitely friendly target after the timed effect has ended.
            spawn(target); ready = false; continue;
        }
        if (ally != target.controlled) {
            if (ally) { ++session_.sample.conversions; ++report.sample.conversions; }
            target.controlled = ally;
            thaw(target);
            target.recover_until = time() + 0.25;
        }
        if (field(ai, L"bAIStarted").get<bool>() != ally) invoke(ai, ally ? L"StartAI" : L"StopAI");
        auto* montage = Call(target.anim.Get(), L"GetCurrentActiveMontage").invoke().result<UObject*>();
        const bool native_montage = montage && montage != target.pose_montage.Get();
        if (ally || reacting(actor) || native_montage || time() < target.recover_until) {
            report.state = ally ? TargetState::Controlled : TargetState::Recovering;
            thaw(target);
        } else {
            const auto& old = target.spawned_config;
            const auto& next = target.config;
            if (old.kind != next.kind || old.x != next.x || old.y != next.y || old.yaw != next.yaw) {
                spawn(target); ready = false; continue;
            }
            report.state = TargetState::Frozen;
            freeze(target);
        }
    }
    running_ = running_ || ready;
    if (first_hit_ >= 0) {
        session_.sample.elapsed = std::max(0.0, time() - first_hit_);
        for (auto& [id, report] : session_.target_reports) report.sample.elapsed = session_.sample.elapsed;
    }
    update_hud();
    return ready;
}
void Practice::update_hud() {
    auto* hud = hud_.Get(); if (!hud) return;
    Call paused(library(L"Engine", L"GameplayStatics"), L"IsGamePaused");
    paused[L"WorldContextObject"].set(player_.Get());
    const bool hidden = paused.invoke().result<bool>();
    if (hidden != hud_hidden_) {
        Call visibility(hud, L"SetVisibility");
        visibility[L"InVisibility"].text(hidden ? L"Collapsed" : L"HitTestInvisible"); visibility.invoke();
        hud_hidden_ = hidden;
        if (hidden) for (auto& target : targets_) if (auto* label = target.label.Get()) visible(label, false);
    }
    const auto now = time();
    if (hidden || now < hud_update_) return;
    hud_update_ = now + 0.1;
    const auto message = [&](std::string_view key) {
        const auto& messages = chinese_ ? resources::zh_hans : resources::en;
        const auto it = std::ranges::find(messages, key, &Message::id);
        if (it == messages.end()) throw std::runtime_error("message_missing");
        return std::wstring(it->value);
    };
    const auto& s = session_.sample;
    const auto text = message("sample") + L" " + std::to_wstring(session_.sample_number)
        + L" · " + std::format(L"{:.1f}s", s.elapsed)
        + L"\n" + message("totalDamage") + std::format(L" {:.1f} · DPS {:.1f}", s.damage, s.elapsed > 0 ? s.damage / s.elapsed : 0)
        + L"\n" + message("lastMax") + std::format(L" {:.1f} / {:.1f}", s.last, s.maximum)
        + L"\n" + message("allyDamage") + std::format(L" {:.1f}", s.ally_damage)
        + L" · " + message("conversions") + L" " + std::to_wstring(s.conversions);
    set_text(object(hud, L"Stats"), text);
    Call controller(library(L"Engine", L"GameplayStatics"), L"GetPlayerController");
    controller[L"WorldContextObject"].set(player_.Get()); controller[L"PlayerIndex"].set<int32_t>(0);
    auto* pc = controller.invoke().result<UObject*>();
    for (auto& target : targets_) {
        auto* label = target.label.Get(); auto* actor = target.actor.Get();
        if (!label || !actor || !pc) continue;
        auto position = Call(actor, L"K2_GetActorLocation").invoke().result<Vector>(); position.z += 150;
        Call project(library(L"UMG", L"WidgetLayoutLibrary"), L"ProjectWorldLocationToWidgetPosition");
        project[L"PlayerController"].set(pc); project[L"WorldLocation"].set(position); project[L"bPlayerViewportRelative"].set(true);
        const bool onscreen = project.invoke().result<bool>();
        Call show(label, L"SetVisibility"); show[L"InVisibility"].text(onscreen ? L"HitTestInvisible" : L"Collapsed"); show.invoke();
        if (!onscreen) continue;
        Call place(label, L"SetPositionInViewport"); place[L"Position"].copy(project[L"ScreenPosition"]);
        place[L"bRemoveDPIScale"].set(false); place.invoke();
        const bool selected = session_.editing_current_targets() && session_.selected_target_config().id == target.config.id;
        const auto& report = session_.target_reports.at(target.config.id);
        set_text(object(label, L"Label"), (selected ? L"› #" : L"#") + std::to_wstring(target.config.id)
            + std::format(L"  {:.0f}", report.sample.damage + report.sample.ally_damage + report.sample.other_damage));
    }
}
void Practice::event(UObject* actor, UFunction* function, void* parameters) {
    if (!active() || !running_) return;
    const auto target = std::ranges::find_if(targets_, [&](const Target& t) { return t.actor.Get() == actor; });
    if (target == targets_.end() || function->GetName() != L"OnDamage") return;
    thaw(*target); target->recover_until = time() + 0.25;
    // GE_Kill at control expiry is cleanup, not player DPS. Let the native
    // death complete; the corresponding spawner restores a fresh frozen target.
    if (target->controlled && !lute_control_active(actor)) return;
    const auto damage = field(function, parameters, L"DamageAmount").get<float>();
    // PBZCharacter's damage delegate dispatches OnDamage before its native
    // Health<=0 death check, so restoration here also covers a lethal single hit.
    Attributes::write(actor, L"Health", Attributes::read(actor, L"Health_Max"));
    if (!std::isfinite(damage) || damage <= 0) return;
    const auto current = equipment();
    if (current != equipment_) { clear_stats(); equipment_ = current; }
    if (first_hit_ < 0) first_hit_ = time();
    Call source(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"EffectContextGetOriginalInstigator");
    source[L"EffectContext"].copy(field(function, parameters, L"EffectContext"));
    auto* instigator = source.invoke().result<UObject*>();
    bool from_player = false, from_ally = false;
    uint32_t ally_id{};
    for (int depth = 0; instigator && depth < 8; ++depth) {
        if (instigator == player_.Get()) { from_player = true; break; }
        const auto member = std::ranges::find_if(targets_, [&](const Target& t) { return t.actor.Get() == instigator; });
        if (member != targets_.end()) { from_ally = controlled(instigator); ally_id = member->config.id; break; }
        if (Call(instigator, L"GetInstigator").invoke().result<UObject*>() == player_.Get()) { from_player = true; break; }
        instigator = Call(instigator, L"GetOwner").invoke().result<UObject*>();
    }
    for (auto* sample : {&session_.sample, &session_.target_reports[target->config.id].sample}) {
        if (from_player) {
            sample->damage += damage; ++sample->hits; sample->last = damage;
            sample->maximum = std::max(sample->maximum, damage);
        } else if (from_ally) sample->ally_damage += damage;
        else sample->other_damage += damage;
    }
    if (from_ally) session_.target_reports[ally_id].ally_output += damage;
}
void Practice::destroy_target(Target& target) noexcept {
    const auto cleanup = [](auto&& action) {
        try { action(); }
        catch (const std::exception&) { RC::Output::send(L"[PhantomBladeArena] Target cleanup incomplete; return to reload the world.\n"); }
    };
    cleanup([&] { if (auto* label = target.label.Get()) invoke(label, L"RemoveFromParent"); });
    cleanup([&] { if (auto* spawner = target.spawner.Get()) field(spawner, L"ShouldSpawn").text(L"False"); });
    cleanup([&] { if (auto* spawner = target.spawner.Get()) destroy(object(spawner, L"SpawnedAI")); });
    cleanup([&] { destroy(target.actor.Get()); });
    cleanup([&] { destroy(target.spawner.Get()); });
    auto config = std::move(target.config);
    auto transform = std::move(target.spawn_transform);
    target = Target{}; target.config = std::move(config); target.spawn_transform = std::move(transform);
}
void Practice::stop() noexcept {
    gated_counts.store(nullptr, std::memory_order_release);
    const auto cleanup = [](auto&& action) {
        try { action(); }
        catch (const std::exception&) { RC::Output::send(L"[PhantomBladeArena] Practice cleanup incomplete; return to reload the world.\n"); }
    };
    cleanup([&] { if (auto* hud = hud_.Get()) invoke(hud, L"RemoveFromParent"); });
    hud_.Reset();
    player_.Reset(); running_ = false; first_hit_ = -1;
    auto targets = std::move(targets_); targets_.clear();
    for (auto& target : targets) destroy_target(target);
    anchor_.Reset(); anchor_transform_.clear();
}
}
