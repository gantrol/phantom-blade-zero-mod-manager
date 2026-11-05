// Development-only reflection console. Excluded from distribution builds.
#include "Diagnostics.hpp"
#include "Reflection.hpp"
#include <Windows.h>
#include <condition_variable>
#include <mutex>
#include <optional>
#include <sstream>
#include <thread>

namespace arena::diagnostics {
using namespace ue;
namespace {
std::mutex mutex;
std::condition_variable completed;
std::optional<std::string> request, response;
std::function<std::string(const std::string&)> handler;
std::wstring object_path(UObject* obj) {
    auto full = obj->GetFullName();
    return full.substr(full.find(L' ') + 1);
}
std::string execute(const std::string& input) {
    std::istringstream in(input);
    std::string op, path, name, line;
    std::getline(in, op); std::getline(in, path);
    std::wstring out;
    if (op == "arena") return handler ? handler(path) : "handler_not_ready";
    if (op == "list") {
        std::vector<UObject*> objects;
        UObjectGlobals::FindAllOf(wide(path).c_str(), objects);
        for (auto* obj : objects) out += object_path(obj) + L"\n";
        return utf8(out);
    }
    auto* obj = path.starts_with("first:") ? UObjectGlobals::FindFirstOf(wide(path.substr(6)).c_str()) : find(wide(path).c_str());
    if (!obj) throw std::runtime_error("object_not_found");
    if (op == "describe") {
        out = obj->GetFullName() + L"\n";
        for (auto* type = static_cast<UStruct*>(obj->GetClassPrivate()); type; type = type->GetSuperStruct()) {
            out += L"TYPE " + type->GetFullName() + L"\n";
            for (auto* f = type->GetChildProperties(); f; f = f->next()) {
                auto n = f->GetName();
                out += n + L"=" + field(obj, n.c_str()).text() + L"\n";
            }
        }
    } else if (op == "schema") {
        for (auto* type = static_cast<UStruct*>(obj); type; type = type->GetSuperStruct()) {
            out += L"TYPE " + type->GetFullName() + L" SIZE " + std::to_wstring(type->GetPropertiesSize()) + L"\n";
            for (auto* f = type->GetChildProperties(); f; f = f->next()) {
                auto* p = static_cast<FProperty*>(f);
                out += p->GetName() + L" @" + std::to_wstring(p->GetOffset_Internal()) + L" size=" + std::to_wstring(p->GetElementSize()) + L"\n";
            }
        }
    } else if (op == "get") {
        while (std::getline(in, name)) if (!name.empty()) out += wide(name) + L"=" + field(obj, wide(name).c_str()).text() + L"\n";
    } else if (op == "call") {
        std::getline(in, name);
        auto* function = obj->GetFunctionByNameInChain(wide(name).c_str());
        if (!function) throw std::runtime_error("function_missing");
        Frame params(function);
        while (std::getline(in, line)) {
            auto split = line.find('=');
            if (split == std::string::npos) continue;
            params[wide(line.substr(0, split)).c_str()].text(wide(line.substr(split + 1)));
        }
        obj->ProcessEvent(function, params.data());
        for (auto* f = function->GetChildProperties(); f; f = f->next()) {
            auto n = f->GetName(); out += n + L"=" + params[n.c_str()].text() + L"\n";
        }
    } else throw std::runtime_error("unknown_command");
    return utf8(out);
}
void serve() {
    const auto name = L"\\\\.\\pipe\\PhantomBladeArena.Dev." + std::to_wstring(GetCurrentProcessId());
    auto pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1,
        8 * 1024 * 1024, 8 * 1024 * 1024, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return;
    for (;;) {
        if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) break;
        std::string input(8 * 1024 * 1024, '\0'); DWORD size{};
        if (ReadFile(pipe, input.data(), DWORD(input.size()), &size, nullptr)) {
            input.resize(size);
            std::unique_lock lock(mutex);
            request = std::move(input); response.reset();
            completed.wait(lock, [] { return response.has_value(); });
            auto result = std::move(*response); response.reset(); lock.unlock();
            WriteFile(pipe, result.data(), DWORD(result.size()), &size, nullptr);
            FlushFileBuffers(pipe);
        }
        DisconnectNamedPipe(pipe);
    }
    CloseHandle(pipe);
}
}
void start() { static std::once_flag once; std::call_once(once, [] { std::thread(serve).detach(); }); }
void set_handler(std::function<std::string(const std::string&)> value) { handler = std::move(value); }
void tick() {
    std::optional<std::string> input;
    { std::lock_guard lock(mutex); input.swap(request); }
    if (!input) return;
    std::string result;
    try { result = "OK\n" + execute(*input); }
    catch (const std::exception& e) { result = "ERROR\n" + std::string(e.what()); }
    { std::lock_guard lock(mutex); response = std::move(result); }
    completed.notify_one();
}
}
