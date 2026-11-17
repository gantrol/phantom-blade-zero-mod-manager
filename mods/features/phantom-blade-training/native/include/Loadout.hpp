#pragma once
#include "Selection.hpp"
#include "Reflection.hpp"
#include <nlohmann/json.hpp>

namespace arena::loadout {
EntryConfig capture();
std::vector<OwnedWeapon> owned();
std::optional<EntryConfig> stage_equipment(const Session& session);
void apply(nlohmann::json& save, const nlohmann::json& baseline, const EntryConfig& config);
void verify(const EntryConfig& config);
}
