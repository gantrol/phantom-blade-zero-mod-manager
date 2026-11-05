#pragma once
#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>
#include <stdexcept>

namespace arena
{
enum class LoadoutMode : uint8_t { Current = 0, Recommended = 1, Custom = 2 };

struct EntryConfig
{
    LoadoutMode mode{LoadoutMode::Recommended};
    int32_t max_health{800};
    int32_t weapon_level{18};
    bool operator==(const EntryConfig&) const = default;
};

struct Selection
{
    size_t boss{}, scene{};
    EntryConfig config{};
    bool operator==(const Selection&) const = default;
};

enum class Phase { Ready, Entering, Fighting, Paused, Ended, Resetting, Returning };

class Session
{
public:
    Selection pending{};
    std::optional<Selection> current;
    std::optional<size_t> scene_preview;
    Phase phase{Phase::Ready};
    bool lock_health{};
    bool player_defeated{};
    bool practice_session{};
    int32_t player_max_health{800};
    int32_t player_weapon_level{18};

    bool busy() const { return phase == Phase::Entering || phase == Phase::Resetting || phase == Phase::Returning; }
    bool different() const { return !current || *current != pending; }
    bool continuable() const { return current && (phase == Phase::Fighting || phase == Phase::Paused); }

    void reset_default(size_t default_scene, int32_t rec_hp, int32_t rec_wpn)
    {
        pending.boss = 0;
        pending.scene = default_scene;
        pending.config = {LoadoutMode::Recommended, rec_hp, rec_wpn};
    }

    void select_boss(size_t boss, size_t default_scene, int32_t rec_hp, int32_t rec_wpn)
    {
        if (busy() || scene_preview || pending.boss == boss) return;
        pending.boss = boss;
        pending.scene = default_scene;
        if (pending.config.mode == LoadoutMode::Recommended)
        {
            pending.config.max_health = rec_hp;
            pending.config.weapon_level = rec_wpn;
        }
        else if (pending.config.mode == LoadoutMode::Current)
        {
            pending.config.max_health = player_max_health;
            pending.config.weapon_level = player_weapon_level;
        }
    }

    void select_loadout_mode(LoadoutMode mode, int32_t rec_hp, int32_t rec_wpn)
    {
        if (busy() || scene_preview) return;
        pending.config.mode = mode;
        if (mode == LoadoutMode::Current)
        {
            pending.config.max_health = player_max_health;
            pending.config.weapon_level = player_weapon_level;
        }
        else if (mode == LoadoutMode::Recommended)
        {
            pending.config.max_health = rec_hp;
            pending.config.weapon_level = rec_wpn;
        }
    }

    void step_health(int32_t delta)
    {
        if (busy() || scene_preview) return;
        pending.config.mode = LoadoutMode::Custom;
        pending.config.max_health = std::clamp(pending.config.max_health + delta, 100, 2000);
    }

    void step_weapon(int32_t delta)
    {
        if (busy() || scene_preview) return;
        pending.config.mode = LoadoutMode::Custom;
        pending.config.weapon_level = std::clamp(pending.config.weapon_level + delta, 0, 30);
    }

    void preview_scene(size_t scene) { if (scene_preview) scene_preview = scene; }
    void open_scenes() { if (!busy()) scene_preview = pending.scene; }
    void use_scene() { if (scene_preview) { pending.scene = *scene_preview; scene_preview.reset(); } }
    void cancel_scene() { scene_preview.reset(); }
    void pause() { if (phase == Phase::Fighting) phase = Phase::Paused; }
    void resume() { if (continuable()) phase = Phase::Fighting; }
    void accept_started(Selection selection) { current = selection; phase = Phase::Fighting; }
    void finish(bool player) { phase = Phase::Ended; player_defeated = player; }
    void clear()
    {
        const auto saved_hp = player_max_health;
        const auto saved_wpn = player_weapon_level;
        *this = Session{};
        player_max_health = saved_hp;
        player_weapon_level = saved_wpn;
    }
};
}
