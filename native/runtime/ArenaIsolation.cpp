#include "ArenaIsolation.hpp"
#include <windows.h>
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <future>
#include <stdexcept>
#include <vector>
#include <utility>

namespace ArenaIsolation {
namespace {
namespace fs = std::filesystem;
struct String { wchar_t* data{}; int32_t size{}, capacity{}; };
struct Bytes { void* data{}; int32_t size{}, capacity{}; };
using PathFn = String*(*)(void*, String*, const wchar_t*);
using SaveFn = bool(*)(void*, bool, const wchar_t*, int32_t, const void*);
using UnlockFn = void(*)(void*, const String&, float);
using ProgressFn = void(*)(void*, const String&, int, int);
using StateFn = void(*)(void*, void*, void*);
using ConstructStringFn = String*(*)(String*, const wchar_t*);
using FreeFn = void(*)(void*);
using ReserveFn = void(*)(uint32_t, uint32_t, int32_t, void*, uint32_t, int32_t*);
using LoadMasterFn = bool(*)(void*, Bytes&, const String&);
using UpdateTagsFn = bool(*)(void*, const uint64_t*, int32_t, bool, uint8_t);
PathFn pathFn{};
SaveFn saveFn{};
UnlockFn unlockFn{};
ProgressFn progressFn{};
StateFn stateFn{};
ConstructStringFn constructString{};
FreeFn freeMemory{};
ReserveFn reserve{};
LoadMasterFn loadMaster{};
UpdateTagsFn updateTags{};
std::atomic<void*> combatCounts{};
uint64_t combatName{};
thread_local bool armCombat{};
void* saveSystem{};
std::atomic<bool> isolated{}, preparing{}, restoring{};
std::atomic<uint32_t> writes{};
thread_local int route = -1;
thread_local std::wstring command;
thread_local std::string result;
bool initialized{}, hadOriginal{}, ready{};
fs::path original, sandbox, directory;
std::vector<char> originalBytes;
std::future<void> worker;
std::future<std::string> templateWorker;
constexpr size_t SaveLimit = 128 * 1024 * 1024;

bool idle(void* save) {
    const auto* p = static_cast<const uint8_t*>(save);
    return p && !p[0x278] && !p[0x279] && !p[0x27a] && !p[0x298] && !writes.load();
}
void require(bool condition, const char* error) { if (!condition) throw std::runtime_error(error); }
std::string errorResponse(const std::exception& error) {
    const std::string message(error.what());
    const bool code = !message.empty() && message.size() < 128 && std::all_of(message.begin(), message.end(),
        [](char c) { return c == '_' || (c >= 'a' && c <= 'z'); });
    return "{\"error\":\"" + (code ? message : std::string("arena_bridge_io_failed")) + "\"}";
}
std::vector<char> read(const fs::path& file) {
    const auto length = fs::file_size(file);
    require(length <= SaveLimit, "save_too_large");
    std::ifstream stream(file, std::ios::binary);
    require(bool(stream), "save_read_failed");
    std::vector<char> data(static_cast<size_t>(length));
    stream.read(data.data(), static_cast<std::streamsize>(length));
    require(bool(stream), "save_read_failed");
    return data;
}
void pollWorker() {
    if (!worker.valid() || worker.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return;
    worker.get(); ready = true;
}
String* redirectedPath(void* self, String* output, const wchar_t* name) {
    pathFn(self, output, name);
    const bool redirected = route >= 0 ? route != 0 : isolated.load() && !restoring.load();
    if (!redirected || !output->data || output->size < 2) return output;
    // Publish the immutable directory before setting isolated. Preserve the
    // platform's own temp/flush/rename implementation and filenames.
    const fs::path current(output->data);
    auto lower = current.wstring();
    std::transform(lower.begin(), lower.end(), lower.begin(), towlower);
    if (lower.find(L"savegames") == std::wstring::npos) return output;
    const auto target = directory / current.filename();
    String replacement{};
    constructString(&replacement, target.c_str());
    freeMemory(output->data);
    *output = replacement;
    return output;
}
bool saveGame(void* self, bool ui, const wchar_t* name, int32_t user, const void* bytes) {
    ++writes;
    struct Finish {
        int previous{route};
        ~Finish() { route = previous; --writes; writes.notify_all(); }
    } finish;
    if (preparing.load()) return false;
    route = isolated.load() ? 1 : 0;
    return saveFn(self, ui, name, user, bytes);
}
void unlock(void* self, const String& name, float amount) {
    if (!isolated.load()) unlockFn(self, name, amount);
}
void progress(void* self, const String& name, int current, int maximum) {
    if (!isolated.load()) progressFn(self, name, current, maximum);
}
bool filterCombat(void* counts, const uint64_t* tag, int32_t delta, bool deferred, uint8_t replication) {
    if (armCombat && isolated && tag) {
        armCombat = false;
        struct Entry { uint64_t name; int32_t count; uint8_t replication; uint8_t padding[3]; };
        struct Array { Entry* data; int32_t size, capacity; };
        const auto entries = *static_cast<const Array*>(counts);
        if (entries.size >= 0 && entries.size <= 65536 && entries.capacity >= entries.size && (!entries.size || entries.data)) {
            combatName = *tag;
            for (int32_t i = 0; i < entries.size; ++i) {
                const auto entry = entries.data[i];
                if (entry.name == combatName && entry.count > 0) {
                    updateTags(counts, tag, -entry.count, false, entry.replication); break;
                }
            }
            combatCounts.store(counts, std::memory_order_release);
            return false;
        }
        return updateTags(counts, tag, delta, deferred, replication);
    }
    if (counts == combatCounts.load(std::memory_order_acquire) && tag && *tag == combatName) return false;
    return updateTags(counts, tag, delta, deferred, replication);
}
void execute(void* save, std::wstring_view action) {
    pollWorker();
    if (action == L"status") return;
    if (action == L"prepare") {
        require(!isolated && !worker.valid() && idle(save), "save_still_pending");
        String path{};
        pathFn(saveSystem, &path, L"PBZSaveGame");
        require(path.data && path.size > 1, "save_path_missing");
        original = fs::absolute(path.data); freeMemory(path.data);
        wchar_t local[32768]{};
        const auto length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
        require(length > 0 && length < 32768, "local_app_data_missing");
        directory = fs::path(local) / L"PBZArena/Sessions" /
            (std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
        sandbox = directory / original.filename();
        preparing = true; ready = false;
        worker = std::async(std::launch::async, [] {
            for (auto count = writes.load(); count; count = writes.load()) writes.wait(count);
            fs::create_directories(directory);
            hadOriginal = fs::exists(original);
            originalBytes.clear();
            if (!hadOriginal) return;
            originalBytes = read(original);
            for (const auto& file : fs::directory_iterator(original.parent_path()))
                if (file.is_regular_file() && file.path().extension() == L".sav")
                    fs::copy_file(file.path(), directory / file.path().filename());
            require(read(sandbox) == originalBytes, "save_copy_verification_failed");
            fs::copy_file(sandbox, directory / L"entry-baseline.sav");
        });
    } else if (action == L"activate") {
        require(ready && !worker.valid() && idle(save) && !sandbox.empty(), "isolation_not_prepared");
        isolated = true; preparing = false;
    } else if (action == L"baseline") {
        require(isolated && idle(save) && !worker.valid(), "save_still_pending");
        if (!originalBytes.empty()) return;
        ready = false;
        worker = std::async(std::launch::async, [] {
            originalBytes = read(sandbox);
            fs::copy_file(sandbox, directory / L"entry-baseline.sav");
        });
    } else if (action == L"verify") {
        require(isolated && idle(save) && !worker.valid(), "save_still_pending");
        preparing = true; ready = false;
        worker = std::async(std::launch::async, [] {
            require(hadOriginal ? fs::exists(original) && read(original) == originalBytes : !fs::exists(original),
                "main_save_changed_during_practice");
        });
    } else if (action == L"restore") {
        require(isolated && preparing && ready && !worker.valid() && idle(save), "save_still_pending");
        require(originalBytes.size() >= 5 && std::memcmp(originalBytes.data(), "PBZS", 4) == 0
            && originalBytes[4] == 3, "unsupported_save_container");
        restoring = true;
        // The game owns the allocator of any TArray passed to its container parser.
        // Reserve is the same helper used by this build's FString constructor.
        Bytes replay{};
        struct Release { Bytes& value; ~Release() { if (value.data) freeMemory(value.data); } } release{replay};
        replay.size = static_cast<int32_t>(originalBytes.size());
        reserve(1, 1, replay.size, &replay.data, 0, &replay.capacity);
        require(replay.data != nullptr, "save_replay_allocation_failed");
        std::memcpy(replay.data, originalBytes.data(), originalBytes.size());
        wchar_t slot[] = L"PBZSaveGame";
        const String physical{slot, 12, 12};
        require(loadMaster(save, replay, physical), "original_save_reload_failed");
    } else if (action == L"release") {
        require(isolated && restoring && preparing && idle(save), "restore_not_verified");
        // PuerTS compares every logical slot and account record before this call.
        combatCounts = nullptr; isolated = false; restoring = false; preparing = false; ready = false;
        originalBytes.clear();
    } else if (action == L"cancel") {
        // A failed exit never returns practice writes to the real save path.
        combatCounts = nullptr; armCombat = false; restoring = false;
        if (!worker.valid()) preparing = false;
    } else throw std::runtime_error("unknown_arena_command");
}
void state(void* self, void* frame, void* output) {
    stateFn(self, frame, output);
    if (command.empty()) return;
    const auto action = std::exchange(command, {});
    try {
        execute(self, action);
        result = "{\"idle\":" + std::string(idle(self) ? "true" : "false")
            + ",\"ready\":" + (ready ? "true" : "false")
            + ",\"isolated\":" + (isolated ? "true" : "false")
            + ",\"hadOriginal\":" + (ready && hadOriginal ? "true" : "false") + "}";
    } catch (const std::exception& error) {
        // Exception messages from filesystem APIs can contain quotes and paths;
        // keep the protocol stable and report the native detail to the debugger.
        OutputDebugStringA(error.what());
        result = errorResponse(error);
    }
}
void initialize() {
    if (initialized) return;
    require(GetModuleHandleW(L"UE4SS.dll") == nullptr, "arena_legacy_loader_active");
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    require(pe->FileHeader.TimeDateStamp == 0x6ae7ae3f && pe->OptionalHeader.SizeOfImage == 0x1e0bb000, "unsupported_arena_build");
    auto check = [base](uintptr_t rva, std::initializer_list<uint8_t> signature) {
        require(std::memcmp(reinterpret_cast<void*>(base + rva), signature.begin(), signature.size()) == 0, "unsupported_arena_signature");
    };
    check(0x484b070, {0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x18,0x55,0x57,0x41,0x54});
    check(0x43a0f40, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x7c,0x24,0x18,0x55,0x48,0x8b,0xec});
    check(0x6460f70, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x57,0x48,0x83,0xec,0x70});
    check(0x6460ac0, {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x56,0x41,0x56,0x41,0x57});
    check(0x6b67430, {0x48,0x89,0x54,0x24,0x10,0x48,0x89,0x4c,0x24,0x08,0x55,0x53,0x56,0x57,0x41,0x54});
    // The UFUNCTION thunk is an exact build-bound jump to its protected body.
    check(0x6645f30, {0xe9,0x2b,0x00,0xbb,0x12});
    check(0x127c240, {0x48,0x89,0x5c,0x24,0x08});
    check(0x125d8b0, {0x48,0x89,0x5c,0x24,0x08});
    check(0x4d2bb30, {0x48,0x89,0x5c,0x24,0x18,0x44,0x88,0x4c,0x24,0x20,0x48,0x89,0x54,0x24,0x10});
    constructString = reinterpret_cast<ConstructStringFn>(base + 0x127c240);
    freeMemory = reinterpret_cast<FreeFn>(base + 0x12e0af0);
    reserve = reinterpret_cast<ReserveFn>(base + 0x125d8b0);
    loadMaster = reinterpret_cast<LoadMasterFn>(base + 0x6b67430);
    auto* platform = reinterpret_cast<void*(*)()>(base + 0x3f80930)();
    require(platform != nullptr, "save_platform_missing");
    saveSystem = reinterpret_cast<void*(*)(void*)>((*reinterpret_cast<void***>(platform))[8])(platform);
    require(saveSystem && (*reinterpret_cast<void***>(saveSystem))[15] == reinterpret_cast<void*>(base + 0x484b070), "save_platform_mismatch");
    const uintptr_t rvas[]{0x484b070,0x43a0f40,0x6460f70,0x6460ac0,0x6645f30,0x4d2bb30};
    void* hooks[]{reinterpret_cast<void*>(&redirectedPath),reinterpret_cast<void*>(&saveGame),reinterpret_cast<void*>(&unlock),reinterpret_cast<void*>(&progress),reinterpret_cast<void*>(&state),reinterpret_cast<void*>(&filterCombat)};
    void** originals[]{reinterpret_cast<void**>(&pathFn),reinterpret_cast<void**>(&saveFn),reinterpret_cast<void**>(&unlockFn),reinterpret_cast<void**>(&progressFn),reinterpret_cast<void**>(&stateFn),reinterpret_cast<void**>(&updateTags)};
    std::vector<void*> created;
    try {
        for (size_t i = 0; i < std::size(rvas); ++i) {
            auto* target = reinterpret_cast<void*>(base + rvas[i]);
            require(MH_CreateHook(target, hooks[i], originals[i]) == MH_OK, "arena_hook_create");
            created.push_back(target);
            require(MH_QueueEnableHook(target) == MH_OK, "arena_hook_queue");
        }
        require(MH_ApplyQueued() == MH_OK, "arena_hook_enable");
    } catch (...) {
        for (auto* target : created) { MH_DisableHook(target); MH_RemoveHook(target); }
        throw;
    }
    initialized = true;
}
}
bool poll(std::wstring_view request, std::string& response) {
    constexpr std::wstring_view prefix = L"pbz-arena-v1/";
    if (!request.starts_with(prefix)) return false;
    request.remove_prefix(prefix.size());
    try {
        initialize();
        if (request == L"combat/arm") {
            require(isolated && !restoring, "practice_not_isolated"); armCombat = true; response = "{}";
        } else if (request == L"combat/result") {
            armCombat = false; response = combatCounts ? "{}" : "{\"error\":\"practice_combat_gate_failed\"}";
        } else if (request == L"combat/release") {
            armCombat = false; combatCounts = nullptr; response = "{}";
        } else if (request == L"template/start") {
            require(!templateWorker.valid(), "template_read_pending");
            templateWorker = std::async(std::launch::async, [] {
                wchar_t local[32768]{};
                const auto length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
                require(length > 0 && length < 32768, "local_app_data_missing");
                const auto file = fs::path(local) / L"PBZArena/Templates/high-cycle-profile.json";
                if (!fs::exists(file)) return std::string("{\"template\":null}");
                auto data = read(file);
                require(data.size() <= 16 * 1024 * 1024, "template_too_large");
                if (data.size() >= 3 && static_cast<uint8_t>(data[0]) == 0xef && static_cast<uint8_t>(data[1]) == 0xbb
                    && static_cast<uint8_t>(data[2]) == 0xbf) data.erase(data.begin(), data.begin() + 3);
                return std::string("{\"template\":") + std::string(data.begin(), data.end()) + "}";
            });
            response = "{}";
        } else if (request == L"template/result") {
            require(templateWorker.valid(), "template_read_missing");
            response = templateWorker.wait_for(std::chrono::seconds(0)) == std::future_status::ready
                ? templateWorker.get() : "{\"pending\":true}";
        } else if (request == L"result") {
            response = result.empty() ? "{\"error\":\"arena_state_callback_missing\"}" : result;
            result.clear(); command.clear();
        } else if (request.starts_with(L"arm/") && request.size() < 32) {
            command = request.substr(4); result.clear(); response = "{}";
        } else response = "{\"error\":\"invalid_arena_command\"}";
    } catch (const std::exception& error) { response = errorResponse(error); }
    return true;
}
}
