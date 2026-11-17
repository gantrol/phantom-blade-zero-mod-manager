#include "Menu.hpp"
#include "Resources.hpp"
#include "Loadout.hpp"
#include "PracticeCatalog.hpp"
#include <algorithm>
#include <iomanip>
#include <sstream>

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
    const auto& pending_boss = resources::bosses.at(s.pending.boss);
    const bool dummy = s.pending.practice != PracticeMode::Boss;
    const char* practice_keys[]{"practiceBoss", "practiceDummy", "practiceGroup"};
    for (auto [name, mode] : {std::pair{L"PracticeBoss", PracticeMode::Boss}, {L"PracticeDummy", PracticeMode::Dummy}, {L"PracticeGroup", PracticeMode::Group}}) {
        const std::wstring prefix(name);
        const bool selected = s.pending.practice == mode;
        label((prefix + L"Label").c_str(), message(practice_keys[static_cast<int>(mode)]));
        label((prefix + L"SelectedLabel").c_str(), message(practice_keys[static_cast<int>(mode)]));
        visible(control((prefix + L"Selected").c_str()), selected);
        visible(control((prefix + L"SelectedLabel").c_str()), selected);
        visible(control((prefix + L"Label").c_str()), !selected);
        enabled(control(name), !s.busy() && !s.scene_preview);
    }
    visible(control(L"BossScroll"), !dummy);
    visible(control(L"PracticeScroll"), dummy);
    label(L"PracticeHint", message("practiceHint"));
    const auto& target_selection = s.target_selection();
    const auto& target = s.selected_target_config();
    const auto& target_kind = practice_catalog::kind(target.kind);
    const bool edit_current = s.editing_current_targets();
    const bool editable_target = s.targets_editable();
    for (auto [name, key, selected] : {std::tuple{L"TargetEditCurrent", "targetEditCurrent", edit_current},
        {L"TargetEditPending", "targetEditPending", !edit_current}}) {
        const std::wstring prefix(name);
        label((prefix + L"Label").c_str(), message(key)); label((prefix + L"SelectedLabel").c_str(), message(key));
        visible(control((prefix + L"Selected").c_str()), selected);
        visible(control((prefix + L"SelectedLabel").c_str()), selected); visible(control((prefix + L"Label").c_str()), !selected);
        enabled(control(name), !s.busy() && !s.scene_preview && (name == std::wstring_view(L"TargetEditPending")
            || (s.current && s.current->practice != PracticeMode::Boss)));
    }
    label(L"TargetCountLabel", message("targetCount") + L" " + std::to_wstring(target_selection.target_count()) + L" / " + std::to_wstring(max_practice_targets));
    label(L"TargetValue", message("target") + L" #" + std::to_wstring(target.id));
    for (auto* name : {L"TargetPrev", L"TargetNext"}) enabled(control(name), editable_target && target_selection.target_count() > 1);
    label(L"AddTargetLabel", message("addTarget")); label(L"RemoveTargetLabel", message("removeTarget"));
    enabled(control(L"AddTarget"), editable_target && target_selection.practice == PracticeMode::Group && target_selection.targets.size() < max_practice_targets);
    enabled(control(L"RemoveTarget"), editable_target && target_selection.practice == PracticeMode::Group && target_selection.targets.size() > 1);
    label(L"TargetKindLabel", message("targetKind")); label(L"TargetKindValue", message(target_kind.label));
    label(L"TargetEligibility", message(target_kind.lute_controllable ? "targetControllable" : "targetNotControllable"));
    label(L"PoseLabel", message("pose"));
    label(L"PoseValue", message(practice_catalog::motion(target).label));
    label(L"PoseProgressLabel", message("poseProgress"));
    label(L"PoseProgressValue", std::to_wstring(target.pose_percent) + L"%");
    label(L"TargetXLabel", message("targetX")); label(L"TargetXValue", std::to_wstring(target.x) + L" cm");
    label(L"TargetYLabel", message("targetY")); label(L"TargetYValue", std::to_wstring(target.y) + L" cm");
    label(L"TargetYawLabel", message("targetYaw")); label(L"TargetYawValue", std::to_wstring(target.yaw) + L"°");
    for (auto* prefix : {L"TargetKind", L"Pose", L"PoseProgress", L"TargetX", L"TargetY", L"TargetYaw"})
        for (auto* suffix : {L"Down", L"Up"}) enabled(control((std::wstring(prefix) + suffix).c_str()), editable_target);
    enabled(control(L"PoseProgressDown"), editable_target && target.pose_percent > 0);
    enabled(control(L"PoseProgressUp"), editable_target && target.pose_percent < 100);
    label(L"TargetEditHint", message(edit_current ? "targetLiveHint" : "targetDraftHint"));
    label(L"ClearStatsLabel", message("clearStats"));
    enabled(control(L"ClearStats"), s.current && s.current->practice != PracticeMode::Boss && !s.busy());
    const auto number = [](double n) { std::wostringstream out; out << std::fixed << std::setprecision(1) << n; return out.str(); };
    const auto& sample = s.sample;
    std::wstring stats = message("groupStats") + L"\n" + message("sample") + L" " + std::to_wstring(s.sample_number)
        + L"\n" + message("totalDamage") + L" " + number(sample.damage)
        + L"\nDPS " + number(sample.elapsed > 0 ? sample.damage / sample.elapsed : 0) + L" · " + number(sample.elapsed) + L"s"
        + L"\n" + message("hits") + L" " + std::to_wstring(sample.hits)
        + L"\n" + message("lastMax") + L" " + number(sample.last) + L" / " + number(sample.maximum)
        + L"\n" + message("allyDamage") + L" " + number(sample.ally_damage)
        + L"\n" + message("conversions") + L" " + std::to_wstring(sample.conversions);
    if (sample.other_damage > 0) stats += L"\n" + message("otherDamage") + L" " + number(sample.other_damage);
    if (s.sample_number > 1) stats += L"\n" + message("previousDamage") + L" " + number(s.previous_sample.damage);
    label(L"PracticeStats", stats);
    const auto report = s.target_reports.find(target.id);
    const bool has_report = edit_current && report != s.target_reports.end();
    visible(control(L"TargetStats"), has_report);
    std::wstring target_status = message("targetDraftStatus");
    if (has_report) {
        const auto& r = report->second;
        const char* states[]{"targetSpawning", "targetFrozen", "targetRecovering", "targetControlled"};
        target_status = message(s.phase == Phase::Ended ? "ended" : states[static_cast<int>(r.state)]);
        label(L"TargetStats", message("target") + L" #" + std::to_wstring(target.id)
            + L"\n" + message("totalDamage") + L" " + number(r.sample.damage)
            + L" · DPS " + number(sample.elapsed > 0 ? r.sample.damage / sample.elapsed : 0)
            + L"\n" + message("lastMax") + L" " + number(r.sample.last) + L" / " + number(r.sample.maximum)
            + L"\n" + message("hits") + L" " + std::to_wstring(r.sample.hits)
            + L"\n" + message("targetAllyReceived") + L" " + number(r.sample.ally_damage)
            + L"\n" + message("targetAllyOutput") + L" " + number(r.ally_output)
            + L"\n" + message("conversions") + L" " + std::to_wstring(r.sample.conversions)
            + L"\n" + message("otherDamage") + L" " + number(r.sample.other_damage)
            + (s.sample_number > 1 ? L"\n" + message("previousDamage") + L" " + number(r.previous_sample.damage) : L""));
    }
    label(L"TargetStatus", target_status);
    label(L"OpponentHeading", message("opponents"));
    label(L"OpponentName", dummy ? message(practice_keys[static_cast<int>(s.pending.practice)]) : boss_names_.at(s.pending.boss));
    label(L"EnergyRule", message("energy"));
    label(L"LockHealthLabel", message(s.lock_health ? "lockOn" : "lockOff"));
    label(L"PendingStatus", message("pending"));
    visible(control(L"PendingStatus"), s.current && s.different());
    const char* difficulty_keys[] = {"difficulty0", "difficulty1", "difficulty2", "difficulty3", "difficulty4"};
    auto status = s.current ? message(s.phase == Phase::Ended ? "ended" : "paused") + L" · "
        + (s.current->practice == PracticeMode::Boss ? boss_names_.at(s.current->boss) : message(practice_keys[static_cast<int>(s.current->practice)]))
        + L" / " + scene_name(s.current->scene)
        + L" · " + (s.current->config.max_health ? L"HP " + std::to_wstring(s.current->config.max_health) : message("derivedHealth")) : L"";
    if (s.current) {
        const auto& c = s.current->config;
        status += L"\n" + message(difficulty_keys[std::clamp(c.difficulty < 0 ? s.player_difficulty : c.difficulty, 0, 4)])
            + L" · " + message("cycle") + L" " + std::to_wstring((c.cycle < 0 ? s.player_cycle : c.cycle) + 1)
            + L" · " + message("bossHealth") + L" " + std::to_wstring(c.boss_health_percent) + L"%"
            + L" · " + message("bossAttack") + L" " + std::to_wstring(c.boss_attack_percent) + L"%";
    }
    label(L"CurrentStatus", status);
    visible(control(L"CurrentStatus"), s.current.has_value());
    label(L"SceneName", scene_name(s.pending.scene));
    const bool standard = s.pending.scene == pending_boss.default_scene;
    label(L"SceneStatus", localized(resources::scenes.at(s.pending.scene).name) + L" · " + message(standard ? "default" : "changed"));
    label(L"ScenePickerHeading", message("changeScene"));
    label(L"ScenePreviewName", s.scene_preview ? scene_name(*s.scene_preview) : L"");
    label(L"ScenePreviewRegion", s.scene_preview ? localized(resources::scenes.at(*s.scene_preview).name) : L"");
    auto display = s.pending.config.weapons;
    auto heading = message("loadoutHeading");
    std::wstring hint;
    if (s.pending.config.mode == LoadoutMode::Stage) {
        display = {};
        const auto binding = std::ranges::find(resources::stage_bindings, std::string_view(pending_boss.id), &resources::StageBinding::boss_id);
        if (binding != resources::stage_bindings.end()) {
            const auto& stage = resources::stages.at(binding->stage);
            heading = message(stage.label) + L" · " + message("stagePreview");
            for (size_t i = 0; i < 4; ++i) display[i] = {stage.weapons[i].id, stage.weapons[i].level};
            hint = message(loadout::stage_equipment(s) ? "stagePreviewHint" : "stageCopyUnavailable");
        } else hint = message("stageMissingHint");
    } else if (s.pending.config.mode == LoadoutMode::Adaptive) {
        display = s.player_loadout.weapons;
        hint = message("adaptiveUnavailableHint");
    } else hint = message(!s.baseline_ready ? "loadoutNotReady"
        : s.pending.config.copied_stage_equipment ? "copiedStageHint" : "perSlotCustomHint");
    label(L"LoadoutHeading", heading);
    label(L"LoadoutHint", hint);
    const std::tuple<const wchar_t*, const char*, LoadoutMode> mode_chips[] = {
        {L"LoadoutRecommended", "stageMode", LoadoutMode::Stage},
        {L"LoadoutCurrent", "adaptiveMode", LoadoutMode::Adaptive},
        {L"LoadoutCustom", "loadoutCustom", LoadoutMode::Custom}
    };
    for (auto [chip, key, mode] : mode_chips) {
        const auto text = message(key);
        const bool selected = s.pending.config.mode == mode;
        const std::wstring prefix(chip);
        label((prefix + L"Label").c_str(), text);
        label((prefix + L"SelectedLabel").c_str(), text);
        visible(control((prefix + L"Selected").c_str()), selected);
        visible(control((prefix + L"Label").c_str()), !selected);
        visible(control((prefix + L"SelectedLabel").c_str()), selected);
        enabled(control(chip), !s.busy());
    }
    const char* slots[] = {"mainSlot1", "mainSlot2", "phantomSlot1", "phantomSlot2"};
    for (size_t i = 0; i < 4; ++i) {
        const auto prefix = L"Equip" + std::to_wstring(i);
        const auto& slot = display[i];
        const auto item = std::ranges::find_if(resources::equipment, [&](const auto& e) {
            return e.main == (i < 2) && slot.item_id == e.id;
        });
        const auto* original = s.owned(slot.item_id, i < 2);
        label((prefix + L"Slot").c_str(), message(slots[i]));
        label((prefix + L"Name").c_str(), slot.item_id.empty() ? message("emptyEquipment")
            : item == resources::equipment.end() ? message("unknownEquipment") : localized(item->name));
        label((prefix + L"Level").c_str(), slot.item_id.empty() ? L"—"
            : slot.level == 0 ? message("initialWeaponLevel") : L"Lv." + std::to_wstring(slot.level));
        label((prefix + L"State").c_str(), s.pending.config.mode == LoadoutMode::Stage ? L""
            : original ? message(original->snap_soul ? "soulUnlocked" : "soulLocked") : L"");
        const bool has_icon = item != resources::equipment.end() && item->icon[0];
        visible(control((prefix + L"Icon").c_str()), has_icon);
        if (has_icon) image((prefix + L"Icon").c_str(), item->icon);
        const bool can_choose = s.editable() && (i % 2 == 0 || !display[i-1].item_id.empty());
        enabled(control((prefix + L"Prev").c_str()), can_choose);
        enabled(control((prefix + L"Next").c_str()), can_choose);
        enabled(control((prefix + L"Down").c_str()), s.editable() && original && slot.level > original->min_level);
        enabled(control((prefix + L"Up").c_str()), s.editable() && original && slot.level < original->max_level);
    }
    label(L"UseCurrentLoadoutLabel", message("useCurrentLoadout"));
    enabled(control(L"UseCurrentLoadout"), !s.busy());
    label(L"MaxHealthLabel", message("maxHealthLabel"));
    label(L"HealthValue", s.pending.config.mode == LoadoutMode::Stage ? message("healthPending")
        : s.pending.config.max_health ? std::to_wstring(s.pending.config.max_health) : message("derivedHealth"));
    label(L"HealthAutoLabel", message("healthAuto"));
    for (auto button : {L"HealthDown", L"HealthUp", L"HealthAuto"}) enabled(control(button), s.editable());
    const auto& config = s.pending.config;
    const int difficulty = config.difficulty < 0 ? s.player_difficulty : config.difficulty;
    const int cycle = config.cycle < 0 ? s.player_cycle : config.cycle;
    label(L"DifficultyValue", message(difficulty_keys[std::clamp(difficulty, 0, 4)]));
    label(L"CycleValue", std::to_wstring(cycle + 1));
    label(L"BossHealthValue", std::to_wstring(config.boss_health_percent) + L"%");
    label(L"BossAttackValue", std::to_wstring(config.boss_attack_percent) + L"%");
    visible(control(L"BossSettings"), config.mode == LoadoutMode::Custom);
    const std::tuple<const wchar_t*, bool, bool> boss_controls[] = {
        {L"Difficulty", difficulty > 0, difficulty < 4}, {L"Cycle", cycle > 0, cycle < 6},
        {L"BossHealth", config.boss_health_percent > 25, config.boss_health_percent < 1000},
        {L"BossAttack", config.boss_attack_percent > 0, config.boss_attack_percent < 500}
    };
    for (auto [prefix, down, up] : boss_controls) {
        enabled(control((std::wstring(prefix) + L"Down").c_str()), s.editable() && down);
        enabled(control((std::wstring(prefix) + L"Up").c_str()), s.editable() && up);
    }
    enabled(control(L"BossDefaults"), s.editable());
    visible(control(L"CopyStage"), config.mode == LoadoutMode::Stage && !s.scene_preview);
    enabled(control(L"CopyStage"), loadout::stage_equipment(s).has_value());
    const std::pair<const wchar_t*, const char*> labels[] = {
        {L"ChangeSceneLabel", "changeScene"}, {L"DefaultSceneLabel", "defaultScene"},
        {L"CancelSceneLabel", "cancelScene"}, {L"UseSceneLabel", "useScene"},
        {L"BackLabel", "back"}, {L"EndLabel", "end"}, {L"ResetLabel", "reset"}, {L"ContinueLabel", "continue"},
        {L"CopyStageLabel", "copyStage"}, {L"BossHeading", "bossSettings"}, {L"BossHint", "bossHint"},
        {L"DifficultyLabel", "gameDifficulty"}, {L"CycleLabel", "cycle"},
        {L"BossHealthLabel", "bossHealth"}, {L"BossAttackLabel", "bossAttack"}, {L"BossDefaultsLabel", "bossDefaults"}
    };
    for (auto [widget, key] : labels) label(widget, message(key));
    if (dummy) {
        label(L"BossHeading", message("targetSettings"));
        label(L"BossHealthLabel", message("targetHealth"));
        label(L"BossAttackLabel", message("targetAttack"));
    }
    label(L"StartLabel", message(s.current ? "startNew" : "start"));
    label(L"Error", error.empty() ? L"" : message(error));
    visible(control(L"Error"), !error.empty());
    for (size_t i = 0; i < resources::max_slots; ++i)
    {
        const auto name = L"Boss" + std::to_wstring(i);
        const bool has_boss = i < resources::bosses.size();
        visible(control(name.c_str()), has_boss);
        const auto chapter_widget = L"BossChapter" + std::to_wstring(i);
        const bool chapter_start = has_boss && (i == 0 || boss_chapters_[i] != boss_chapters_[i-1]);
        visible(control(chapter_widget.c_str()), chapter_start);
        if (chapter_start) label(chapter_widget.c_str(), boss_chapters_[i]);
        if (has_boss)
        {
            const bool boss_sel = (i == s.pending.boss);
            label((name + L"Label").c_str(), boss_names_[i]);
            label((name + L"SelectedLabel").c_str(), boss_names_[i]);
            visible(control((name + L"Selected").c_str()), boss_sel);
            visible(control((name + L"Label").c_str()), !boss_sel);
            visible(control((name + L"SelectedLabel").c_str()), boss_sel);
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
    visible(control(L"PortraitFrame"), !dummy);
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
    visible(control(L"Start"), s.different() && config.mode == LoadoutMode::Custom);
    enabled(control(L"Actions"), !s.busy());
    enabled(control(L"Start"), s.startable());
    enabled(control(L"SceneSwitcher"), !s.busy());
    enabled(control(L"LockHealth"), !s.busy());
}
void Menu::focus_primary(const Session& s)
{
    if (s.scene_preview)
        focus((L"Scene" + std::to_wstring(slot_for_scene(resources::bosses[s.pending.boss].default_scene, *s.scene_preview))).c_str());
    else if (s.continuable()) focus(L"Continue");
    else if (s.current) focus(L"Reset");
    else if (s.pending.practice != PracticeMode::Boss) focus(L"Start");
    else focus((L"Boss" + std::to_wstring(s.pending.boss)).c_str());
}
void Menu::show()
{
    Call c(root_, L"OpenByTabID"); c[L"TabId"].text(tab_id); c.invoke();
}
void Menu::close() { if (root_handle_.Get()) invoke(root_, L"PopWidget"); }
std::deque<std::wstring> Menu::take_commands() { auto commands = std::move(commands_); commands_.clear(); return commands; }
}
