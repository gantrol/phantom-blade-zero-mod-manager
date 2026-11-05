#include "Reflection.hpp"
#include <Windows.h>
#include <malloc.h>
#include <unordered_map>
#include <chrono>

namespace arena::ue
{
namespace {
// Script classes and their default objects are stable across level travel.
// Keep weak references so a class reload cannot leave a stale pointer here.
std::unordered_map<std::wstring, FWeakObjectPtr> script_objects;
bool cache_lookups = true;
#if ARENA_DIAGNOSTICS
uint64_t lookup_calls{}, lookup_hits{}, lookup_ns{};
#endif
// The two Serialize slots match the installed UE OutputDevice interface.
// ImportText reports malformed enum/struct input through this device even when
// it returns null; passing a null device turns that report into an access fault.
class ImportErrors {
    uint32_t flags_{};
public:
    std::wstring message;
    virtual ~ImportErrors() = default;
    virtual void Serialize(const wchar_t* value, uint8_t, const void*) { message = value; }
    virtual void Serialize(const wchar_t* value, uint8_t verbosity, const void* category, double) {
        Serialize(value, verbosity, category);
    }
};
}
std::wstring wide(std::string_view s)
{
    const int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), int(s.size()), nullptr, 0);
    std::wstring out(n, L'\0');
    if (n) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), int(s.size()), out.data(), n);
    return out;
}
std::string utf8(std::wstring_view s)
{
    const int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), int(s.size()), nullptr, 0, nullptr, nullptr);
    std::string out(n, '\0');
    if (n) WideCharToMultiByte(CP_UTF8, 0, s.data(), int(s.size()), out.data(), n, nullptr, nullptr);
    return out;
}
std::wstring quote(std::wstring_view s)
{
    std::wstring out = L"\"";
    for (auto c : s) { if (c == L'\\' || c == L'"') out += L'\\'; out += c; }
    return out + L"\"";
}
UObject* find(const wchar_t* path)
{
    const bool script = std::wstring_view(path).starts_with(L"/Script/");
    if (script && cache_lookups) {
        const auto it = script_objects.find(path);
        if (it != script_objects.end()) if (auto* object = it->second.Get()) {
#if ARENA_DIAGNOSTICS
            ++lookup_hits;
#endif
            return object;
        }
    }
#if ARENA_DIAGNOSTICS
    const auto start = std::chrono::steady_clock::now();
#endif
    auto* object = UObjectGlobals::StaticFindObject_InternalSlow(nullptr, nullptr, path, false);
#if ARENA_DIAGNOSTICS
    ++lookup_calls;
    lookup_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count();
#endif
    if (script && object && cache_lookups) script_objects[path] = object;
    return object;
}
#if ARENA_DIAGNOSTICS
void lookup_cache(bool enabled) { cache_lookups = enabled; lookup_profile(true); }
std::string lookup_profile(bool reset) {
    const auto result = "lookup_calls=" + std::to_string(lookup_calls) + " cache_hits=" + std::to_string(lookup_hits)
        + " lookup_ms=" + std::to_string(double(lookup_ns) / 1e6);
    if (reset) lookup_calls = lookup_hits = lookup_ns = 0;
    return result;
}
#endif
UObject* library(const wchar_t* module, const wchar_t* name)
{
    const auto path = std::wstring(L"/Script/") + module + L".Default__" + name;
    auto* result = find(path.c_str());
    if (!result) throw std::runtime_error("library_missing:" + utf8(path));
    return result;
}
void Field::text(std::wstring_view value) const
{
    const std::wstring text(value);
    ImportErrors errors;
    const auto* end = property->ImportText_Direct(text.c_str(), data, nullptr, 0,
        reinterpret_cast<FOutputDevice*>(&errors));
    if (!end || !errors.message.empty())
        throw std::runtime_error("property_import_failed:" + utf8(property->GetName()) + ":" + utf8(errors.message.substr(0,512)));
}
std::wstring Field::text() const
{
    FString out;
    property->ExportTextItem_Direct(out, data, nullptr, nullptr, 0, nullptr);
    return out.str();
}
void Field::copy(Field source) const
{
    if (property->GetElementSize() != source.property->GetElementSize())
        throw std::runtime_error("property_copy_size_mismatch");
    property->CopyCompleteValue(data, source.data);
}
Field field(UObject* obj, const wchar_t* name)
{
    if (!obj) throw std::runtime_error("object_missing");
    auto* p = obj->GetPropertyByNameInChain(name);
    if (!p) throw std::runtime_error("property_missing:" + utf8(name));
    return {p, static_cast<std::byte*>(static_cast<void*>(obj)) + p->GetOffset_Internal()};
}
Field field(UStruct* type, void* data, const wchar_t* name)
{
    if (!type) throw std::runtime_error("struct_missing");
    FProperty* p = nullptr;
    for (auto* s = type; s && !p; s = s->GetSuperStruct())
        for (auto* f = s->GetChildProperties(); f; f = f->next())
            if (f->GetName() == name) { p = static_cast<FProperty*>(f); break; }
    if (!p) throw std::runtime_error("parameter_missing:" + utf8(name));
    return {p, static_cast<std::byte*>(data) + p->GetOffset_Internal()};
}
Frame::Frame(UStruct* type) : type_(type)
{
    if (!type) throw std::runtime_error("function_or_struct_missing");
    data_ = _aligned_malloc(std::max(type->GetPropertiesSize(), 16), 16);
    if (!data_) throw std::bad_alloc();
    std::memset(data_, 0, std::max(type->GetPropertiesSize(), 16));
    type->InitializeStruct(data_, 1);
}
Frame::~Frame() { type_->DestroyStruct(data_, 1); _aligned_free(data_); }
namespace
{
UFunction* require_function(UObject* object, const wchar_t* name)
{
    auto* f = object ? object->GetFunctionByNameInChain(name) : nullptr;
    if (!f) throw std::runtime_error("function_missing:" + utf8(name));
    return f;
}
}
Call::Call(UObject* obj, const wchar_t* name) : object_(obj),
    function_(require_function(obj, name)), frame_(function_) {}
Call& Call::invoke() { object_->ProcessEvent(function_, frame_.data()); return *this; }
UObject* object(UObject* obj, const wchar_t* property) { return field(obj, property).get<UObject*>(); }
UObject* load_object(std::wstring_view path)
{
    Call load(library(L"Engine", L"KismetSystemLibrary"), L"LoadAsset_Blocking");
    load[L"Asset"].text(path); load.invoke(); return load.result<UObject*>();
}
UObject* load_class(std::wstring_view path)
{
    if (auto* result = find(std::wstring(path).c_str())) return result;
    Call load(library(L"Engine", L"KismetSystemLibrary"), L"LoadClassAsset_Blocking");
    load[L"AssetClass"].text(path); load.invoke(); return load.result<UObject*>();
}
bool valid(UObject* obj)
{
    if (!obj) return false;
    Call c(library(L"Engine", L"KismetSystemLibrary"), L"IsValid");
    c[L"Object"].set(obj); return c.invoke().result<bool>();
}
void invoke(UObject* obj, const wchar_t* name) { Call(obj, name).invoke(); }
void visible(UObject* obj, bool show)
{
    Call c(obj, L"SetVisibility"); c[L"InVisibility"].text(show ? L"Visible" : L"Collapsed"); c.invoke();
}
void enabled(UObject* obj, bool enable)
{
    Call c(obj, L"SetIsEnabled"); c[L"bInIsEnabled"].text(enable ? L"True" : L"False"); c.invoke();
}
void set_text(UObject* obj, std::wstring_view text)
{
    Call convert(library(L"Engine", L"KismetTextLibrary"), L"Conv_StringToText");
    convert[L"InString"].text(text); convert.invoke();
    Call c(obj, L"SetText"); c[L"InText"].copy(convert[L"ReturnValue"]); c.invoke();
}
}
