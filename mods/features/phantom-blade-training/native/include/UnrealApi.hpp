#pragma once
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

// Pointer-only ABI facade for UE4SS cdbc0b66. These declarations are matched
// against the pinned DLL's decorated exports during linking. Engine class
// layouts are never reconstructed here; properties are accessed by reflection.
#define UEAPI __declspec(dllimport)
namespace RC::Output { UEAPI void send(std::wstring_view); }
namespace RC::Unreal
{
class UObject; class UClass; class UFunction; class FProperty; class FOutputDevice;
class UEngine;
class FString
{
public:
    wchar_t* data{};
    int32_t num{}, capacity{};
    UEAPI FString();
    UEAPI explicit FString(const wchar_t*);
    UEAPI ~FString();
    FString(const FString&) = delete;
    FString& operator=(const FString&) = delete;
    std::wstring str() const { return data && num > 0 ? std::wstring(data, num - 1) : std::wstring{}; }
};
static_assert(sizeof(FString) == 16);
class UObjectBase
{
public:
    UEAPI UClass*& GetClassPrivate();
    UEAPI UObject*& GetOuterPrivate();
    UEAPI bool IsA(UClass*) const;
};
class UObject : public UObjectBase
{
public:
    UEAPI std::wstring GetName() const;
    UEAPI std::wstring GetFullName(UObject* stop_outer = nullptr) const;
    UEAPI FProperty* GetPropertyByNameInChain(const wchar_t*);
    UEAPI UFunction* GetFunctionByNameInChain(const wchar_t*);
    UEAPI void ProcessEvent(UFunction*, void*);
};
struct FWeakObjectPtr
{
    int32_t index{}, serial{};
    UEAPI FWeakObjectPtr();
    UEAPI explicit FWeakObjectPtr(const UObject*);
    UEAPI void operator=(UObject*);
    UEAPI UObject* Get() const;
    UEAPI void Reset();
};
static_assert(sizeof(FWeakObjectPtr) == 8);
class FField
{
private:
    UEAPI FField*& GetNext();
public:
    UEAPI std::wstring GetName() const;
    FField* next() { return GetNext(); }
};
class FProperty : public FField
{
public:
    UEAPI int32_t& GetOffset_Internal();
    UEAPI int32_t& GetElementSize();
    UEAPI int32_t GetSize() const;
    UEAPI FProperty*& GetPropertyLinkNext();
    UEAPI void CopyCompleteValue(void*, const void*) const;
    UEAPI const wchar_t* ImportText_Direct(const wchar_t*, void*, UObject*, int32_t, FOutputDevice*) const;
    UEAPI void ExportTextItem_Direct(FString&, const void*, const void*, UObject*, int32_t, UObject*) const;
};
class UStruct : public UObject
{
public:
    UEAPI FField*& GetChildProperties();
    UEAPI UStruct*& GetSuperStruct();
    UEAPI int32_t& GetPropertiesSize();
    UEAPI void InitializeStruct(void*, int32_t) const;
    UEAPI void DestroyStruct(void*, int32_t) const;
};
class UClass : public UStruct {};
class UFunction : public UStruct {};
namespace UObjectGlobals
{
    UEAPI UObject* StaticFindObject_InternalSlow(UClass*, UObject*, const wchar_t*, bool);
    UEAPI UObject* FindFirstOf(const wchar_t*);
    UEAPI void FindAllOf(const wchar_t*, std::vector<UObject*>&);
}
namespace Hook
{
    // Legacy callbacks have no deregistration token. The mod pins its image,
    // and all callbacks consult one disarmed-on-uninstall runtime owner.
    UEAPI void RegisterProcessEventPostCallback(std::function<void(UObject*, UFunction*, void*)>);
    UEAPI void RegisterEngineTickPostCallback(std::function<void(UEngine*, float)>);
}
}
