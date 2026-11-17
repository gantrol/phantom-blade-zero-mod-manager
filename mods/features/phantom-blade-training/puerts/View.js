"use strict";
const E = require("./Engine"), L = require("./Loadout"), { catalog } = require("./Battle"), { catalog: practiceCatalog } = require("./Practice");
const { UE, cpp, t, text, valid } = E;
let frameClass, active;
function frameType() {
    if (frameClass) return frameClass;
    class PhantomBladeArenaFrame extends UE.UserWidget {
        OnKeyDown(geometry, event) {
            const key = String(UE.KismetInputLibrary.GetKey(event).KeyName);
            if (key === "Escape" || key === "Gamepad_FaceButton_Right") {
                if (active?.frame === this) active.close(); return UE.WidgetBlueprintLibrary.Handled();
            }
            return UE.WidgetBlueprintLibrary.Unhandled();
        }
    }
    Object.defineProperty(PhantomBladeArenaFrame, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
    return frameClass = cpp.makeUClass(PhantomBladeArenaFrame);
}
class View {
    constructor(battle, owner, fromTitle, onClose) {
        this.battle = battle; this.owner = owner; this.fromTitle = fromTitle; this.onClose = onClose; this.bindings = [];
        this.pc = UE.GameplayStatics.GetPlayerController(owner, 0); this.paused = UE.GameplayStatics.IsGamePaused(owner);
        this.ownerEnabled = typeof owner.GetIsEnabled === "function" ? owner.GetIsEnabled() : null;
        this.cursor = this.pc.bShowMouseCursor;
        this.frame = UE.WidgetBlueprintLibrary.Create(owner, frameType(), this.pc); active = this;
        this.root = UE.NewObject(UE.CanvasPanel.StaticClass(), this.frame.WidgetTree); this.frame.WidgetTree.RootWidget = this.root;
        this.panel = this.create(UE.Border, this.root); this.panel.SetBrushColor(new UE.LinearColor(.035, .031, .027, .98)); this.panel.SetPadding(new UE.Margin(24));
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.panel), anchors = new UE.Anchors(); anchors.Minimum = new UE.Vector2D(0, 0); anchors.Maximum = new UE.Vector2D(1, 1);
        slot.SetAnchors(anchors); slot.SetOffsets(new UE.Margin(36, 28, 36, 28));
        const body = this.create(UE.VerticalBox, this.panel), head = this.create(UE.HorizontalBox, body);
        this.label(head, t("tab"), 25); this.button(head, t("back"), () => this.close(), "header");
        this.status = this.label(body, "", 14);
        const columns = this.create(UE.HorizontalBox, body); this.fill(columns);
        const leftSize = this.create(UE.SizeBox, columns); leftSize.SetWidthOverride(310);
        const left = this.create(UE.VerticalBox, leftSize); this.search = this.input(left, t("bossSearch"), query => this.bosses(query));
        this.bossList = this.create(UE.ScrollBox, left); this.fill(this.bossList);
        const right = this.create(UE.ScrollBox, columns); this.fill(right); right.Slot.SetPadding(new UE.Margin(24, 0, 0, 0));
        this.details = this.create(UE.VerticalBox, right);
        this.footer = this.create(UE.HorizontalBox, body);
        this.continueButton = this.button(this.footer, t("continue"), () => this.close());
        this.resetButton = this.button(this.footer, t("reset"), () => this.transition(() => battle.start(true)));
        this.endButton = this.button(this.footer, t(fromTitle ? "endToTitle" : "end"), () => this.transition(() => battle.end()));
        this.startButton = this.button(this.footer, t(battle.current ? "startNew" : "start"), () => this.transition(() => battle.start()));
        this.bosses(""); this.render();
        this.frame.AddToViewport(100); this.frame.SetIsFocusable(true);
        if (!fromTitle && !this.paused) UE.GameplayStatics.SetGamePaused(owner, true);
        this.pc.bShowMouseCursor = true;
        UE.WidgetBlueprintLibrary.SetInputMode_UIOnlyEx(this.pc, this.frame, UE.EMouseLockMode.DoNotLock, false);
        this.startButton.SetKeyboardFocus();
        if (this.ownerEnabled !== null) owner.SetIsEnabled(false);
    }
    create(type, parent) { const widget = UE.NewObject(type.StaticClass(), this.frame.WidgetTree); parent.AddChild(widget); return widget; }
    fill(widget) { const size = new UE.SlateChildSize(); size.Value = 1; size.SizeRule = UE.ESlateSizeRule.Fill; widget.Slot.SetSize(size); }
    label(parent, value, size = 16) {
        const label = this.create(UE.TextBlock, parent), font = label.Font; font.Size = size;
        label.SetFont(font); label.SetText(text(value)); label.SetAutoWrapText(true);
        const color = new UE.SlateColor(); color.SpecifiedColor = new UE.LinearColor(.88, .84, .74, 1); label.SetColorAndOpacity(color);
        if (label.Slot.SetPadding) label.Slot.SetPadding(new UE.Margin(8, 6, 8, 6)); return label;
    }
    bind(delegate, fn, group = "details") {
        const callback = (...args) => { try { fn(...args); } catch (error) { E.report(error); this.status.SetText(text(t("actionFailed"))); } };
        delegate.Add(callback); this.bindings.push({ delegate, callback, group });
    }
    clear(group) {
        for (const binding of this.bindings.filter(b => !group || b.group === group)) binding.delegate.Remove(binding.callback);
        this.bindings = group ? this.bindings.filter(b => b.group !== group) : [];
    }
    button(parent, value, callback, group) {
        const button = this.create(UE.Button, parent); button.SetBackgroundColor(new UE.LinearColor(.20, .14, .105, 1));
        this.label(button, value); this.bind(button.OnClicked, callback, group || (parent === this.footer ? "footer" : parent === this.bossList ? "bosses" : "details"));
        if (button.Slot.SetPadding) button.Slot.SetPadding(new UE.Margin(3)); return button;
    }
    input(parent, hint, fn) {
        const input = this.create(UE.EditableTextBox, parent); input.SetHintText(text(hint));
        this.bind(input.OnTextChanged, value => fn(UE.KismetTextLibrary.Conv_TextToString(value)), "search"); return input;
    }
    row(label) { const row = this.create(UE.HorizontalBox, this.details); this.label(row, label); return row; }
    choice(label, choices, value, changed) {
        const row = this.row(label), combo = this.create(UE.ComboBoxString, row); this.fill(combo);
        const labels = choices.map(item => item.label); labels.forEach(label => combo.AddOption(label));
        combo.SetSelectedIndex(Math.max(0, choices.findIndex(item => item.id === value)));
        this.bind(combo.OnSelectionChanged, selected => {
            if (this.battle.busy) return;
            const item = choices[combo.GetSelectedIndex()]; if (item) { changed(item.id); this.dirty = true; }
        }); return combo;
    }
    number(label, value, min, max, step, changed, extra) {
        const row = this.row(label), field = this.create(UE.SpinBox, row);
        field.SetMinValue(min); field.SetMaxValue(max); field.SetMinSliderValue(min); field.SetMaxSliderValue(max);
        field.SetDelta(step); field.SetValue(value); this.fill(field);
        this.bind(field.OnValueCommitted, current => { if (!this.battle.busy) { changed(Math.round(current / step) * step); this.dirty = true; } });
        if (extra) this.button(row, extra.label, () => { extra.action(); this.dirty = true; });
    }
    bosses(query) {
        this.clear("bosses"); this.bossList.ClearChildren(); const needle = query.trim().toLocaleLowerCase();
        const bosses = catalog.bosses.filter(b => (E.local(b.name) + " " + E.local(b.chapter)).toLocaleLowerCase().includes(needle));
        for (const boss of bosses) this.button(this.bossList, E.local(boss.name) + " · " + E.local(boss.chapter), () => { this.battle.selectBoss(boss.id); this.dirty = true; }, "bosses");
        if (!bosses.length) this.label(this.bossList, t("searchEmpty"));
    }
    render() {
        this.clear("details"); this.details.ClearChildren();
        const battle = this.battle, selection = battle.pending, config = selection.config;
        this.choice(t("pending"), ["boss", "dummy", "group"].map(id => ({ id, label: t("practice" + id[0].toUpperCase() + id.slice(1)) })), selection.practice,
            id => { selection.practice = id; this.editCurrent = false; });
        const boss = catalog.bosses.find(b => b.id === selection.boss); this.label(this.details, E.local(boss.name), 22);
        this.choice(t("changeScene"), catalog.scenes.map(s => ({ id: s.id, label: E.local(catalog.bosses.find(b => b.id === s.bossId).name) + " · " + E.local(s.region) })), selection.scene, id => { selection.scene = id; });
        const sources = [{ id: "template", label: t("highCycleMode") }]; if (battle.playerBase) sources.unshift({ id: "player", label: t("loadoutCurrent") });
        this.choice(t("loadoutHeading"), sources, config.source, source => battle.useSource(source));
        this.label(this.details, config.source === "template" ? t(L.overridden() ? "highCycleOverrideHint" : "highCycleHint") : t("customHint"), 13);
        const pool = battle.pool();
        const stage = require("./Stages.generated").profiles.find(p => p.encounterBindings.bossFormIds.includes(selection.boss));
        if (stage) {
            const row = this.row(t(stage.labelKey) + " · " + t("stagePreview"));
            const proposed = stage.player.equipment.map(item => ({ id: item.itemId || "", level: item.level ?? 1 }));
            const allowed = proposed.every((weapon, i) => !weapon.id || pool.some(item => item.id === weapon.id
                && (item.kind === "main") === (i < 2) && weapon.level >= item.min && weapon.level <= item.max));
            this.button(row, t("copyStage"), () => {
                if (!allowed) return;
                config.weapons = E.copy(proposed); config.activeMain = config.activePhantom = 0;
                config.difficulty = 2; config.cycle = 0; this.dirty = true;
            }).SetIsEnabled(allowed);
            this.label(this.details, t(allowed ? "stagePreviewHint" : "stageCopyUnavailable"), 13);
        }
        for (let slot = 0; slot < 4; slot++) {
            const weapon = config.weapons[slot], other = config.weapons[slot ^ 1], main = slot < 2;
            const available = pool.filter(item => (item.kind === "main") === main && item.id !== other.id);
            const choices = available.map(item => ({ id: item.id, label: E.local(item.name) + " · " + (item.snapSoul ? t("soulUnlocked") : t("soulLocked")) }));
            if (slot !== 0 && (slot % 2 || !other.id)) choices.unshift({ id: "", label: t("emptyEquipment") });
            this.choice(t(["mainSlot1", "mainSlot2", "phantomSlot1", "phantomSlot2"][slot]), choices, weapon.id, id => {
                if (slot % 2 && !config.weapons[slot - 1].id) return;
                config.weapons[slot] = { id, level: available.find(item => item.id === id)?.level ?? 1 };
                if (!id) main ? config.activeMain = 0 : config.activePhantom = 0;
            });
            const item = pool.find(item => item.id === weapon.id);
            if (item) this.number(t("weaponLevelLabel"), weapon.level, item.min, item.max, 1, value => { weapon.level = value; });
        }
        this.number(t("maxHealthLabel"), config.maxHealth, 0, 100000, 50, value => { config.maxHealth = value; }, { label: t("healthAuto"), action: () => { config.maxHealth = 0; } });
        this.choice(t("gameDifficulty"), [0, 1, 2, 3, 4].map(id => ({ id, label: t("difficulty" + id) })), config.difficulty, id => { config.difficulty = id; });
        this.number(t("cycle"), config.cycle + 1, 1, 8, 1, value => { config.cycle = value - 1; });
        this.number(t("bossHealth"), config.bossHealth, 25, 1000, 25, value => { config.bossHealth = value; });
        this.number(t("bossAttack"), config.bossAttack, 0, 500, 25, value => { config.bossAttack = value; });
        if (selection.practice !== "boss") this.targets(selection);
        if (battle.current) {
            const row = this.row(t("energy")); this.button(row, t("energy"), () => ARefill(battle));
            this.button(row, t(battle.lockHealth ? "lockOn" : "lockOff"), () => { battle.setLock(!battle.lockHealth); this.dirty = true; });
            this.button(row, t("clearStats"), () => { battle.practice.clear(); this.dirty = true; });
            const s = battle.practice.sample;
            this.label(this.details, `${t("totalDamage")} ${s.damage.toFixed(1)} · DPS ${(s.elapsed > 0 ? s.damage / s.elapsed : 0).toFixed(1)}\n${t("lastMax")} ${s.last.toFixed(1)} / ${s.maximum.toFixed(1)} · ${t("hits")} ${s.hits}\n${t("allyDamage")} ${s.ally.toFixed(1)} · ${t("conversions")} ${s.conversions}`);
        }
        this.refresh(); this.dirty = false;
    }
    targets(pending) {
        const battle = this.battle;
        if (battle.current && battle.current.practice !== "boss") this.choice(t("targetSettings"),
            [{ id: false, label: t("targetEditPending") }, { id: true, label: t("targetEditCurrent") }], !!this.editCurrent, id => { this.editCurrent = id; });
        const selection = this.editCurrent ? battle.current : pending;
        if (!this.editCurrent && selection.practice === "group") {
            const row = this.row(t("targetingPresets"));
            for (const [id, key] of [["fan", "targetingFan"], ["depth", "targetingDepth"], ["mixed", "targetingMixed"], ["barrel", "targetingBarrelFront"]])
                this.button(row, t(key), () => { preset(selection, id); this.dirty = true; });
            this.label(this.details, t("targetingDraftRule"), 13);
        }
        const targets = selection.targets.slice(0, selection.practice === "dummy" ? 1 : 16);
        let target = targets.find(target => target.id === this.targetId) || targets[0]; this.targetId = target.id;
        this.choice(t("target"), targets.map(target => ({ id: target.id, label: "#" + target.id })), target.id, id => { this.targetId = id; });
        const change = (name, value) => {
            const next = E.copy(target); next[name] = value;
            if (name === "kind") next.motion = practiceCatalog.kinds.find(kind => kind.id === value).motions[0].id;
            if (this.editCurrent && !battle.practice.edit(next)) { this.status.SetText(text(t("targetEditFailed"))); return; }
            Object.assign(target, next); selection.fixedStart = false;
        };
        this.choice(t("targetKind"), practiceCatalog.kinds.map(kind => ({ id: kind.id, label: t(kind.labelKey) })), target.kind, id => change("kind", id));
        const kind = practiceCatalog.kinds.find(kind => kind.id === target.kind);
        this.choice(t("pose"), kind.motions.map(motion => ({ id: motion.id, label: t(motion.labelKey) })), target.motion, id => change("motion", id));
        this.number(t("poseProgress"), target.pose, 0, 100, 5, value => change("pose", value));
        for (const [name, key, min, max, step] of [["x", "targetX", -1500, 1500, 50], ["y", "targetY", -1500, 1500, 50], ["yaw", "targetYaw", 0, 359, 1]])
            this.number(t(key), target[name], min, max, step, value => change(name, value));
        if (selection.practice === "group") {
            const row = this.row(t("targetCount") + " " + targets.length);
            this.button(row, t("addTarget"), () => {
                if (targets.length >= 16) return;
                const id = Math.max(...selection.targets.map(t => t.id)) + 1, next = { ...target, id };
                for (let cell = 0; cell < 100; cell++) { next.x = Math.floor(cell / 5) * 250 - 500; next.y = cell % 5 * 250 - 500; if (!targets.some(t => Math.abs(t.x - next.x) < 150 && Math.abs(t.y - next.y) < 150)) break; }
                if (this.editCurrent && !battle.practice.add(next)) return;
                selection.targets.push(next); this.targetId = id; this.dirty = true;
            });
            this.button(row, t("removeTarget"), () => {
                if (targets.length <= 1 || this.editCurrent && !battle.practice.remove(target.id)) return;
                selection.targets = selection.targets.filter(t => t.id !== target.id); this.dirty = true;
            });
        }
        this.label(this.details, t(this.editCurrent ? "targetLiveHint" : "targetDraftHint"), 13);
    }
    refresh() {
        const battle = this.battle;
        this.status.SetText(text(battle.message ? t(battle.message) : battle.busy ? t(battle.phase) : t("startMenuSubtitle")));
        this.startButton.SetIsEnabled(!battle.busy && !!battle.pending.config.weapons[0].id && (!battle.failure || !battle.active));
        this.resetButton.SetIsEnabled(!battle.busy && !!battle.current && !battle.failure);
        this.endButton.SetIsEnabled(!battle.busy && battle.active);
        this.continueButton.SetIsEnabled(!battle.busy && !battle.failure);
        this.details.SetIsEnabled(!battle.busy); this.bossList.SetIsEnabled(!battle.busy);
    }
    transition(action) {
        action(); if (!this.battle.busy) { this.refresh(); return; }
        this.battle.suspendedOwner = { owner: this.owner, enabled: this.ownerEnabled, fromTitle: this.fromTitle };
        const owner = this.owner; this.close(true);
        if (!this.fromTitle) {
            const layer = UE.PBZBlueprintFunctionLibrary.GetUILayOut(owner)?.Layers.Get(E.Helper.constructGameplayTag("UI.Layer.GameSystem"));
            if (valid(layer)) layer.ClearWidgets();
        }
        UE.WidgetBlueprintLibrary.SetInputMode_GameOnly(this.pc, false); this.pc.bShowMouseCursor = false;
    }
    close(transition = false) {
        if (!transition && this.battle.busy) return;
        this.clear(); if (valid(this.frame)) this.frame.RemoveFromParent(); if (active === this) active = null;
        if (!transition && valid(this.owner) && this.ownerEnabled !== null) this.owner.SetIsEnabled(this.ownerEnabled);
        if (!this.fromTitle && !this.paused) UE.GameplayStatics.SetGamePaused(E.context(), false);
        this.pc.bShowMouseCursor = this.cursor;
        if (!transition && valid(this.owner) && typeof this.owner.SetFocus === "function") {
            UE.WidgetBlueprintLibrary.SetInputMode_UIOnlyEx(this.pc, this.owner, UE.EMouseLockMode.DoNotLock, false); this.owner.SetFocus();
        } else if (!transition) { UE.WidgetBlueprintLibrary.SetInputMode_GameOnly(this.pc, false); this.pc.bShowMouseCursor = false; }
        this.onClose();
    }
}
function ARefill(battle) { if (battle.current && !battle.busy) require("./Attributes").refill(E.player()); }
function preset(selection, id) {
    const positions = id === "depth" ? [[-250, 0, 180], [300, 0, 180], [0, -350, 150], [0, 350, 210]]
        : id === "mixed" ? [[0, -450, 150], [0, 0, 180], [0, 450, 210]]
        : id === "barrel" ? [[200, 0, 180], [0, -400, 150], [0, 400, 210]]
        : [[-107, -400, 150], [-27, -207, 165], [0, 0, 180], [-27, 207, 195], [-107, 400, 210]];
    selection.targets = positions.map(([x, y, yaw], i) => ({ id: i + 1, kind: "sword", motion: "idle", pose: 25, x, y, yaw }));
    selection.props = (id === "mixed" ? [[-250, -220], [-250, 220]] : id === "barrel" ? [[-300, 0]] : [])
        .map(([x, y], i) => ({ id: i + 1, kind: "explosive_barrel", x, y, yaw: 0 })); selection.fixedStart = true;
}
module.exports = View;
