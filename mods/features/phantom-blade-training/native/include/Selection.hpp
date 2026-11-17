#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>
#include <utility>

namespace arena {
enum class LoadoutMode : uint8_t { Stage, Adaptive, Custom };
enum class PracticeMode : uint8_t { Boss, Dummy, Group };
inline constexpr size_t max_practice_targets = 16;
struct TargetConfig {
    uint32_t id{1};
    std::string kind{"sword"}, motion{"idle"};
    int32_t pose_percent{25};
    int32_t x{}, y{}, yaw{}; // Arena-relative centimetres and degrees.
    bool operator==(const TargetConfig&) const = default;
};
enum class TargetState { Spawning, Frozen, Recovering, Controlled };
struct DamageSample {
    double damage{}, ally_damage{}, other_damage{}, elapsed{};
    float last{}, maximum{};
    uint64_t hits{}, conversions{};
};
struct TargetReport {
    DamageSample sample{}, previous_sample{};
    double ally_output{}, previous_ally_output{};
    TargetState state{TargetState::Spawning};
};
struct WeaponSlot {
    std::string item_id;
    int32_t level{1};
    bool operator==(const WeaponSlot&) const = default;
};
struct OwnedWeapon {
    WeaponSlot weapon;
    bool main{}, snap_soul{};
    int32_t max_level{1}, min_level{1};
};
struct EntryConfig {
    LoadoutMode mode{LoadoutMode::Custom};
    // Zero leaves the engine's equipment/growth calculation intact.
    int32_t max_health{};
    std::array<WeaponSlot, 4> weapons{};
    int32_t active_main{}, active_phantom{};
    int32_t difficulty{-1}, cycle{-1}; // -1 retains the entry save's setting.
    int32_t boss_health_percent{100}, boss_attack_percent{100};
    bool copied_stage_equipment{};
    bool operator==(const EntryConfig&) const = default;
};
struct Selection {
    size_t boss{}, scene{};
    EntryConfig config{};
    PracticeMode practice{PracticeMode::Boss};
    std::vector<TargetConfig> targets{{1, "sword", "idle", 25, 0, -250},
        {2, "sword", "idle", 25, 0, 0}, {3, "sword", "idle", 25, 0, 250}};
    uint32_t next_target_id{4};
    size_t target_count() const { return practice == PracticeMode::Dummy ? std::min(size_t{1}, targets.size()) : targets.size(); }
    bool operator==(const Selection&) const = default;
};
enum class Phase { Ready, Entering, Fighting, Paused, Ended, Resetting, Returning };
class Session {
    std::map<size_t, EntryConfig> custom_drafts_;
public:
    Selection pending{};
    std::optional<Selection> current;
    std::optional<size_t> scene_preview;
    Phase phase{Phase::Ready};
    bool lock_health{}, player_defeated{}, practice_session{}, baseline_ready{};
    int32_t player_max_health{};
    int32_t player_difficulty{2}, player_cycle{};
    EntryConfig player_loadout{};
    std::vector<OwnedWeapon> owned_weapons;
    DamageSample sample{}, previous_sample{};
    uint32_t sample_number{1};
    uint32_t selected_target{1};
    bool edit_current_targets{};
    std::map<uint32_t, TargetReport> target_reports;
    bool editing_current_targets() const { return edit_current_targets && current && current->practice != PracticeMode::Boss; }
    Selection& target_selection() { return editing_current_targets() ? *current : pending; }
    const Selection& target_selection() const { return editing_current_targets() ? *current : pending; }
    bool targets_editable() const { return !busy() && !scene_preview && target_selection().practice != PracticeMode::Boss; }
    const TargetConfig& selected_target_config() const {
        const auto& s = target_selection();
        for (size_t i = 0; i < s.target_count(); ++i) if (s.targets[i].id == selected_target) return s.targets[i];
        return s.targets.front();
    }
    TargetConfig& selected_target_config() {
        return const_cast<TargetConfig&>(std::as_const(*this).selected_target_config());
    }
    void select_target(int delta) {
        const auto& s = target_selection();
        const auto id = selected_target_config().id;
        auto it = std::ranges::find(s.targets, id, &TargetConfig::id);
        const auto index = static_cast<int>(it - s.targets.begin());
        const auto count = static_cast<int>(s.target_count());
        selected_target = s.targets[(index + delta + count) % count].id;
    }
    void add_target() {
        if (!targets_editable()) return;
        auto& s = target_selection();
        if (s.practice != PracticeMode::Group || s.targets.size() >= max_practice_targets) return;
        auto config = selected_target_config(); config.id = s.next_target_id++;
        // Allocate an unused grid position without moving existing targets.
        for (int cell = 0; cell < 100; ++cell) {
            config.x = (cell / 5) * 250 - 500; config.y = (cell % 5) * 250 - 500;
            if (std::ranges::none_of(s.targets, [&](const auto& t) { return std::abs(t.x - config.x) < 150 && std::abs(t.y - config.y) < 150; })) break;
        }
        selected_target = config.id; s.targets.push_back(std::move(config));
    }
    void remove_target() {
        if (!targets_editable()) return;
        auto& s = target_selection();
        if (s.practice != PracticeMode::Group || s.targets.size() <= 1) return;
        const auto id = selected_target_config().id;
        std::erase_if(s.targets, [&](const auto& t) { return t.id == id; });
        selected_target = s.targets.front().id;
    }
    bool busy() const { return phase == Phase::Entering || phase == Phase::Resetting || phase == Phase::Returning; }
    bool different() const { return !current || *current != pending; }
    bool continuable() const { return current && (phase == Phase::Fighting || phase == Phase::Paused); }
    bool editable() const { return !busy() && !scene_preview && pending.config.mode == LoadoutMode::Custom && baseline_ready; }
    bool startable() const { return editable() && !pending.config.weapons[0].item_id.empty(); }
    void remember_custom() {
        if (pending.config.mode == LoadoutMode::Custom && baseline_ready) custom_drafts_[pending.boss] = pending.config;
    }
    void reset_default(size_t scene, size_t boss) {
        pending = {boss, scene, player_loadout};
        selected_target = 1; edit_current_targets = false;
        custom_drafts_.clear();
    }
    void select_boss(size_t boss, size_t scene) {
        if (busy() || scene_preview || pending.boss == boss) return;
        remember_custom();
        const auto mode = pending.config.mode;
        pending.boss = boss; pending.scene = scene;
        const auto draft = custom_drafts_.find(boss);
        pending.config = draft == custom_drafts_.end() ? player_loadout : draft->second;
        pending.config.mode = mode;
    }
    void select_loadout_mode(LoadoutMode mode) {
        if (busy() || scene_preview || pending.config.mode == mode) return;
        remember_custom();
        const auto draft = custom_drafts_.find(pending.boss);
        pending.config = mode == LoadoutMode::Custom && draft != custom_drafts_.end() ? draft->second : player_loadout;
        pending.config.mode = mode;
    }
    void use_current() { pending.config = player_loadout; remember_custom(); }
    void step_boss(std::wstring_view setting, int delta) {
        if (!editable()) return;
        auto& c = pending.config;
        if (setting == L"Difficulty") c.difficulty = std::clamp((c.difficulty < 0 ? player_difficulty : c.difficulty) + delta, 0, 4);
        else if (setting == L"Cycle") c.cycle = std::clamp((c.cycle < 0 ? player_cycle : c.cycle) + delta, 0, 6);
        else if (setting == L"BossHealth") c.boss_health_percent = std::clamp(c.boss_health_percent + delta * 25, 25, 1000);
        else if (setting == L"BossAttack") c.boss_attack_percent = std::clamp(c.boss_attack_percent + delta * 25, 0, 500);
    }
    void reset_boss() {
        if (!editable()) return;
        pending.config.difficulty = pending.config.cycle = -1;
        pending.config.boss_health_percent = pending.config.boss_attack_percent = 100;
    }
    const OwnedWeapon* owned(std::string_view id, bool main) const {
        const auto it = std::ranges::find_if(owned_weapons, [&](const auto& w) { return w.main == main && w.weapon.item_id == id; });
        return it == owned_weapons.end() ? nullptr : &*it;
    }
    void cycle_weapon(size_t slot, int direction) {
        if (!editable() || slot >= 4 || (slot % 2 == 1 && pending.config.weapons[slot-1].item_id.empty())) return;
        const auto other = slot ^ 1;
        std::vector<WeaponSlot> choices;
        if (slot != 0 && (slot % 2 == 1 || pending.config.weapons[other].item_id.empty())) choices.emplace_back();
        for (const auto& w : owned_weapons)
            if (w.main == (slot < 2) && w.weapon.item_id != pending.config.weapons[other].item_id) choices.push_back(w.weapon);
        if (choices.empty()) return;
        const auto it = std::ranges::find(choices, pending.config.weapons[slot].item_id, &WeaponSlot::item_id);
        const auto current_index = it == choices.end() ? 0 : static_cast<int>(it - choices.begin());
        const int count = static_cast<int>(choices.size());
        pending.config.weapons[slot] = choices[(current_index + direction + count) % count];
        if (pending.config.weapons[slot].item_id.empty()) {
            if (slot < 2) pending.config.active_main = 0;
            else pending.config.active_phantom = 0;
        }
    }
    void step_weapon(size_t slot, int delta) {
        if (!editable() || slot >= 4) return;
        auto& w = pending.config.weapons[slot];
        if (const auto* item = owned(w.item_id, slot < 2)) w.level = std::clamp(w.level + delta, item->min_level, item->max_level);
    }
    void step_health(int delta) {
        if (!editable()) return;
        const auto current_hp = pending.config.max_health ? pending.config.max_health : player_max_health;
        pending.config.max_health = std::clamp(current_hp + delta, 50, 100000);
    }
    void preview_scene(size_t scene) { if (scene_preview) scene_preview = scene; }
    void open_scenes() { if (!busy()) scene_preview = pending.scene; }
    void use_scene() { if (scene_preview) { pending.scene = *scene_preview; scene_preview.reset(); } }
    void cancel_scene() { scene_preview.reset(); }
    void pause() { if (phase == Phase::Fighting) phase = Phase::Paused; }
    void resume() { if (continuable()) phase = Phase::Fighting; }
    void accept_started(Selection selection) {
        current = std::move(selection); phase = Phase::Fighting;
        edit_current_targets = current->practice != PracticeMode::Boss;
        if (edit_current_targets) selected_target = selected_target_config().id;
    }
    void finish(bool player) { phase = Phase::Ended; player_defeated = player; }
};
}
