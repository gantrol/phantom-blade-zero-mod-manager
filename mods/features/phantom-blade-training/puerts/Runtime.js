"use strict";
const E = require("./Engine"), { Battle } = require("./Battle"), View = require("./View"), Stats = require("./Stats");
const { UE, valid, text, t } = E;
const battle = new Battle(), entries = new WeakMap();
let timer, view, stats, templatePending;
function open(owner, fromTitle) {
    if (view || battle.busy || templatePending) return;
    battle.open(fromTitle);
    view = new View(battle, owner, fromTitle, () => { view = null; });
}
function detach(page) {
    const entry = entries.get(page); if (!entry) return;
    if (valid(entry.widget)) { entry.delegate.Remove(entry.callback); entry.widget.RemoveFromParent(); }
    if (valid(entry.root)) entry.root.RemoveFromParent(); entries.delete(page);
}
function titleEntry(page) {
    let entry = entries.get(page);
    if (!entry) {
        const donor = page.Settings, parent = donor?.GetParent();
        if (!valid(parent) || !page.ButtonGroup || typeof donor.SetButtonText !== "function") throw new Error("arena_title_interface_mismatch");
        const widget = UE.WidgetBlueprintLibrary.Create(page, donor.GetClass(), page.GetOwningPlayer());
        parent.AddChild(widget); widget.SetButtonText(text(t("tab")));
        widget.SetIsFocusable(true);
        const callback = () => { try { if (!page.AlreadyStartGame) open(page, true); } catch (error) { E.report(error); } };
        widget.OnButtonBaseClicked.Add(callback);
        entry = { widget, callback, delegate: widget.OnButtonBaseClicked }; entries.set(page, entry);
    }
    const group = page.ButtonGroup;
    const all = Array.from({ length: group.GetButtonCount() }, (_, i) => group.GetButtonBaseAtIndex(i));
    if (!all.includes(entry.widget)) { group.AddWidget(entry.widget); all.push(entry.widget); }
    all.forEach((button, i) => {
        button.SetNavigationRuleExplicit(UE.EUINavigation.Up, all[(i - 1 + all.length) % all.length]);
        button.SetNavigationRuleExplicit(UE.EUINavigation.Down, all[(i + 1) % all.length]);
    });
}
function characterEntry(page) {
    if (entries.has(page)) return;
    if (!valid(page.Overlay_32)) throw new Error("arena_character_overlay_missing");
    const root = UE.NewObject(UE.CanvasPanel.StaticClass(), page.WidgetTree); page.Overlay_32.AddChildToOverlay(root);
    root.SetVisibility(UE.ESlateVisibility.SelfHitTestInvisible);
    const overlay = UE.WidgetLayoutLibrary.SlotAsOverlaySlot(root);
    overlay.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill); overlay.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);
    const widget = UE.NewObject(UE.Button.StaticClass(), page.WidgetTree); root.AddChild(widget);
    widget.SetBackgroundColor(new UE.LinearColor(.20, .14, .105, 1));
    const label = UE.NewObject(UE.TextBlock.StaticClass(), page.WidgetTree); widget.AddChild(label); label.SetText(text(t("tab")));
    const font = label.Font; font.Size = 18; label.SetFont(font);
    const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget), anchors = new UE.Anchors();
    anchors.Minimum = anchors.Maximum = new UE.Vector2D(0, 1); slot.SetAnchors(anchors);
    slot.SetPosition(new UE.Vector2D(32, -78)); slot.SetSize(new UE.Vector2D(220, 44)); slot.SetZOrder(80);
    const callback = () => { try { open(page, false); } catch (error) { E.report(error); } };
    widget.OnClicked.Add(callback); entries.set(page, { widget, root, callback, delegate: widget.OnClicked });
}
function hook(type, name, after) {
    const proto = type.prototype, original = proto[name];
    if (typeof original !== "function") throw new Error("arena_interface_missing:" + name);
    proto[name] = function (...args) {
        const result = original.apply(this, args);
        try { after(this); } catch (error) { E.report(error); }
        return result;
    };
}
function tick() {
    try {
        if (templatePending) {
            const template = JSON.parse(E.cpp.loadModule("pbz-arena-v1/template/result"));
            if (template.pending) return;
            templatePending = false;
            if (template.error) E.report(template.error);
            else if (template.template) {
                const loadout = require("./Loadout"); loadout.setTemplate(template.template);
                battle.pending.config = loadout.profile(loadout.template(), "template");
            }
        }
        battle.tick();
        if (!battle.busy && battle.suspendedOwner) {
            const suspended = battle.suspendedOwner; battle.suspendedOwner = null;
            if (valid(suspended.owner) && suspended.enabled !== null) suspended.owner.SetIsEnabled(suspended.enabled);
        }
        if (view) { if (!valid(view.frame)) { view.clear(); view = null; } else if (view.dirty) view.render(); else view.refresh(); }
        if (battle.current?.practice !== "boss" && battle.current && !battle.busy && !battle.failure) {
            if (!stats || stats.player !== E.player()) { stats?.dispose(); stats = new Stats(battle); }
            stats.update();
        } else if (stats) { stats.dispose(); stats = null; }
    } catch (error) {
        if (templatePending) { templatePending = false; E.report(error); return; }
        battle.fail(error);
        const suspended = battle.suspendedOwner; battle.suspendedOwner = null;
        if (suspended && valid(suspended.owner) && suspended.enabled !== null) suspended.owner.SetIsEnabled(suspended.enabled);
        if (view) view.refresh();
        else if (!battle.active && suspended && valid(suspended.owner)) {
            try { open(suspended.owner, suspended.fromTitle); } catch (viewError) { E.report(viewError); }
        }
        else if (battle.active && valid(E.context())) {
            try { open(E.player() || E.context(), false); } catch (viewError) { E.report(viewError); }
        }
    }
}
function install() {
    if (timer !== undefined) return;
    const title = require("UI/Widget/StartMenu").StartmenuMinXinInterface;
    const character = require("UI/Widget/Character_V2").CharacterUI_V2MixinInterface;
    const ability = require("UI/Widget/AbilityWidget").abilityWidgetMixinInterface;
    hook(title, "BP_OnActivated", titleEntry); hook(title, "Destruct", detach);
    hook(character, "BP_InitWidget", characterEntry); hook(character, "FinishCurrentWidget", detach); hook(character, "Destruct", detach);
    require("./Skills").install(ability);
    const request = JSON.parse(E.cpp.loadModule("pbz-arena-v1/template/start"));
    if (request.error) E.report(request.error); else templatePending = true;
    timer = setInterval(tick, 100);
    console.log("[PhantomBladeArena] installed=PuerTS build=26367565 bridge=1");
}
module.exports = { install };
