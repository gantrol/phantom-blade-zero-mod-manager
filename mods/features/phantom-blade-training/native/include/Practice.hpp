#pragma once
#include "Reflection.hpp"
#include "Selection.hpp"

namespace arena {
// Transient targets belong exclusively to the isolated arena world.
class Practice {
    struct Target {
        TargetConfig config{}, spawned_config{};
        std::wstring spawn_transform;
        ue::FWeakObjectPtr spawner, actor, ai, mesh, anim, pose_montage, label;
        bool controlled{};
        bool awaiting_spawn{true};
        float animation_rate{1};
        double recover_until{};
        double spawn_since{};
        std::string motion;
        int32_t pose_percent{-1};
    };
    Session& session_;
    ue::FWeakObjectPtr player_;
    ue::FWeakObjectPtr hud_;
    ue::FWeakObjectPtr anchor_;
    std::wstring anchor_transform_;
    std::vector<Target> targets_;
    std::wstring equipment_;
    double first_hit_{-1};
    bool running_{};
    bool chinese_{}, hud_hidden_{};
    double hud_update_{};
    Selection selection_{};
    void prepare(Target&, ue::UObject*);
    void spawn(Target&);
    std::wstring placement(const TargetConfig&) const;
    static void destroy_target(Target&) noexcept;
    void thaw(Target&);
    void freeze(Target&);
    void update_hud();
    double time() const;
    std::wstring equipment() const;
public:
    explicit Practice(Session& session) : session_(session) {}
    static void initialize();
    void begin(ue::UObject* player, ue::UObject* anchor, const Selection&);
    bool tick();
    void event(ue::UObject*, ue::UFunction*, void*);
    void clear_stats();
    void configure(const Selection&);
    void stop() noexcept;
    bool active() const { return player_.Get() != nullptr; }
};
}
