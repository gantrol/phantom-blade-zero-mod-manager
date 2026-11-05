#pragma once
#include "UnrealApi.hpp"
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string_view>

namespace arena::ue
{
using namespace RC::Unreal;
std::wstring wide(std::string_view);
std::string utf8(std::wstring_view);
std::wstring quote(std::wstring_view);
UObject* find(const wchar_t* path);
UObject* library(const wchar_t* module, const wchar_t* name);
#if ARENA_DIAGNOSTICS
void lookup_cache(bool enabled);
std::string lookup_profile(bool reset = false);
#endif

struct Field
{
    FProperty* property;
    void* data;
    void text(std::wstring_view value) const;
    std::wstring text() const;
    void copy(Field source) const;
    template<class T> T get() const
    {
        if (sizeof(T) != static_cast<size_t>(property->GetElementSize()))
            throw std::runtime_error("property_size_mismatch");
        T result; std::memcpy(&result, data, sizeof(T)); return result;
    }
    template<class T> void set(T value) const
    {
        if (sizeof(T) != static_cast<size_t>(property->GetElementSize()))
            throw std::runtime_error("property_size_mismatch");
        std::memcpy(data, &value, sizeof(T));
    }
};
Field field(UObject*, const wchar_t*);
Field field(UStruct*, void*, const wchar_t*);

class Frame
{
    UStruct* type_{};
    void* data_{};
public:
    explicit Frame(UStruct* type);
    ~Frame();
    Frame(const Frame&) = delete;
    Frame& operator=(const Frame&) = delete;
    Field operator[](const wchar_t* name) const { return field(type_, data_, name); }
    void* data() const { return data_; }
};

class Call
{
    UObject* object_;
    UFunction* function_;
    Frame frame_;
public:
    Call(UObject*, const wchar_t*);
    Field operator[](const wchar_t* name) const { return frame_[name]; }
    Call& invoke();
    template<class T> T result() const { return frame_[L"ReturnValue"].get<T>(); }
};

UObject* object(UObject*, const wchar_t* property);
UObject* load_class(std::wstring_view path);
UObject* load_object(std::wstring_view path);
bool valid(UObject*);
void invoke(UObject*, const wchar_t*);
void visible(UObject*, bool);
void enabled(UObject*, bool);
void set_text(UObject*, std::wstring_view);
}
