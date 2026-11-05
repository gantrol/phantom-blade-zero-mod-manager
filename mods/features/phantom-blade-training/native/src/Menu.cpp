#include "Menu.hpp"
#include "Resources.hpp"
#include <algorithm>

namespace arena
{
using namespace ue;
namespace
{
constexpr auto tab_id = L"PhantomBladeArena";
std::wstring remove_tab_mapping(std::wstring_view map, int32_t removed_index)
{
    std::vector<std::wstring> entries;
    int depth = 0;
    size_t start = 0;
    bool quoted = false, escaped = false;
    for (size_t i = 0; i < map.size(); ++i)
    {
        const auto ch = map[i];
        if (escaped) { escaped = false; continue; }
        if (quoted && ch == L'\\') { escaped = true; continue; }
        if (ch == L'"') { quoted = !quoted; continue; }
        if (quoted) continue;
        if (ch == L'(') { if (depth == 1) start = i; ++depth; }
        else if (ch == L')' && --depth == 1)
        {
            std::wstring entry(map.substr(start, i - start + 1));
            const auto comma = entry.rfind(L',');
            if (comma == std::wstring::npos) throw std::runtime_error("tab_map_format");
            auto key = entry.substr(1, comma - 1);
            key.erase(std::remove_if(key.begin(), key.end(), [](auto c) { return c == L'"' || c == L' '; }), key.end());
            if (key == tab_id) continue;
            const auto index = std::stoi(entry.substr(comma + 1));
            if (removed_index >= 0 && index > removed_index)
                entry = entry.substr(0, comma + 1) + std::to_wstring(index - 1) + L")";
            entries.push_back(std::move(entry));
        }
    }
    std::wstring out = L"(";
    for (const auto& entry : entries) { if (out.size() > 1) out += L","; out += entry; }
    return out + L")";
}
}

UObject* Menu::control(const wchar_t* name)
{
    auto it = controls_.find(name);
    if (it != controls_.end()) return it->second;
    auto* c = object(view_, name);
    if (!c) throw std::runtime_error("widget_missing:" + utf8(name));
    controls_.emplace(name, c); return c;
}
std::wstring Menu::localized(LocalText text) const
{
    Call c(library(L"ProjectPBZ", L"EventPipelineManager"), L"Conv_LocTextToText");
    c[L"InLocText"].text(L"(Namespace=" + quote(text.space) + L",Key=" + quote(text.key) + L")");
    c.invoke();
    Call to_string(library(L"Engine", L"KismetTextLibrary"), L"Conv_TextToString");
    to_string[L"InText"].copy(c[L"ReturnValue"]); to_string.invoke();
    return static_cast<FString*>(to_string[L"ReturnValue"].data)->str();
}
std::wstring Menu::message(std::string_view key) const
{
    const auto& resource = language_.starts_with(L"zh") ? resources::zh_hans : resources::en;
    auto it = std::ranges::find(resource, key, &Message::id);
    if (it == resource.end()) throw std::runtime_error("message_missing");
    return std::wstring(it->value);
}
void Menu::label(const wchar_t* name, std::wstring_view value) { set_text(control(name), value); }
void Menu::focus(const wchar_t* name) { invoke(control(name), L"SetFocus"); }
std::wstring Menu::scene_name(size_t scene) const
{
    auto result = message("bossArena");
    const auto boss = std::ranges::find(resources::bosses, scene, &Boss::default_scene);
    const auto name = (boss != resources::bosses.end())
        ? boss_names_.at(boss - resources::bosses.begin())
        : localized(resources::scenes.at(scene).name);
    result.replace(result.find(L"{boss}"), 6, name);
    return result;
}
void Menu::image(const wchar_t* name, const wchar_t* path)
{
    auto [asset, fresh] = textures_.try_emplace(path, nullptr);
    if (fresh) asset->second = load_object(path);
    if (brushes_[name] == asset->second) return;
    Call brush(control(name), L"SetBrushFromTexture");
    brush[L"Texture"].set(asset->second);
    brush[L"bMatchSize"].text(L"True"); brush.invoke();
    brushes_[name] = asset->second;
}

void Menu::sync_navigation(bool selected)
{
    // RegisterTab does not initialize this game's authored SelectedImage.
    // The displayed page, rather than hover/focus, owns its selection highlight.
    Call appearance(nav_, L"DoSelectedApperaance");
    appearance[L"DoSelected"].text(selected ? L"True" : L"False"); appearance.invoke();
    Call visibility(object(nav_, L"SelectedImage"), L"SetVisibility");
    visibility[L"InVisibility"].text(selected ? L"HitTestInvisible" : L"Hidden"); visibility.invoke();
    set_text(object(nav_, L"Text"), message("tab"));
    navigation_dirty_ = false;
}

void Menu::attach(UObject* root, Session& state)
{
    if (root_ == root) return;
    if (root_) detach();
    root_ = root;
    root_handle_ = root;
    try
    {
        tab_list_ = object(root, L"TabList");
        switcher_ = object(root, L"VisibilitySwitcher");
        auto mapping = field(root, L"TabToSwitcherIndexMap").text();
        Call lang(library(L"Engine", L"KismetInternationalizationLibrary"), L"GetCurrentLanguage");
        lang.invoke(); language_ = static_cast<FString*>(lang[L"ReturnValue"].data)->str();
        auto* content_class = load_class(L"/Game/PhantomBladeArena/W_Arena.W_Arena_C");
        auto* button_class = load_class(L"/Game/PBZContent/UI/UINew/System/W_System_NavButton.W_System_NavButton_C");
        if (!content_class || !button_class) throw std::runtime_error("ui_asset_missing");
        Call create(library(L"UMG", L"WidgetBlueprintLibrary"), L"Create");
        create[L"WorldContextObject"].set(root); create[L"WidgetType"].set(content_class);
        view_ = create.invoke().result<UObject*>();
        if (!view_) throw std::runtime_error("widget_create_failed");
        view_handle_ = view_;
        auto index = Call(switcher_, L"GetChildrenCount").invoke().result<int32_t>();
        Call add(switcher_, L"AddChild"); add[L"Content"].set(view_); add.invoke();
        if (mapping.empty() || mapping.back() != L')') throw std::runtime_error("tab_map_format");
        mapping.pop_back();
        if (mapping.size() > 1) mapping += L",";
        mapping += L"(" + std::wstring(tab_id) + L"," + std::to_wstring(index) + L"))";
        field(root, L"TabToSwitcherIndexMap").text(mapping);

        int32_t tab_index = -1;
        const int32_t count = Call(tab_list_, L"GetTabCount").invoke().result<int32_t>();
        for (int32_t i = 0; i < count; ++i)
        {
            Call id(tab_list_, L"GetTabIdAtIndex"); id[L"Index"].set(i); id.invoke();
            if (id[L"ReturnValue"].text() == L"System") { tab_index = i + 1; break; }
        }
        if (tab_index < 0) throw std::runtime_error("system_tab_missing");
        Call reg(tab_list_, L"RegisterTab");
        reg[L"TabNameID"].text(tab_id); reg[L"ButtonWidgetType"].set(button_class);
        reg[L"ContentWidget"].set(view_); reg[L"TabIndex"].set(tab_index);
        if (!reg.invoke().result<bool>()) throw std::runtime_error("tab_registration_failed");
        Call button(tab_list_, L"GetTabButtonBaseByID"); button[L"TabNameID"].text(tab_id);
        nav_ = button.invoke().result<UObject*>();
        auto* nav_text = object(nav_, L"Text");
        // The native button defaults to a bell-menu localization key. Clear it
        // so its language decorator does not replace the arena title on focus.
        invoke(nav_text, L"SetLocText");
        set_text(nav_text, message("tab"));
        sync_navigation(Call(switcher_, L"GetActiveWidget").invoke().result<UObject*>() == view_);
        Call tree_path(library(L"Engine", L"KismetSystemLibrary"), L"GetPathName");
        tree_path[L"Object"].set(object(root, L"WidgetTree")); tree_path.invoke();
        const auto path = static_cast<FString*>(tree_path[L"ReturnValue"].data)->str() + L".WB_PBZTriggerBoundAction";
        action_bar_ = find(path.c_str());
        if (action_bar_) action_bar_visibility_ = field(action_bar_, L"Visibility").text();
        boss_names_.resize(resources::bosses.size());
        boss_chapters_.resize(resources::bosses.size());
        for (size_t i = 0; i < resources::bosses.size(); ++i)
        {
            boss_names_[i] = localized(resources::bosses[i].name);
            boss_chapters_[i] = (resources::bosses[i].chapter.key && resources::bosses[i].chapter.key[0])
                ? localized(resources::bosses[i].chapter)
                : L"";
        }
        render(state);
        // Preserve each authored text size while adopting the original game's font.
        auto* font_type = static_cast<UStruct*>(find(L"/Script/SlateCore.SlateFontInfo"));
        for (const auto& [name, widget] : controls_)
        {
            if (!widget->GetPropertyByNameInChain(L"Font")) continue;
            const auto old_size = field(font_type, field(widget, L"Font").data, L"Size").text();
            Call set(widget, L"SetFont");
            set[L"InFontInfo"].copy(field(nav_text, L"Font"));
            field(font_type, set[L"InFontInfo"].data, L"Size").text(old_size);
            set.invoke();
        }
    }
    catch (...)
    {
        try { detach(); } catch (...) { forget(); }
        throw;
    }
}
void Menu::detach()
{
    if (root_handle_.Get())
    {
        if (action_bar_ && selected_)
        {
            Call visibility(action_bar_, L"SetVisibility");
            visibility[L"InVisibility"].text(action_bar_visibility_); visibility.invoke();
        }
        int32_t index = -1;
        if (view_handle_.Get() && switcher_)
        {
            Call child(switcher_, L"GetChildIndex"); child[L"Content"].set(view_);
            index = child.invoke().result<int32_t>();
        }
        if (valid(tab_list_))
        {
            Call c(tab_list_, L"RemoveTab"); c[L"TabNameID"].text(tab_id); c.invoke();
        }
        if (view_handle_.Get()) invoke(view_, L"RemoveFromParent");
        auto map = field(root_, L"TabToSwitcherIndexMap");
        map.text(remove_tab_mapping(map.text(), index));
    }
    forget();
}
void Menu::forget()
{
    root_ = view_ = tab_list_ = switcher_ = nav_ = action_bar_ = nullptr;
    action_bar_visibility_.clear(); navigation_dirty_ = false;
    root_handle_.Reset(); view_handle_.Reset(); textures_.clear(); brushes_.clear();
    controls_.clear(); commands_.clear(); selected_ = false;
}
void Menu::event(UObject* object, UFunction* function)
{
    if (object == nav_)
    {
        navigation_dirty_ = true;
        return;
    }
    if (object != view_) return;
    const auto name = function->GetName();
    if (name.starts_with(L"Arena_")) commands_.push_back(name.substr(6));
    else if (name == L"BackInput") commands_.push_back(L"Back");
}
void Menu::tick(Session& state)
{
    if (!root_) return;
    if (!root_handle_.Get() || !view_handle_.Get()) { forget(); return; }
    auto* active = Call(switcher_, L"GetActiveWidget").invoke().result<UObject*>();
    const bool now = active == view_;
    if (now != selected_ || navigation_dirty_) sync_navigation(now);
    if (action_bar_ && now != selected_)
    {
        Call visibility(action_bar_, L"SetVisibility");
        visibility[L"InVisibility"].text(now ? L"Hidden" : action_bar_visibility_); visibility.invoke();
    }
    if (now && !selected_) { state.pause(); render(state); focus_primary(state); }
    selected_ = now;
}
void Menu::render(const Session& s, std::string_view error)
{
    if (!view_) return;
    auto replace_token = [](std::wstring text, std::wstring_view token, std::wstring_view value) {
        if (const auto pos = text.find(token); pos != std::wstring::npos)
            text.replace(pos, token.size(), value);
        return text;
    };
    const auto& pending_boss = resources::bosses.at(s.pending.boss);
    const auto& pending_chapter = boss_chapters_.at(s.pending.boss);
    label(L"OpponentHeading", pending_chapter.empty() ? message("opponents") : (message("opponents") + L" · " + pending_chapter));
    label(L"OpponentName", boss_names_.at(s.pending.boss));
    label(L"EnergyRule", message("energy"));
    label(L"LockHealthLabel", message(s.lock_health ? "lockOn" : "lockOff"));
    label(L"PendingStatus", message("pending"));
    visible(control(L"PendingStatus"), s.current && s.different());
    auto status = s.current ? message(s.phase == Phase::Ended ? "ended" : "paused") + L" · " + boss_names_.at(s.current->boss)
        + L" / " + scene_name(s.current->scene)
        + L" · HP " + std::to_wstring(s.current->config.max_health)
        + L" / +" + std::to_wstring(s.current->config.weapon_level) : L"";
    label(L"CurrentStatus", status);
    visible(control(L"CurrentStatus"), s.current.has_value());
    label(L"SceneName", scene_name(s.pending.scene));
    const bool standard = s.pending.scene == pending_boss.default_scene;
    label(L"SceneStatus", localized(resources::scenes.at(s.pending.scene).name) + L" · " + message(standard ? "default" : "changed"));
    label(L"ScenePickerHeading", message("changeScene"));
    label(L"ScenePreviewName", s.scene_preview ? scene_name(*s.scene_preview) : L"");
    label(L"ScenePreviewRegion", s.scene_preview ? localized(resources::scenes.at(*s.scene_preview).name) : L"");
    label(L"LoadoutHeading", message("loadoutHeading"));
    std::wstring loadout_hint;
    if (s.pending.config.mode == LoadoutMode::Recommended)
    {
        loadout_hint = replace_token(message("recommendedHint"), L"{chapter}", pending_chapter);
        loadout_hint = replace_token(std::move(loadout_hint), L"{health}", std::to_wstring(pending_boss.recommended_max_health));
        loadout_hint = replace_token(std::move(loadout_hint), L"{weapon}", std::to_wstring(pending_boss.recommended_weapon_level));
    }
    else if (s.pending.config.mode == LoadoutMode::Current)
    {
        loadout_hint = replace_token(message("currentHint"), L"{health}", std::to_wstring(s.pending.config.max_health));
        loadout_hint = replace_token(std::move(loadout_hint), L"{weapon}", std::to_wstring(s.pending.config.weapon_level));
    }
    else
    {
        loadout_hint = replace_token(message("customHint"), L"{health}", std::to_wstring(s.pending.config.max_health));
        loadout_hint = replace_token(std::move(loadout_hint), L"{weapon}", std::to_wstring(s.pending.config.weapon_level));
    }
    label(L"LoadoutHint", loadout_hint);
    const std::tuple<const wchar_t*, const char*, LoadoutMode> mode_chips[] = {
        {L"LoadoutCurrent", "loadoutCurrent", LoadoutMode::Current},
        {L"LoadoutRecommended", "loadoutRecommended", LoadoutMode::Recommended},
        {L"LoadoutCustom", "loadoutCustom", LoadoutMode::Custom}
    };
    for (auto [chip, key, mode] : mode_chips)
    {
        const auto text = message(key);
        const bool is_mode = s.pending.config.mode == mode;
        const std::wstring prefix(chip);
        label((prefix + L"Label").c_str(), text);
        label((prefix + L"SelectedLabel").c_str(), text);
        visible(control((prefix + L"Selected").c_str()), is_mode);
        visible(control((prefix + L"Label").c_str()), !is_mode);
        visible(control((prefix + L"SelectedLabel").c_str()), is_mode);
        enabled(control(chip), !s.busy());
    }
    label(L"MaxHealthLabel", message("maxHealthLabel"));
    label(L"HealthValue", std::to_wstring(s.pending.config.max_health));
    label(L"WeaponLevelLabel", message("weaponLevelLabel"));
    label(L"WeaponValue", L"+" + std::to_wstring(s.pending.config.weapon_level));
    for (auto step_btn : {L"HealthDown", L"HealthUp", L"WeaponDown", L"WeaponUp"})
        enabled(control(step_btn), !s.busy());
    const std::pair<const wchar_t*, const char*> labels[] = {
        {L"ChangeSceneLabel", "changeScene"}, {L"DefaultSceneLabel", "defaultScene"},
        {L"CancelSceneLabel", "cancelScene"}, {L"UseSceneLabel", "useScene"},
        {L"BackLabel", "back"}, {L"EndLabel", "end"}, {L"ResetLabel", "reset"}, {L"ContinueLabel", "continue"}
    };
    for (auto [widget, key] : labels) label(widget, message(key));
    label(L"StartLabel", message(s.current ? "startNew" : "start"));
    label(L"Error", error.empty() ? L"" : message(error));
    visible(control(L"Error"), !error.empty());
    for (size_t i = 0; i < resources::max_slots; ++i)
    {
        const auto name = L"Boss" + std::to_wstring(i);
        const bool has_boss = i < resources::bosses.size();
        visible(control(name.c_str()), has_boss);
        if (has_boss)
        {
            const bool boss_sel = (i == s.pending.boss);
            label((name + L"Label").c_str(), boss_names_[i]);
            label((name + L"SelectedLabel").c_str(), boss_names_[i]);
            label((name + L"SubLabel").c_str(), boss_chapters_[i]);
            label((name + L"SelectedSubLabel").c_str(), boss_chapters_[i]);
            visible(control((name + L"Selected").c_str()), boss_sel);
            visible(control((name + L"Label").c_str()), !boss_sel);
            visible(control((name + L"SelectedLabel").c_str()), boss_sel);
            visible(control((name + L"SubLabel").c_str()), !boss_sel);
            visible(control((name + L"SelectedSubLabel").c_str()), boss_sel);
            image((name + L"Image").c_str(), resources::bosses[i].portrait);
            enabled(control(name.c_str()), !s.scene_preview && !s.busy());
        }
        const auto scene = L"Scene" + std::to_wstring(i);
        const bool has_scene = i < resources::scenes.size();
        visible(control(scene.c_str()), has_scene);
        if (has_scene)
        {
            const auto scene_index = scene_at_slot(pending_boss.default_scene, i);
            const auto scene_label = scene_name(scene_index);
            auto scene_sub = localized(resources::scenes[scene_index].name);
            if (i == 0) scene_sub += L" · " + message("default");
            const bool scene_sel = (s.scene_preview ? (*s.scene_preview == scene_index) : (scene_index == s.pending.scene));
            label((scene + L"Label").c_str(), scene_label);
            label((scene + L"SelectedLabel").c_str(), scene_label);
            label((scene + L"SubLabel").c_str(), scene_sub);
            label((scene + L"SelectedSubLabel").c_str(), scene_sub);
            visible(control((scene + L"Selected").c_str()), scene_sel);
            visible(control((scene + L"Label").c_str()), !scene_sel);
            visible(control((scene + L"SelectedLabel").c_str()), scene_sel);
            visible(control((scene + L"SubLabel").c_str()), !scene_sel);
            visible(control((scene + L"SelectedSubLabel").c_str()), scene_sel);
            image((scene + L"Image").c_str(), resources::scenes[scene_index].preview);
        }
    }
    image(L"Portrait", pending_boss.portrait);
    image(L"SceneImage", resources::scenes[s.pending.scene].preview);
    if (s.scene_preview) image(L"ScenePreviewImage", resources::scenes[*s.scene_preview].preview);
    Call thumb(control(L"LockTrack"), L"SetHorizontalAlignment");
    thumb[L"InHorizontalAlignment"].text(s.lock_health ? L"HAlign_Right" : L"HAlign_Left"); thumb.invoke();
    Call track(control(L"LockTrack"), L"SetBrushColor");
    track[L"InBrushColor"].text(s.lock_health ? L"(R=0.40,G=0.06,B=0.03,A=1)" : L"(R=0.15,G=0.14,B=0.12,A=1)"); track.invoke();
    Call page(control(L"SceneSwitcher"), L"SetActiveWidgetIndex");
    page[L"Index"].set<int32_t>(s.scene_preview ? 1 : 0); page.invoke();
    visible(control(L"DefaultScene"), !standard);
    visible(control(L"Actions"), !s.scene_preview);
    visible(control(L"Back"), !s.practice_session);
    visible(control(L"End"), s.practice_session);
    visible(control(L"Reset"), s.current.has_value());
    visible(control(L"Continue"), s.continuable());
    visible(control(L"Start"), s.different());
    enabled(control(L"Actions"), !s.busy());
    enabled(control(L"SceneSwitcher"), !s.busy());
    enabled(control(L"LockHealth"), !s.busy());
}
void Menu::focus_primary(const Session& s)
{
    if (s.scene_preview)
        focus((L"Scene" + std::to_wstring(slot_for_scene(resources::bosses[s.pending.boss].default_scene, *s.scene_preview))).c_str());
    else if (s.continuable()) focus(L"Continue");
    else if (s.current) focus(L"Reset");
    else focus((L"Boss" + std::to_wstring(s.pending.boss)).c_str());
}
void Menu::show()
{
    Call c(root_, L"OpenByTabID"); c[L"TabId"].text(tab_id); c.invoke();
}
void Menu::close() { if (root_handle_.Get()) invoke(root_, L"PopWidget"); }
std::deque<std::wstring> Menu::take_commands() { auto commands = std::move(commands_); commands_.clear(); return commands; }
}
