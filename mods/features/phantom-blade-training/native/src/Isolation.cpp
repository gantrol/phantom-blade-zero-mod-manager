#include "Isolation.hpp"
#include <MinHook.h>
#include <Windows.h>
#include <atomic>
#include <fstream>
#include <chrono>
#include <algorithm>

namespace arena {
using namespace ue;
namespace {
std::atomic<bool> isolated{};
std::atomic<bool> preparing{};
std::atomic<uint32_t> writes_in_flight{};
std::atomic<uint64_t> deferred_writes{};
thread_local int save_route = -1;
std::atomic<uint64_t> routed{}, blocked_achievements{};
std::filesystem::path arena_directory;
using PathFunction = FString* (*)(void*, FString*, const wchar_t*);
PathFunction original_path{};
using SaveFunction = bool (*)(void*, bool, const wchar_t*, int32_t, const void*);
SaveFunction original_save{};
using UnlockFunction = void (*)(UObject*, const FString&, float);
using ProgressFunction = void (*)(UObject*, const FString&, int, int);
UnlockFunction original_unlock{};
ProgressFunction original_progress{};
uintptr_t base{};
bool initialized_hooks{};
void* save_system{};
std::vector<char> original_bytes;

void checked_hook(uintptr_t rva, std::initializer_list<uint8_t> signature, void* replacement, void** original) {
    auto* address = reinterpret_cast<void*>(base + rva);
    if (std::memcmp(address, signature.begin(), signature.size()) != 0)
        throw std::runtime_error("unsupported_game_build");
    auto result = MH_CreateHook(address, replacement, original);
    if (result != MH_OK) throw std::runtime_error(std::string("native_hook:") + MH_StatusToString(result));
    if (MH_QueueEnableHook(address) != MH_OK) throw std::runtime_error("native_hook_enable");
}
FString* redirected_path(void* self, FString* output, const wchar_t* name) {
    original_path(self, output, name);
    const bool route = save_route >= 0 ? save_route != 0 : isolated.load(std::memory_order_acquire);
    if (route) {
        // The platform's temporary and recovery paths derive from this path.
        // Its own atomic-write/flush/rename implementation remains in charge.
        auto result = std::filesystem::path(output->str());
        auto lower = result.wstring();
        std::transform(lower.begin(), lower.end(), lower.begin(), towlower);
        if (lower.find(L"savegames") != std::wstring::npos) {
            const auto redirected = arena_directory / result.filename();
            output->~FString(); new (output) FString(redirected.c_str());
            ++routed;
        }
    }
    return output;
}
bool save_game(void* self, bool ui, const wchar_t* name, int32_t user, const void* data) {
    ++writes_in_flight;
    struct Finish {
        int previous{save_route};
        ~Finish() { save_route = previous; --writes_in_flight; writes_in_flight.notify_all(); }
    } finish;
    // Close the entry boundary before copying. Requests already inside native
    // I/O finish with their original destination; new requests receive a real
    // failure until the independent copy is ready, rather than fake success.
    if (preparing) { ++deferred_writes; return false; }
    save_route = isolated ? 1 : 0;
    return original_save(self, ui, name, user, data);
}
void unlock(UObject* object, const FString& name, float percent) {
    if (isolated) { ++blocked_achievements; return; }
    original_unlock(object, name, percent);
}
void progress(UObject* object, const FString& name, int current, int maximum) {
    if (isolated) { ++blocked_achievements; return; }
    original_progress(object, name, current, maximum);
}
std::vector<char> bytes(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) throw std::runtime_error("save_read_failed");
    return {std::istreambuf_iterator<char>(stream), {}};
}
}
void Isolation::initialize() {
    if (initialized_hooks) return;
    const auto image = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    auto* pe = reinterpret_cast<IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    uintptr_t platform_rva{}, path_rva{}, save_rva{}, unlock_rva{}, progress_rva{};
    if (pe->FileHeader.TimeDateStamp == 0x6ae7ae3f && pe->OptionalHeader.SizeOfImage == 0x1e0bb000) {
        platform_rva = 0x3f80930;
        path_rva = 0x484b070;
        save_rva = 0x43a0f40;
        unlock_rva = 0x6460f70;
        progress_rva = 0x6460ac0;
    } else if (pe->FileHeader.TimeDateStamp == 0x6ae19d7a && pe->OptionalHeader.SizeOfImage == 0x1ec54000) {
        platform_rva = 0x3f6d3a0;
        path_rva = 0x4820da0;
        save_rva = 0x4380f90;
        unlock_rva = 0x6419060;
        progress_rva = 0x6418bb0;
    } else {
        throw std::runtime_error("unsupported_game_build");
    }
    base = image;
    auto* platform = reinterpret_cast<void* (*)()>(base + platform_rva)();
    auto** platform_table = *reinterpret_cast<void***>(platform);
    save_system = reinterpret_cast<void* (*)(void*)>(platform_table[8])(platform);
    auto** table = *reinterpret_cast<void***>(save_system);
    if (table[15] != reinterpret_cast<void*>(base + path_rva)) throw std::runtime_error("save_platform_mismatch");
    const auto initialized = MH_Initialize();
    if (initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) throw std::runtime_error("native_hooks_init");
    checked_hook(path_rva, {0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x18,0x55,0x57,0x41,0x54},
        reinterpret_cast<void*>(&redirected_path), reinterpret_cast<void**>(&original_path));
    checked_hook(save_rva, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x7c,0x24,0x18,0x55,0x48,0x8b,0xec},
        reinterpret_cast<void*>(&save_game), reinterpret_cast<void**>(&original_save));
    checked_hook(unlock_rva, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x57,0x48,0x83,0xec,0x70},
        reinterpret_cast<void*>(&unlock), reinterpret_cast<void**>(&original_unlock));
    checked_hook(progress_rva, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x56,0x41,0x56,0x41,0x57},
        reinterpret_cast<void*>(&progress), reinterpret_cast<void**>(&original_progress));
    if (MH_ApplyQueued() != MH_OK) throw std::runtime_error("native_hooks_enable");
    initialized_hooks = true;
    RC::Output::send(L"[PhantomBladeArena] Save-path and achievement hooks ready.\n");
}
void Isolation::prepare() {
    initialize();
    if (isolated || copy_.valid()) throw std::runtime_error("isolation_busy");
    ready_ = false;
    FString original;
    original_path(save_system, &original, L"PBZSaveGame");
    original_ = std::filesystem::absolute(original.str());
    wchar_t local[MAX_PATH]{};
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH)) throw std::runtime_error("local_app_data_missing");
    const auto id = std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    arena_directory = std::filesystem::path(local) / L"PBZArena" / L"Sessions" / id;
    sandbox_ = arena_directory / original_.filename();
    const auto original_path_copy = original_, sandbox_path_copy = sandbox_;
    preparing = true;
    // Disk reads and copies run on a worker. The paused game thread only polls.
    copy_ = std::async(std::launch::async, [original_path_copy, sandbox_path_copy] {
        for (auto count = writes_in_flight.load(); count; count = writes_in_flight.load())
            writes_in_flight.wait(count);
        original_bytes = bytes(original_path_copy);
        std::filesystem::create_directories(sandbox_path_copy.parent_path());
        for (const auto& file : std::filesystem::directory_iterator(original_path_copy.parent_path())) {
            if (!file.is_regular_file() || file.path().extension() != L".sav") continue;
            std::filesystem::copy_file(file.path(), sandbox_path_copy.parent_path() / file.path().filename());
        }
        if (bytes(sandbox_path_copy) != original_bytes) throw std::runtime_error("save_copy_verification_failed");
        std::filesystem::copy_file(sandbox_path_copy, sandbox_path_copy.parent_path() / L"entry-baseline.sav");
    });
}
bool Isolation::prepared() {
    if (ready_) return true;
    if (!copy_.valid()) return false;
    if (copy_.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return false;
    try { copy_.get(); }
    catch (...) { preparing = false; throw; }
    ready_ = true; return true;
}
void Isolation::activate() {
    if (!ready_ || sandbox_.empty()) throw std::runtime_error("isolation_not_prepared");
    isolated.store(true, std::memory_order_release);
    preparing = false;
    RC::Output::send(L"[PhantomBladeArena] Practice save routing enabled: " + sandbox_.wstring() + L"\n");
}
bool Isolation::idle(UObject* save) const {
    if (!base || !save) return false;
    const auto* data = reinterpret_cast<const uint8_t*>(save);
    // Build 26266538: the serialization and disk-write phases share Busy.
    // Deferred/forced writes must also have drained before changing destinations.
    return !data[0x279] && !data[0x278] && !data[0x27a] && !data[0x298] && !writes_in_flight;
}
void Isolation::verify_original() {
    const auto original = original_;
    ready_ = false;
    copy_ = std::async(std::launch::async, [original] {
        if (bytes(original) != original_bytes) throw std::runtime_error("main_save_changed_during_practice");
    });
}
void Isolation::restore_account(UObject* save) {
    if (!idle(save) || !ready_) throw std::runtime_error("save_still_pending");
    isolated.store(false, std::memory_order_release);
    try { invoke(save, L"LoadAdvancedSaveGame"); }
    catch (...) { isolated = true; throw; }
    RC::Output::send(L"[PhantomBladeArena] Original account save reloaded.\n");
}
bool Isolation::active() const { return isolated; }
std::wstring Isolation::status() const {
    return L"isolated=" + std::to_wstring(isolated.load()) + L" routed=" + std::to_wstring(routed.load())
        + L" achievements_blocked=" + std::to_wstring(blocked_achievements.load())
        + L" preparing=" + std::to_wstring(preparing.load()) + L" entry_deferred=" + std::to_wstring(deferred_writes.load());
}
}
