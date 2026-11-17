#pragma once
#include "Reflection.hpp"
#include <filesystem>
#include <future>
namespace arena {
class Isolation {
    std::filesystem::path original_, sandbox_;
    std::future<void> copy_;
    bool ready_{};
public:
    void initialize();
    void prepare(ue::UObject* save);
    bool prepared();
    void activate();
    void verify_original();
    void begin_restore_account(ue::UObject* save);
    void finish_restore_account(ue::UObject* save, std::wstring_view continue_slot);
    void cancel_restore();
    bool active() const;
    bool idle(ue::UObject* save) const;
    std::wstring status() const;
};
}
