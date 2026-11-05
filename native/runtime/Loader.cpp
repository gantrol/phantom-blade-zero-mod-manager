#include <windows.h>
#include <bcrypt.h>
#include <MinHook.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

namespace {
namespace fs = std::filesystem;
// Steam 26367565 / CL-302256_JKS-17280 only. See COMPATIBILITY.txt.
constexpr uintptr_t StartRva = 0x62cc860;
constexpr uintptr_t ExecuteRva = 0x61ec420;
constexpr uintptr_t BytesRva = 0x62f65a0;
constexpr uintptr_t SourceRva = 0x62f65e0;
constexpr uintptr_t LogRva = 0x61ff840;
constexpr uintptr_t ToStringRva = 0x62f38e0;
constexpr uintptr_t FreeRva = 0x12e0af0;
struct FString { const wchar_t* data; int32_t count; int32_t capacity; };
struct ByteArray { const uint8_t* data; int32_t count; int32_t capacity; };
static_assert(sizeof(FString) == 16 && sizeof(ByteArray) == 16);
using StartFn = void(*)(void*, const FString&, const void*);
using ExecuteFn = void(*)(void*, const FString&);
using BytesFn = bool(*)(const FString&, const ByteArray&);
using SourceFn = bool(*)(const FString&, const FString&);
// Reviewed layout of this build's V8 FunctionCallbackInfo<Value>.
struct CallbackInfo { uintptr_t* implicit; uintptr_t* values; int32_t length; };
using LogFn = void(*)(void*, const CallbackInfo&);
using ToStringFn = FString*(*)(FString*, void*, uintptr_t*);
using FreeFn = void(*)(void*);
StartFn originalStart;
BytesFn originalBytes;
SourceFn originalSource;
LogFn originalLog;
ToStringFn toString;
FreeFn freeString;
ExecuteFn execute;
using Digest = std::array<uint8_t, 32>;
struct Script { size_t byteCount, sourceLength; Digest bytes, source; };
std::map<std::wstring, Script, std::less<>> scripts;
std::atomic<bool> bootstrapped = false;
std::atomic<bool> bootstrapReturned = false;
std::atomic<unsigned> acceptedBytes = 0, acceptedSources = 0, mismatches = 0;
HANDLE startedEvent;
std::mutex messagesMutex;
std::vector<std::wstring> messages;

bool hash(const void* data, size_t size, Digest& output) {
    return BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
        const_cast<PUCHAR>(static_cast<const uint8_t*>(data)), static_cast<ULONG>(size), output.data(), static_cast<ULONG>(output.size())) >= 0;
}

std::wstring_view view(const FString& value) {
    return value.data && value.count > 0
        ? std::wstring_view(value.data, static_cast<size_t>(value.count - 1)) : std::wstring_view{};
}

void captureLog(void* env, const CallbackInfo& info) {
    originalLog(env, info);
    if (info.length < 2) return;
    FString message{};
    toString(&message, reinterpret_cast<void*>(info.implicit[1]), info.values + 1);
    const auto text = view(message);
    if (text.starts_with(L"[PhantomBlade")) {
        // The callback copies bounded text only; the startup worker owns file I/O.
        try {
            std::unique_lock lock(messagesMutex, std::try_to_lock);
            if (lock.owns_lock() && messages.size() < 128) {
                messages.emplace_back(text.substr(0, 4096));
                SetEvent(startedEvent);
            }
        } catch (...) { /* Diagnostic allocation must not interrupt gameplay. */ }
    }
    if (message.data) freeString(const_cast<wchar_t*>(message.data));
}

std::wstring normalize(std::wstring_view value) {
    std::wstring path(value);
    std::replace(path.begin(), path.end(), L'\\', L'/');
    std::transform(path.begin(), path.end(), path.begin(), towlower);
    return path;
}

const Script* findScript(const FString& name) {
    auto path = normalize(view(name));
    constexpr std::wstring_view prefix = L"/content/javascript/";
    const auto at = path.find(prefix);
    if (at != std::wstring::npos) path.erase(0, at + prefix.size());
    const auto found = scripts.find(path);
    return found == scripts.end() ? nullptr : &found->second;
}

bool validateBytes(const FString& name, const ByteArray& bytes) {
    const auto* script = findScript(name);
    if (!script) return originalBytes(name, bytes);
    Digest actual;
    const bool ok = bytes.count >= 0 && static_cast<size_t>(bytes.count) == script->byteCount
        && hash(bytes.data, bytes.count, actual) && actual == script->bytes;
    if (ok) ++acceptedBytes; else ++mismatches;
    return ok;
}

bool validateSource(const FString& name, const FString& source) {
    const auto* script = findScript(name);
    if (!script) return originalSource(name, source);
    auto text = view(source);
    // The original modular.js evaluates CommonJS modules with this exact wrapper.
    constexpr std::wstring_view head = L"(function (exports, require, module, __filename, __dirname) { ";
    constexpr std::wstring_view tail = L"\n});";
    if (text.starts_with(head) && text.ends_with(tail)) {
        text.remove_prefix(head.size());
        text.remove_suffix(tail.size());
    }
    Digest actual;
    const bool ok = text.size() == script->sourceLength
        && hash(text.data(), text.size() * sizeof(wchar_t), actual) && actual == script->source;
    if (ok) ++acceptedSources; else ++mismatches;
    return ok;
}

void start(void* env, const FString& module, const void* args) {
    originalStart(env, module, args);
    if (view(module) != L"MainStart" || !*reinterpret_cast<const bool*>(static_cast<char*>(env) + 0x190)) return;
    if (bootstrapped.exchange(true)) return;
    static constexpr wchar_t path[] = L"PhantomBladeRuntime/Bootstrap.js";
    const FString bootstrap{path, static_cast<int32_t>(std::size(path)), static_cast<int32_t>(std::size(path))};
    execute(env, bootstrap);
    bootstrapReturned = true;
    SetEvent(startedEvent);
}

void preload(const fs::path& directory, std::wstring_view name, size_t& total) {
    if (!fs::exists(directory)) return;
    for (const auto& file : fs::recursive_directory_iterator(directory)) {
        if ((GetFileAttributesW(file.path().c_str()) & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw std::runtime_error("SCRIPT_REPARSE_POINT");
        if (!file.is_regular_file() || file.path().extension() != L".js") continue;
        const auto size = file.file_size();
        total += static_cast<size_t>(size);
        if (total > 64 * 1024 * 1024) throw std::runtime_error("SCRIPT_BUDGET_EXCEEDED");
        Script script{};
        std::vector<uint8_t> bytes(static_cast<size_t>(size));
        // Startup worker only: sequential bounded reads, never in a game callback.
        std::ifstream input(file.path(), std::ios::binary);
        if (!input.read(reinterpret_cast<char*>(bytes.data()), size)) throw std::runtime_error("SCRIPT_READ_FAILED");
        auto text = std::string_view(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        if (text.starts_with("\xef\xbb\xbf")) text.remove_prefix(3);
        const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
        if (count == 0 && !text.empty()) throw std::runtime_error("SCRIPT_UTF8_INVALID");
        std::wstring source(count, L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), source.data(), count);
        script.byteCount = bytes.size();
        script.sourceLength = source.size();
        if (!hash(bytes.data(), bytes.size(), script.bytes) || !hash(source.data(), source.size() * sizeof(wchar_t), script.source))
            throw std::runtime_error("SCRIPT_HASH_FAILED");
        scripts.emplace(normalize(std::wstring(name) + L"/" + fs::relative(file.path(), directory).generic_wstring()), std::move(script));
    }
}

bool matches(const uint8_t* base, uintptr_t rva, std::initializer_list<uint8_t> expected) {
    return memcmp(base + rva, expected.begin(), expected.size()) == 0;
}

DWORD WINAPI initialize(void*) {
    wchar_t filename[32768];
    const DWORD length = GetModuleFileNameW(nullptr, filename, static_cast<DWORD>(std::size(filename)));
    if (!length || length == std::size(filename)) return 0;
    const fs::path binary(filename);
    if (_wcsicmp(binary.filename().c_str(), L"PBZ.exe") != 0) return 0;
    const auto runtime = binary.parent_path() / L"PhantomBladeRuntime";
    std::ofstream log(runtime / L"loader.log", std::ios::trunc);
    try {
        const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (pe->FileHeader.TimeDateStamp != 0x6ae7ae3f || pe->OptionalHeader.SizeOfImage != 0x1e0bb000
            || !matches(base, StartRva, {0x48,0x89,0x5c,0x24,0x20,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57})
            || !matches(base, ExecuteRva, {0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57})
            || !matches(base, BytesRva, {0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xda,0xe8,0x32,0x3b,0xff,0xff})
            || !matches(base, SourceRva, {0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57})
            || !matches(base, LogRva, {0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57})) {
            log << "UNSUPPORTED_BUILD\n"; return 0;
        }
        const auto content = binary.parent_path().parent_path().parent_path() / L"ProjectPBZ/Content/JavaScript";
        size_t total = 0;
        for (const auto name : {L"PhantomBladeRuntime", L"PhantomBladeSprint", L"PhantomBladeMap", L"PhantomBladeInfiniteTalisman", L"PhantomBladeQuickActions"})
            preload(content / name, name, total);
        if (!scripts.contains(L"phantombladeruntime/bootstrap.js")) { log << "BOOTSTRAP_MISSING\n"; return 0; }
        startedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!startedEvent) throw std::runtime_error("EVENT_FAILED");
        execute = reinterpret_cast<ExecuteFn>(const_cast<uint8_t*>(base) + ExecuteRva);
        toString = reinterpret_cast<ToStringFn>(const_cast<uint8_t*>(base) + ToStringRva);
        freeString = reinterpret_cast<FreeFn>(const_cast<uint8_t*>(base) + FreeRva);
        if (MH_Initialize() != MH_OK) throw std::runtime_error("HOOK_INIT_FAILED");
        const uintptr_t addresses[] = {BytesRva, SourceRva, StartRva, LogRva};
        void* hooks[] = {reinterpret_cast<void*>(&validateBytes), reinterpret_cast<void*>(&validateSource), reinterpret_cast<void*>(&start), reinterpret_cast<void*>(&captureLog)};
        void** originals[] = {reinterpret_cast<void**>(&originalBytes), reinterpret_cast<void**>(&originalSource), reinterpret_cast<void**>(&originalStart), reinterpret_cast<void**>(&originalLog)};
        for (size_t i = 0; i < std::size(addresses); ++i) {
            auto target = const_cast<uint8_t*>(base) + addresses[i];
            if (MH_CreateHook(target, hooks[i], originals[i]) != MH_OK || MH_QueueEnableHook(target) != MH_OK)
                throw std::runtime_error("HOOK_CREATE_FAILED");
        }
        if (MH_ApplyQueued() != MH_OK) {
            MH_DisableHook(MH_ALL_HOOKS);
            throw std::runtime_error("HOOK_ENABLE_FAILED");
        }
        log << "READY build=26367565 scripts=" << scripts.size() << " bytes=" << total << std::endl;
        bool reported = false;
        size_t logBytes = 0;
        for (;;) {
            WaitForSingleObject(startedEvent, INFINITE);
            if (!reported && bootstrapReturned.load()) {
                reported = true;
                log << "BOOTSTRAP_RETURNED bytes=" << acceptedBytes << " sources=" << acceptedSources << " mismatches=" << mismatches << std::endl;
            }
            std::vector<std::wstring> pending;
            { std::lock_guard lock(messagesMutex); pending.swap(messages); }
            for (const auto& message : pending) {
                if (logBytes >= 2 * 1024 * 1024) break;
                const auto characters = static_cast<int>(message.size());
                std::string utf8(WideCharToMultiByte(CP_UTF8, 0, message.data(), characters, nullptr, 0, nullptr, nullptr), '\0');
                WideCharToMultiByte(CP_UTF8, 0, message.data(), characters, utf8.data(), static_cast<int>(utf8.size()), nullptr, nullptr);
                log << GetTickCount64() << ' ' << utf8 << '\n';
                logBytes += utf8.size();
            }
            log.flush();
        }
    } catch (const std::exception& error) {
        log << error.what() << std::endl;
    }
    return 0;
}
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        // Work begins after the loader lock is released; DllMain never waits.
        if (HANDLE worker = CreateThread(nullptr, 0, initialize, nullptr, 0, nullptr)) CloseHandle(worker);
    }
    return TRUE;
}
