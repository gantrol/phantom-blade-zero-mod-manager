#pragma once
#include "Attributes.hpp"
#include "Isolation.hpp"
#include "Selection.hpp"
#include "Practice.hpp"
#include <nlohmann/json.hpp>
#include <chrono>

namespace arena {
class Battle {
public:
    enum class Update { None, Started, Ended, Returned };
    Battle(Isolation& isolation, Attributes& attributes, Session& session)
        : isolation_(isolation), attributes_(attributes), session_(session), practice_(session) {}
    void start(bool reset = false);
    void end();
    void set_lock(bool enabled);
    void sync_player_baseline(Session& session);
    void fail();
    void event(ue::UObject* actor, ue::UFunction* function, void* parameters) { practice_.event(actor, function, parameters); }
    void clear_stats() { practice_.clear_stats(); }
    void configure_targets() { if (session_.current) practice_.configure(*session_.current); }
    void stop_practice() { practice_.stop(); }
    Update tick();
    bool active() const { return !checkpoint_.is_null(); }
    std::string status() const;
private:
    enum class Step { Idle, Drain, Capture, Copy, Write, Save, Load, World, Spawn, Finish, Verify, Restore };
    Isolation& isolation_;
    Attributes& attributes_;
    Session& session_;
    Practice practice_;
    Step step_{Step::Idle};
    bool returning_{}, first_entry_{}, spawning_{}, resetting_{};
    Selection entering_{};
    float baseline_max_health_{};
    EntryConfig baseline_loadout_{};
    std::vector<OwnedWeapon> baseline_owned_;
    nlohmann::json checkpoint_, round_, transfer_;
    std::map<std::wstring,float> return_resources_;
    std::map<std::wstring,std::wstring> return_spawn_;
    std::wstring return_slot_, level_, spawn_transform_;
    ue::FWeakObjectPtr old_player_, boss_, spawner_, ready_player_;
    ue::FWeakObjectPtr held_player_;
    uint8_t return_movement_mode_{};
    std::chrono::steady_clock::time_point since_;
    void stage(Step);
    void snapshot();
    void prepare_round(bool reset);
    void apply_entry_loadout();
    void write();
    void load();
    void restore_return_spawn();
    bool player_ready(ue::UObject*);
    bool world_ready();
    void release_player();
    ue::UObject* find_spawner();
};
}
