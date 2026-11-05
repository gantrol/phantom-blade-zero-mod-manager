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
    void prepare();
    bool prepared();
    void activate();
    void verify_original();
    void restore_account(ue::UObject* save);
    bool active() const;
    bool idle(ue::UObject* save) const;
    std::wstring status() const;
};
}
