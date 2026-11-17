#include "Loadout.hpp"
#include "Resources.hpp"
#include <set>

namespace arena::loadout {
using namespace ue;
using Json = nlohmann::json;
namespace {
// Public weapon queries use (FName, bool) inputs and an int or bool return.
// Discover parameter names from reflection rather than assuming a particular spelling.
template<class T = int32_t>
T weapon_value(const wchar_t* method, std::string_view id, bool main) {
    auto* bag = UObjectGlobals::FindFirstOf(L"PBZBackPackSubsystem");
    auto* fn = bag ? bag->GetFunctionByNameInChain(method) : nullptr;
    if (!fn) throw std::runtime_error("backpack_query_missing");
    Frame args(fn);
    int names = 0, flags = 0;
    for (auto* f = fn->GetChildProperties(); f; f = f->next()) {
        const auto name = f->GetName();
        if (name == L"ReturnValue") continue;
        auto param = args[name.c_str()];
        if (param.property->GetElementSize() == 8) { param.text(wide(id)); ++names; }
        else if (param.property->GetElementSize() == 1) { param.set<bool>(main); ++flags; }
        else throw std::runtime_error("backpack_query_signature_changed");
    }
    if (names != 1 || flags != 1 || args[L"ReturnValue"].property->GetElementSize() != sizeof(T))
        throw std::runtime_error("backpack_query_signature_changed");
    bag->ProcessEvent(fn, args.data());
    return args[L"ReturnValue"].get<T>();
}
std::array<std::string, 2> equipped_ids(UObject* bag, bool main) {
    Call query(bag, main ? L"GetEquipmentNormalWeapons" : L"GetShaqiWeapons");
    query.invoke();
    const auto exported = query[L"ReturnValue"].text();
    if (exported.size() < 2 || exported.front() != L'(' || exported.back() != L')')
        throw std::runtime_error("equipment_slots_unsupported");
    auto values = std::wstring_view(exported).substr(1, exported.size() - 2);
    std::array<std::string, 2> result{};
    size_t slot = 0;
    while (!values.empty()) {
        if (slot == result.size()) throw std::runtime_error("equipment_slots_unsupported");
        const auto separator = values.find(L',');
        auto id = values.substr(0, separator);
        const auto first = id.find_first_not_of(L" \t\"");
        if (first != id.npos) {
            id = id.substr(first, id.find_last_not_of(L" \t\"") - first + 1);
            if (id.find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_") != id.npos)
                throw std::runtime_error("equipment_id_unsupported");
            if (id != L"None") result[slot] = utf8(id);
        }
        ++slot;
        if (separator == values.npos) break;
        values.remove_prefix(separator + 1);
    }
    return result;
}
const Json& inventory(const Json& save) {
    const auto& inv = save.at("Inventory");
    if (!inv.is_object() || !inv.at("MainWeaponInfos").is_object() || !inv.at("ShaChiWeaponInfo").is_object())
        throw std::runtime_error("inventory_structure_unsupported");
    return inv;
}
const char* records(bool main) { return main ? "MainWeaponInfos" : "ShaChiWeaponInfo"; }
const char* level_field(bool main) { return main ? "WeaponLevel" : "ShaChiWeaponLevel"; }
}
std::vector<OwnedWeapon> owned() {
    std::vector<OwnedWeapon> result;
    for (const auto& item : resources::equipment) {
        if (!weapon_value<bool>(L"HasWeapon", item.id, item.main)) continue;
        const auto level = weapon_value(L"GetWeaponLevel", item.id, item.main);
        const auto maximum = weapon_value(L"GetWeaponMaxLevel", item.id, item.main);
        if (level < 0 || maximum < std::max(1, level)) throw std::runtime_error("invalid_weapon_level");
        result.push_back({{item.id, level}, item.main,
            weapon_value<bool>(L"IsWeaponSnapSouled", item.id, item.main), maximum, level == 0 ? 0 : 1});
    }
    return result;
}
EntryConfig capture() {
    auto* bag = UObjectGlobals::FindFirstOf(L"PBZBackPackSubsystem");
    if (!bag) throw std::runtime_error("backpack_query_missing");
    EntryConfig result;
    for (size_t group = 0; group < 2; ++group) {
        const bool main = group == 0;
        const auto ids = equipped_ids(bag, main);
        for (size_t i = 0; i < ids.size(); ++i) {
            const auto& id = ids[i];
            if (id.empty()) continue;
            if (!weapon_value<bool>(L"HasWeapon", id, main)) throw std::runtime_error("equipped_item_missing");
            const auto level = weapon_value(L"GetWeaponLevel", id, main);
            if (level < 0) throw std::runtime_error("invalid_weapon_level");
            result.weapons[group * 2 + i] = {id, level};
        }
    }
    result.active_main = Call(bag, L"GetCurrentMainWeaponIndex").invoke().result<int32_t>();
    result.active_phantom = Call(bag, L"GetCurrentShaqiWeaponIndex").invoke().result<int32_t>();
    return result;
}
std::optional<EntryConfig> stage_equipment(const Session& session) {
    if (!session.baseline_ready || session.busy() || session.scene_preview) return std::nullopt;
    const auto boss = resources::bosses.at(session.pending.boss).id;
    const auto binding = std::ranges::find(resources::stage_bindings, std::string_view(boss), &resources::StageBinding::boss_id);
    if (binding == resources::stage_bindings.end()) return std::nullopt;
    auto config = session.player_loadout;
    config.weapons = {};
    const auto& stage = resources::stages.at(binding->stage);
    for (size_t i = 0; i < 4; ++i) {
        const auto& w = stage.weapons[i];
        if (!w.id[0]) continue;
        const auto* item = session.owned(w.id, i < 2);
        if (!item || w.level < item->min_level || w.level > item->max_level) return std::nullopt;
        config.weapons[i] = {w.id, w.level};
    }
    config.active_main = config.active_phantom = 0;
    config.difficulty = 2; config.cycle = 0;
    config.copied_stage_equipment = true;
    return config;
}
void apply(Json& save, const Json& baseline, const EntryConfig& config) {
    if (config.mode != LoadoutMode::Custom) throw std::runtime_error("loadout_mode_unavailable");
    if (config.difficulty < -1 || config.difficulty > 4 || config.cycle < -1 || config.cycle > 6
        || config.boss_health_percent < 25 || config.boss_health_percent > 1000
        || config.boss_attack_percent < 0 || config.boss_attack_percent > 500)
        throw std::runtime_error("invalid_boss_config");
    if (config.difficulty >= 0) save.at("PBZGameSystem")["DifficultModeByte"] = config.difficulty;
    if (config.cycle >= 0) save.at("PBZGameSystem")["CycleCount"] = config.cycle;
    const auto& original = inventory(baseline);
    inventory(save);
    auto& inv = save["Inventory"];
    for (size_t group = 0; group < 2; ++group) {
        const bool main = group == 0;
        auto ids = Json::array();
        std::set<std::string> used;
        for (size_t i = 0; i < 2; ++i) {
            const auto& slot = config.weapons[group * 2 + i];
            if (slot.item_id.empty()) {
                if (i == 0 && (main || !config.weapons[group * 2 + 1].item_id.empty())) throw std::runtime_error("empty_primary_slot");
                continue;
            }
            if (!used.insert(slot.item_id).second || !original.at(records(main)).contains(slot.item_id))
                throw std::runtime_error("weapon_not_owned_or_duplicate");
            const auto maximum = weapon_value(L"GetWeaponMaxLevel", slot.item_id, main);
            // GetWeaponLevel returns the stored value without normalizing zero.
            // Preserve an original zero, but do not invent it for upgraded items.
            const auto minimum = original.at(records(main)).at(slot.item_id).value(level_field(main), 0) == 0 ? 0 : 1;
            if (slot.level < minimum || slot.level > maximum) throw std::runtime_error("invalid_weapon_level");
            if (!inv.at(records(main)).contains(slot.item_id)) throw std::runtime_error("weapon_record_missing");
            auto& item = inv[records(main)][slot.item_id];
            // Keep acquired-cycle, reincarnation, soul and all other item metadata.
            // In particular, a health edit never writes ability nodes.
            if (slot.level != 0 || item.value(level_field(main), 0) != 0)
                item[level_field(main)] = slot.level;
            ids.push_back(slot.item_id);
        }
        inv[main ? "MainWeapons" : "ShaqiWeapons"] = ids;
        const auto requested = main ? config.active_main : config.active_phantom;
        const auto active = ids.empty() ? 0 : std::clamp(requested, 0, static_cast<int>(ids.size()) - 1);
        inv[main ? "CurrentWeaponIndex" : "CurrentShaqiWeaponIndex"] = active;
        if (main) inv["CurrentMainWeaponID"] = ids.at(active);
    }
}
void verify(const EntryConfig& config) {
    const auto actual = capture();
    if (actual.weapons != config.weapons) throw std::runtime_error("equipment_apply_mismatch");
}
}
