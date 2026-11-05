#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <utility>

namespace arena::resources
{
inline constexpr size_t max_slots = 64;
struct LocalText { const wchar_t* space; const wchar_t* key; };
struct Boss
{
    const char* id;
    const wchar_t* actor_class;
    LocalText name;
    LocalText chapter;
    const wchar_t* portrait;
    size_t default_scene;
    int32_t recommended_max_health;
    int32_t recommended_weapon_level;
};
struct Scene
{
    const char* id;
    const wchar_t* level;
    const wchar_t* spawner;
    LocalText name;
    std::array<double, 3> position;
    const wchar_t* preview;
};
struct Message { std::string_view id; const wchar_t* value; };
}

namespace arena
{
using resources::LocalText;
using resources::Boss;
using resources::Scene;
using resources::Message;

inline size_t scene_at_slot(size_t default_scene, size_t slot)
{
    return slot == 0 ? default_scene : (slot <= default_scene ? slot - 1 : slot);
}

inline size_t slot_for_scene(size_t default_scene, size_t scene)
{
    return scene == default_scene ? 0 : (scene < default_scene ? scene + 1 : scene);
}
}
