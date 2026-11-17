#pragma once
#include "Resources.hpp"
#include "Selection.hpp"
#include <stdexcept>

namespace arena::practice_catalog {
inline const resources::PracticeKind& kind(std::string_view id) {
    const auto it = std::ranges::find(resources::practice_kinds, id, &resources::PracticeKind::id);
    if (it == resources::practice_kinds.end()) throw std::runtime_error("practice_kind_unknown");
    return *it;
}
inline const resources::PracticeMotion& motion(const TargetConfig& target) {
    const auto& k = kind(target.kind);
    for (size_t i = k.motion_begin; i < k.motion_begin + k.motion_count; ++i)
        if (target.motion == resources::practice_motions[i].id) return resources::practice_motions[i];
    throw std::runtime_error("practice_motion_unknown");
}
inline void step_kind(TargetConfig& target, int direction) {
    const auto index = &kind(target.kind) - resources::practice_kinds.data();
    const auto count = static_cast<int>(resources::practice_kinds.size());
    target.kind = resources::practice_kinds[(index + direction + count) % count].id;
    target.motion = "idle";
}
inline void step_motion(TargetConfig& target, int direction) {
    const auto& k = kind(target.kind);
    const auto index = &motion(target) - resources::practice_motions.data() - k.motion_begin;
    const auto count = static_cast<int>(k.motion_count);
    target.motion = resources::practice_motions[k.motion_begin + (static_cast<int>(index) + direction + count) % count].id;
}
}
