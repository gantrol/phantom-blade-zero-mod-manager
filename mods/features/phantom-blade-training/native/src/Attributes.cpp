#include "Attributes.hpp"
#include <array>
#include <cmath>

namespace arena {
using namespace ue;
namespace {
constexpr std::array<const wchar_t*,5> resources{L"Health", L"Shachi", L"UltimateCharge", L"UltimateChargeCount", L"SpiritShadow"};
void attribute(Field output, const wchar_t* name) {
    Call make(library(L"ProjectPBZ", L"PBZBlueprintFunctionLibrary"), L"MakeAttribute");
    make[L"AttributeName"].text(name);
    make[L"Set"].set(find(L"/Script/CharacterBasics.BasicAttributeSet"));
    make.invoke(); output.copy(make[L"ReturnValue"]);
}
std::wstring apply_override(UObject* player, const wchar_t* path, float value) {
    auto* asc = object(player, L"AbilitySystemComponent");
    auto* effect = load_class(path);
    if (!effect) throw std::runtime_error("health_effect_missing");
    Call context(asc, L"MakeEffectContext"); context.invoke();
    Call spec(asc, L"MakeOutgoingSpec");
    spec[L"GameplayEffectClass"].set(effect); spec[L"Level"].set<float>(1);
    spec[L"Context"].copy(context[L"ReturnValue"]); spec.invoke();
    Call magnitude(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"AssignSetByCallerMagnitude");
    magnitude[L"SpecHandle"].copy(spec[L"ReturnValue"]); magnitude[L"DataName"].text(L"Arena.Health");
    magnitude[L"Magnitude"].set(value); magnitude.invoke();
    Call apply(asc, L"BP_ApplyGameplayEffectSpecToSelf");
    apply[L"SpecHandle"].copy(magnitude[L"ReturnValue"]); apply.invoke();
    auto handle = apply[L"ReturnValue"].text();
    if (handle.find(L"-1") != std::wstring::npos) throw std::runtime_error("health_effect_rejected");
    return handle;
}
}
float Attributes::read(UObject* player, const wchar_t* name) {
    Call get(library(L"SGameAttributes", L"AttributeUtils"), L"GetCharacterAttributeValue");
    get[L"Character"].set(player); attribute(get[L"Attribute"], name);
    return get.invoke().result<float>();
}
void Attributes::write(UObject* player, const wchar_t* name, float value) {
    Call set(library(L"SGameAttributes", L"AttributeUtils"), L"ModifyCharacterAttributeValue");
    set[L"Character"].set(player); attribute(set[L"Attribute"], name);
    set[L"ModiferOp"].text(L"Override"); set[L"Value"].set(value); set.invoke();
}
void Attributes::refill(UObject* player) {
    for (const auto* resource : resources) write(player, resource, read(player, (std::wstring(resource)+L"_Max").c_str()));
}
void Attributes::limit_health(UObject* player, float maximum) {
    // Override the final GAS value, including equipment and NG+ modifiers.
    // This transient effect belongs to the practice pawn and dies on level change.
    apply_override(player, L"/Game/PhantomBladeArena/GE_MaxHealth.GE_MaxHealth_C", maximum);
    if (std::abs(read(player, L"Health_Max") - maximum) > 0.1f)
        throw std::runtime_error("health_limit_not_applied");
}
void Attributes::tune_boss(UObject* boss, float health, float attack) {
    if (health == 1.f && attack == 1.f) return;
    Call ability_system(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"GetAbilitySystemComponent");
    ability_system[L"Actor"].set(boss);
    auto* asc = ability_system.invoke().result<UObject*>();
    if (!asc) throw std::runtime_error("boss_ability_system_missing");
    auto* effect = load_class(L"/Game/PhantomBladeArena/GE_BossTuning.GE_BossTuning_C");
    if (!effect) throw std::runtime_error("boss_effect_missing");
    Call context(asc, L"MakeEffectContext"); context.invoke();
    Call spec(asc, L"MakeOutgoingSpec");
    spec[L"GameplayEffectClass"].set(effect); spec[L"Level"].set<float>(1);
    spec[L"Context"].copy(context[L"ReturnValue"]); spec.invoke();
    for (auto [name, value] : {std::pair{L"Arena.BossHealth", health}, std::pair{L"Arena.BossAttack", attack}}) {
        Call magnitude(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"AssignSetByCallerMagnitude");
        magnitude[L"SpecHandle"].copy(spec[L"ReturnValue"]);
        magnitude[L"DataName"].text(name); magnitude[L"Magnitude"].set(value); magnitude.invoke();
        spec[L"ReturnValue"].copy(magnitude[L"ReturnValue"]);
    }
    Call apply(asc, L"BP_ApplyGameplayEffectSpecToSelf");
    apply[L"SpecHandle"].copy(spec[L"ReturnValue"]); apply.invoke();
    if (apply[L"ReturnValue"].text().find(L"-1") != std::wstring::npos)
        throw std::runtime_error("boss_effect_rejected");
    const auto maximum = read(boss, L"Health_Max");
    if (!std::isfinite(maximum) || maximum <= 0) throw std::runtime_error("boss_attributes_invalid");
    write(boss, L"Health", maximum);
    RC::Output::send(L"[PhantomBladeArena] Boss tuning: HP=" + std::to_wstring(maximum)
        + L" Attack=" + std::to_wstring(read(boss, L"Attack"))
        + L" ShachiAttack=" + std::to_wstring(read(boss, L"ShachiAttack")) + L"\n");
}
std::map<std::wstring,float> Attributes::capture(UObject* player) {
    std::map<std::wstring,float> result;
    for (auto* resource : resources) result[resource] = read(player, resource);
    return result;
}
void Attributes::restore(UObject* player, const std::map<std::wstring,float>& values) {
    for (const auto& [name,value] : values) write(player, name.c_str(), value);
}
void Attributes::lock(UObject* player, bool enabled) {
    if (locked() && (!enabled || owner_.Get() != player)) {
        auto* old = owner_.Get();
        // Instant damage can change the base while an override holds current HP.
        // Restore that base before removing the override; never replay damage.
        write(old, L"Health", held_health_);
        Call remove(object(old, L"AbilitySystemComponent"), L"RemoveActiveGameplayEffect");
        remove[L"Handle"].text(effect_handle_); remove[L"StacksToRemove"].set<int32_t>(-1); remove.invoke();
        owner_.Reset(); effect_handle_.clear();
    }
    if (!enabled || locked()) return;
    owner_.Reset(); effect_handle_.clear();
    const auto health = read(player, L"Health");
    if (health <= 0) return;
    effect_handle_ = apply_override(player, L"/Game/PhantomBladeArena/GE_HealthLock.GE_HealthLock_C", health);
    held_health_ = health; owner_ = player;
    RC::Output::send(L"[PhantomBladeArena] Health override applied at " + std::to_wstring(health) + L" " + effect_handle_ + L"\n");
}
}
