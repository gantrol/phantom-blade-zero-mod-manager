#include <Mod/CppUserModBase.hpp>
#include "Menu.hpp"
#include "Resources.hpp"
#include "Diagnostics.hpp"
#include "Isolation.hpp"
#include "Attributes.hpp"
#include "Battle.hpp"
#include <Windows.h>
#include <atomic>
#include <mutex>

namespace arena
{
using namespace ue;
class Runtime
{
    Menu menu_;
    Session session_;
    Isolation isolation_;
    Attributes attributes_;
    Battle battle_{isolation_, attributes_, session_};
    bool show_arena_{};
    UClass* system_class_{};
    std::atomic<bool> stop_{};
    std::atomic<bool> running_{true};
    bool inside_{};
    bool failed_{};
    FWeakObjectPtr pending_root_;
#if ARENA_DIAGNOSTICS
    uint64_t ticks_{}, tick_ns_{};
    std::chrono::steady_clock::time_point profile_since_{std::chrono::steady_clock::now()};
#endif

    struct Scope
    {
        bool& value;
        explicit Scope(bool& ref) : value(ref) { value = true; }
        ~Scope() { value = false; }
    };
    void report(const std::exception& error)
    {
        const auto message = L"[PhantomBladeArena] " + wide(error.what()) + L"\n";
        RC::Output::send(message);
    }
    void command(const std::wstring& action)
    {
        if (session_.busy()) return;
        bool return_scene_focus = false;
        if (action.starts_with(L"Boss") && action.size() > 4)
        {
            const auto index = static_cast<size_t>(std::stoul(action.substr(4)));
            if (index < resources::bosses.size())
            {
                const auto& b = resources::bosses[index];
                session_.select_boss(index, b.default_scene, b.recommended_max_health, b.recommended_weapon_level);
            }
        }
        else if (action.starts_with(L"Scene") && action.size() > 5)
        {
            const auto index = static_cast<size_t>(std::stoul(action.substr(5)));
            if (index < resources::scenes.size())
                session_.preview_scene(scene_at_slot(resources::bosses[session_.pending.boss].default_scene, index));
        }
        else if (action == L"LoadoutCurrent")
        {
            battle_.sync_player_baseline(session_);
            const auto& b = resources::bosses[session_.pending.boss];
            session_.select_loadout_mode(LoadoutMode::Current, b.recommended_max_health, b.recommended_weapon_level);
        }
        else if (action == L"LoadoutRecommended")
        {
            const auto& b = resources::bosses[session_.pending.boss];
            session_.select_loadout_mode(LoadoutMode::Recommended, b.recommended_max_health, b.recommended_weapon_level);
        }
        else if (action == L"LoadoutCustom")
        {
            const auto& b = resources::bosses[session_.pending.boss];
            session_.select_loadout_mode(LoadoutMode::Custom, b.recommended_max_health, b.recommended_weapon_level);
        }
        else if (action == L"HealthDown") session_.step_health(-50);
        else if (action == L"HealthUp") session_.step_health(50);
        else if (action == L"WeaponDown") session_.step_weapon(-1);
        else if (action == L"WeaponUp") session_.step_weapon(1);
        else if (action == L"ChangeScene") session_.open_scenes();
        else if (action == L"CancelScene") { session_.cancel_scene(); return_scene_focus = true; }
        else if (action == L"UseScene") { session_.use_scene(); return_scene_focus = true; }
        else if (action == L"DefaultScene") session_.pending.scene = resources::bosses[session_.pending.boss].default_scene;
        else if (action == L"LockHealth") battle_.set_lock(!session_.lock_health);
        else if (action == L"Back")
        {
            if (session_.scene_preview) { session_.cancel_scene(); return_scene_focus = true; }
            else { menu_.close(); return; }
        }
        else if (action == L"Start")
        {
            battle_.start(); menu_.render(session_, "entering"); menu_.close(); return;
        }
        else if (action == L"Reset") { battle_.start(true); menu_.render(session_, "resetting"); menu_.close(); return; }
        else if (action == L"End") { battle_.end(); menu_.render(session_, "returning"); menu_.close(); return; }
        else if (action == L"Continue") { session_.resume(); menu_.close(); return; }
        menu_.render(session_);
        if (action == L"ChangeScene") menu_.focus_primary(session_);
        if (return_scene_focus) menu_.focus_scene_action();
    }
public:
    void request_stop() { stop_.store(true, std::memory_order_release); }
    void start() {
        stop_.store(false, std::memory_order_release); running_ = true;
        if (!resources::bosses.empty())
            session_.reset_default(resources::bosses[0].default_scene, resources::bosses[0].recommended_max_health, resources::bosses[0].recommended_weapon_level);
        diagnostics::set_handler([this](const std::string& action) {
#if ARENA_DIAGNOSTICS
            if (action == "cache_on" || action == "cache_off") {
                lookup_cache(action == "cache_on"); ticks_ = tick_ns_ = 0;
                profile_since_ = std::chrono::steady_clock::now();
                return std::string("profiling_started");
            }
            if (action == "profile") {
                const auto seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - profile_since_).count();
                return "frames=" + std::to_string(ticks_) + " seconds=" + std::to_string(seconds)
                    + " fps=" + std::to_string(ticks_ / seconds)
                    + " mod_ms_per_frame=" + std::to_string(ticks_ ? double(tick_ns_) / ticks_ / 1e6 : 0)
                    + " " + lookup_profile();
            }
#endif
            auto* player = UObjectGlobals::FindFirstOf(L"BP_Renewal_Hun_C");
            auto* save = UObjectGlobals::FindFirstOf(L"PBZSaveLoadSubsystem");
            if (action == "isolate") { isolation_.initialize(); if (!isolation_.idle(save)) return std::string("save_busy"); isolation_.prepare(); }
            else if (action == "activate") { if (!isolation_.prepared()) return std::string("copy_pending"); isolation_.activate(); }
            else if (action == "fill" || action == "lock_on" || action == "lock_off") {
                if (!isolation_.active()) throw std::runtime_error("practice_isolation_required");
                if (action == "fill") Attributes::refill(player);
                else attributes_.lock(player, action == "lock_on");
            }
            else if (action != "status") command(wide(action));
            return utf8(isolation_.status()) + " " + battle_.status() + " idle=" + std::to_string(isolation_.idle(save))
                + (player ? " health=" + std::to_string(Attributes::read(player, L"Health")) : "");
        });
    }
    void event(UObject* object, UFunction* function, void*)
    {
        if (!running_ || inside_ || !system_class_) return;
        Scope scope(inside_);
        try
        {
            if (menu_.owns(object)) { menu_.event(object, function); return; }
            if (!object->IsA(system_class_)) return;
            const auto name = function->GetName();
            if (name == L"TsConstruct" || name == L"Construct")
            {
                if (!menu_.attached()) { pending_root_ = object; failed_ = false; }
            }
            else if (name == L"TsDestruct" || name == L"Destruct")
            {
                if (menu_.context() == object) menu_.detach();
                if (pending_root_.Get() == object) pending_root_.Reset();
                session_.resume();
            }
        }
        catch (const std::exception& error) { report(error); failed_ = true; }
    }
    void tick()
    {
        if (inside_ || !running_) return;
#if ARENA_DIAGNOSTICS
        struct Timing {
            uint64_t &count, &total;
            std::chrono::steady_clock::time_point begin{std::chrono::steady_clock::now()};
            ~Timing() { ++count; total += std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - begin).count(); }
        } timing{ticks_, tick_ns_};
#endif
        Scope scope(inside_);
        try
        {
            if (stop_.load(std::memory_order_acquire))
            {
                menu_.detach(); pending_root_.Reset(); running_ = false; return;
            }
            diagnostics::tick();
            try {
                const auto update = battle_.tick();
                if (update == Battle::Update::Ended) {
                    show_arena_ = true;
                    if (auto* p = UObjectGlobals::FindFirstOf(L"BP_Renewal_Hun_C")) invoke(p, L"PushSystemUI");
                }
                if (update == Battle::Update::Started || update == Battle::Update::Returned) menu_.render(session_);
            } catch (const std::exception& error) {
                report(error); battle_.fail(); menu_.render(session_, "actionFailed");
            }
            if (!system_class_)
                system_class_ = static_cast<UClass*>(find(L"/Script/ProjectPBZ.PBZSystemScreenWidget"));
            if (pending_root_.Get() && !failed_)
            {
                auto* root = pending_root_.Get(); pending_root_.Reset();
                if (valid(root)) { battle_.sync_player_baseline(session_); menu_.attach(root, session_); }
            }
            if (show_arena_ && menu_.attached()) { menu_.show(); menu_.focus_primary(session_); show_arena_ = false; }
            if (failed_) return;
            menu_.tick(session_);
            for (const auto& action : menu_.take_commands()) {
                try { command(action); }
                catch (const std::exception& error) { report(error); battle_.fail(); menu_.render(session_, "actionFailed"); }
            }
        }
        catch (const std::exception& error) { report(error); failed_ = true; }
    }
};

Runtime& runtime()
{
    // Hook callbacks are process-lifetime registrations in this UE4SS build.
    // Keep the receiver alive and disarm it on unload; never leave dangling code.
    static auto* value = new Runtime;
    return *value;
}

class Mod final : public RC::CppUserModBase
{
public:
    Mod()
    {
        ModName = L"PhantomBladeArena";
        ModVersion = L"0.1.0-native-menu";
        ModDescription = L"In-game arena menu integration";
        ModAuthors = L"gantrol";
        ModIntendedSDKVersion = L"cdbc0b66";
    }
    void on_unreal_init() override
    {
        HMODULE self{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&runtime), &self)) return;
        static std::once_flag hooks;
        std::call_once(hooks, [] {
            Hook::RegisterProcessEventPostCallback([](UObject* object, UFunction* function, void* parameters) {
                runtime().event(object, function, parameters);
            });
            Hook::RegisterEngineTickPostCallback([](UEngine*, float) { runtime().tick(); });
        });
        runtime().start();
        diagnostics::start();
        RC::Output::send(L"[PhantomBladeArena] Native arena development build loaded.\n");
    }
    ~Mod() override { runtime().request_stop(); }
};
}

extern "C" __declspec(dllexport) RC::CppUserModBase* start_mod() { return new arena::Mod; }
extern "C" __declspec(dllexport) void uninstall_mod(RC::CppUserModBase* mod) { delete mod; }
