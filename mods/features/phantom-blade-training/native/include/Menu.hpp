#pragma once
#include "Reflection.hpp"
#include "Selection.hpp"
#include "Catalog.hpp"
#include <deque>
#include <string>
#include <unordered_map>
#include <vector>

namespace arena
{
class Menu
{
    ue::UObject* root_{};
    ue::UObject* view_{};
    ue::UObject* tab_list_{};
    ue::UObject* switcher_{};
    ue::UObject* nav_{};
    ue::UObject* action_bar_{};
    std::wstring action_bar_visibility_;
    ue::FWeakObjectPtr root_handle_, view_handle_;
    std::unordered_map<std::wstring, ue::UObject*> controls_;
    std::unordered_map<std::wstring, ue::UObject*> textures_;
    std::unordered_map<std::wstring, ue::UObject*> brushes_;
    std::wstring language_;
    std::vector<std::wstring> boss_names_;
    std::vector<std::wstring> boss_chapters_;
    bool selected_{};
    bool navigation_dirty_{};
    std::deque<std::wstring> commands_;
    ue::UObject* control(const wchar_t* name);
    std::wstring localized(LocalText) const;
    std::wstring scene_name(size_t) const;
    void label(const wchar_t*, std::wstring_view);
    void image(const wchar_t*, const wchar_t*);
    void focus(const wchar_t*);
    void sync_navigation(bool selected);
public:
    void attach(ue::UObject* root, Session&);
    void detach();
    void forget();
    void event(ue::UObject*, ue::UFunction*);
    void tick(Session&);
    void render(const Session&, std::string_view error = {});
    void focus_primary(const Session&);
    void focus_scene_action() { focus(L"ChangeScene"); }
    void show();
    void close();
    std::wstring message(std::string_view) const;
    std::deque<std::wstring> take_commands();
    ue::UObject* context() const { return root_handle_.Get(); }
    bool attached() const { return root_ != nullptr; }
    bool owns(ue::UObject* object) const { return object == view_handle_.Get() || object == nav_; }
};
}
