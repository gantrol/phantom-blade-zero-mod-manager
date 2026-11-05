#include "Attributes.hpp"
#include <array>

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
    auto* asc = object(player, L"AbilitySystemComponent");
    auto* effect = load_class(L"/Game/PhantomBladeArena/GE_HealthLock.GE_HealthLock_C");
    if (!effect) throw std::runtime_error("health_effect_missing");
    Call context(asc, L"MakeEffectContext"); context.invoke();
    Call spec(asc, L"MakeOutgoingSpec");
    spec[L"GameplayEffectClass"].set(effect); spec[L"Level"].set<float>(1);
    spec[L"Context"].copy(context[L"ReturnValue"]); spec.invoke();
    Call magnitude(library(L"GameplayAbilities", L"AbilitySystemBlueprintLibrary"), L"AssignSetByCallerMagnitude");
    magnitude[L"SpecHandle"].copy(spec[L"ReturnValue"]); magnitude[L"DataName"].text(L"Arena.Health");
    magnitude[L"Magnitude"].set(health); magnitude.invoke();
    Call apply(asc, L"BP_ApplyGameplayEffectSpecToSelf");
    apply[L"SpecHandle"].copy(magnitude[L"ReturnValue"]); apply.invoke();
    effect_handle_ = apply[L"ReturnValue"].text();
    if (effect_handle_.find(L"-1") != std::wstring::npos) throw std::runtime_error("health_effect_rejected");
    held_health_ = health; owner_ = player;
    RC::Output::send(L"[PhantomBladeArena] Health override applied at " + std::to_wstring(health) + L" " + effect_handle_ + L"\n");
}
}
