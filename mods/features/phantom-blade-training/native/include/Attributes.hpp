#pragma once
#include "Reflection.hpp"
#include <map>
namespace arena {
class Attributes {
    ue::FWeakObjectPtr owner_;
    std::wstring effect_handle_;
    float held_health_{};
public:
    static float read(ue::UObject* player, const wchar_t* name);
    static void write(ue::UObject* player, const wchar_t* name, float value);
    static void refill(ue::UObject* player);
    static std::map<std::wstring,float> capture(ue::UObject* player);
    static void restore(ue::UObject* player, const std::map<std::wstring,float>&);
    void lock(ue::UObject* player, bool enabled);
    bool locked() const { return !effect_handle_.empty() && owner_.Get(); }
};
}
