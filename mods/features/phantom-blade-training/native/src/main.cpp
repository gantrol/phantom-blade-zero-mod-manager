#include <Mod/CppUserModBase.hpp>
#include "Menu.hpp"
#include "Resources.hpp"
#include "Diagnostics.hpp"
#include "Isolation.hpp"
#include "Attributes.hpp"
#include "Battle.hpp"
#include "Loadout.hpp"
#include "PracticeCatalog.hpp"
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
        if (action == L"PracticeBoss" || action == L"PracticeDummy" || action == L"PracticeGroup") {
            session_.pending.practice = action == L"PracticeBoss" ? PracticeMode::Boss
                : action == L"PracticeDummy" ? PracticeMode::Dummy : PracticeMode::Group;
            session_.edit_current_targets = false;
        }
        else if (action == L"TargetEditCurrent" || action == L"TargetEditPending") {
            session_.edit_current_targets = action == L"TargetEditCurrent";
            session_.selected_target = session_.selected_target_config().id;
        }
        else if (action == L"TargetPrev" || action == L"TargetNext") session_.select_target(action == L"TargetNext" ? 1 : -1);
        else if (action == L"ClearStats") battle_.clear_stats();
        else if (action == L"AddTarget" || action == L"RemoveTarget"
            || action == L"TargetKindDown" || action == L"TargetKindUp"
            || action == L"PoseDown" || action == L"PoseUp" || action == L"PoseProgressDown" || action == L"PoseProgressUp"
            || action == L"TargetXDown" || action == L"TargetXUp" || action == L"TargetYDown" || action == L"TargetYUp"
            || action == L"TargetYawDown" || action == L"TargetYawUp") {
            if (!session_.targets_editable()) return;
            const auto previous = session_.target_selection();
            const auto previous_id = session_.selected_target;
            const int delta = action.ends_with(L"Up") ? 1 : -1;
            try {
                auto& target = session_.selected_target_config();
                if (action == L"AddTarget") session_.add_target();
                else if (action == L"RemoveTarget") session_.remove_target();
                else if (action.starts_with(L"TargetKind")) practice_catalog::step_kind(target, delta);
                else if (action.starts_with(L"PoseProgress")) target.pose_percent = std::clamp(target.pose_percent + delta, 0, 100);
                else if (action.starts_with(L"TargetX")) target.x = std::clamp(target.x + delta * 50, -1500, 1500);
                else if (action.starts_with(L"TargetYaw")) target.yaw = (target.yaw + delta * 15 + 360) % 360;
                else if (action.starts_with(L"TargetY")) target.y = std::clamp(target.y + delta * 50, -1500, 1500);
                else practice_catalog::step_motion(target, delta);
                if (session_.editing_current_targets()) battle_.configure_targets();
            } catch (const std::exception& error) {
                session_.target_selection() = previous; session_.selected_target = previous_id;
                if (session_.editing_current_targets()) battle_.configure_targets();
                report(error); menu_.render(session_, "targetEditFailed"); return;
            }
        }
        else if (action.starts_with(L"Boss") && action.size() > 4 && action[4] >= L'0' && action[4] <= L'9')
        {
            const auto index = static_cast<size_t>(std::stoul(action.substr(4)));
            if (index < resources::bosses.size())
            {
                const auto& b = resources::bosses[index];
                session_.select_boss(index, b.default_scene);
            }
        }
        else if (action.starts_with(L"Scene") && action.size() > 5)
        {
            const auto index = static_cast<size_t>(std::stoul(action.substr(5)));
            if (index < resources::scenes.size())
                session_.preview_scene(scene_at_slot(resources::bosses[session_.pending.boss].default_scene, index));
        }
        else if (action == L"UseCurrentLoadout")
        {
            battle_.sync_player_baseline(session_);
            if (session_.baseline_ready) session_.use_current();
        }
        else if (action == L"CopyStage") {
            if (auto config = loadout::stage_equipment(session_)) {
                session_.remember_custom();
                session_.pending.config = *config;
                session_.remember_custom();
            }
        }
        else if (action == L"BossDefaults") session_.reset_boss();
        else if (action == L"DifficultyDown" || action == L"DifficultyUp") session_.step_boss(L"Difficulty", action.ends_with(L"Up") ? 1 : -1);
        else if (action == L"CycleDown" || action == L"CycleUp") session_.step_boss(L"Cycle", action.ends_with(L"Up") ? 1 : -1);
        else if (action == L"BossHealthDown" || action == L"BossHealthUp") session_.step_boss(L"BossHealth", action.ends_with(L"Up") ? 1 : -1);
        else if (action == L"BossAttackDown" || action == L"BossAttackUp") session_.step_boss(L"BossAttack", action.ends_with(L"Up") ? 1 : -1);
        else if (action == L"LoadoutCurrent") session_.select_loadout_mode(LoadoutMode::Adaptive);
        else if (action == L"LoadoutRecommended")
        {
            session_.select_loadout_mode(LoadoutMode::Stage);
        }
        else if (action == L"LoadoutCustom")
        {
            session_.select_loadout_mode(LoadoutMode::Custom);
        }
        else if (action == L"HealthDown") session_.step_health(-50);
        else if (action == L"HealthUp") session_.step_health(50);
        else if (action == L"HealthAuto" && session_.editable()) session_.pending.config.max_health = 0;
        else if (action.starts_with(L"Equip") && action.size() > 6 && action[5] >= L'0' && action[5] <= L'3') {
            const size_t slot = static_cast<size_t>(action[5] - L'0');
            const auto operation = action.substr(6);
            if (operation == L"Prev") session_.cycle_weapon(slot, -1);
            else if (operation == L"Next") session_.cycle_weapon(slot, 1);
            else if (operation == L"Down") session_.step_weapon(slot, -1);
            else if (operation == L"Up") session_.step_weapon(slot, 1);
        }
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
        {
            const auto& initial = resources::bosses[resources::initial_boss];
            session_.reset_default(initial.default_scene, resources::initial_boss);
        }
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
            if (action == "isolate") { isolation_.initialize(); if (!isolation_.idle(save)) return std::string("save_busy"); isolation_.prepare(save); }
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
    void event(UObject* object, UFunction* function, void* parameters)
    {
        if (!running_ || inside_ || !system_class_) return;
        Scope scope(inside_);
        try
        {
            battle_.event(object, function, parameters);
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
        catch (const std::exception& error) { report(error); battle_.fail(); failed_ = true; }
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
                battle_.stop_practice();
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
                if (valid(root)) {
                    // A temporarily unavailable backpack must not disable the whole menu.
                    try { battle_.sync_player_baseline(session_); }
                    catch (const std::exception& error) { report(error); session_.baseline_ready = false; }
                    menu_.attach(root, session_);
                }
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
