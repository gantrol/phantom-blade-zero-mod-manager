"use strict";
const I = require("./Inventory");
const { UE, t } = I;

class View {
    constructor(page, actions) {
        this.page = page; this.actions = actions; this.bindings = []; this.roots = [];
        try {
        // WB_Character_V4 is rooted in an AspectRatioScaleBox. Keep the
        // native content intact and share its scaled overlay coordinate space.
        const overlay = page.Overlay_32;
        if (!overlay?.AddChildToOverlay) throw new Error("LOADOUTS_OVERLAY_MISMATCH");
        const parent = this.create(UE.CanvasPanel, overlay);
        this.canvas = parent; this.roots.push(parent);
        parent.SetVisibility(UE.ESlateVisibility.SelfHitTestInvisible);
        const canvasSlot = UE.WidgetLayoutLibrary.SlotAsOverlaySlot(parent);
        canvasSlot.SetHorizontalAlignment(UE.EHorizontalAlignment.HAlign_Fill);
        canvasSlot.SetVerticalAlignment(UE.EVerticalAlignment.VAlign_Fill);
        this.entry = this.button(parent, t("title"), actions.open);
        this.place(this.entry, 0, 0, 260, 42, 80);
        this.searchBar = this.create(UE.HorizontalBox, parent);
        this.visible(this.searchBar, false);
        this.place(this.searchBar, 0, 0, 520, 44, 80);
        this.search = this.input(this.searchBar, t("search.placeholder"), actions.search);
        const searchSlot = this.search.Slot, fill = new UE.SlateChildSize();
        fill.SizeRule = UE.ESlateSizeRule.Fill; fill.Value = 1; searchSlot.SetSize(fill);
        this.button(this.searchBar, t("search.clear"), () => this.search.SetText(this.text("")));

        this.panel = this.create(UE.Border, parent);
        this.panel.SetBrushColor(new UE.LinearColor(0.035, 0.031, 0.027, 0.98));
        this.panel.SetPadding(new UE.Margin(24, 18, 24, 18));
        this.place(this.panel, 0, 0, 1120, 680, 100);
        const body = this.create(UE.VerticalBox, this.panel);
        const header = this.create(UE.HorizontalBox, body);
        this.heading = this.label(header, t("title"), 24);
        this.button(header, t("action.close"), actions.close);
        this.presetSearch = this.input(body, t("search.presets"), actions.filter);
        const tabs = this.create(UE.HorizontalBox, body);
        this.button(tabs, t("tab.all"), () => actions.tab("all"));
        this.button(tabs, t("tab.progress"), () => actions.tab("progress"));
        const middle = this.create(UE.HorizontalBox, body);
        const middleSize = new UE.SlateChildSize(); middleSize.SizeRule = UE.ESlateSizeRule.Fill; middleSize.Value = 1;
        middle.Slot.SetSize(middleSize);
        const listSize = this.create(UE.SizeBox, middle); listSize.SetWidthOverride(310);
        this.list = this.create(UE.ScrollBox, listSize);
        this.details = this.create(UE.ScrollBox, middle);
        const detailSize = new UE.SlateChildSize(); detailSize.SizeRule = UE.ESlateSizeRule.Fill; detailSize.Value = 1;
        this.details.Slot.SetSize(detailSize); this.details.Slot.SetPadding(new UE.Margin(24, 0, 0, 0));
        this.description = this.label(this.details, "", 16); this.description.SetAutoWrapText(true);
        const footer = this.create(UE.HorizontalBox, body);
        this.medicines = this.button(footer, t("medicine.preserve"), actions.medicines);
        this.undo = this.button(footer, t("action.undo"), actions.undo);
        this.apply = this.button(footer, t("action.apply"), actions.apply);
        this.status = this.label(body, "", 14);
        this.visible(this.panel, false);
        } catch (error) { this.dispose(); throw error; }
    }
    text(value) { return UE.KismetTextLibrary.Conv_StringToText(String(value)); }
    create(type, parent) { const widget = UE.NewObject(type.StaticClass(), this.page); parent.AddChild(widget); return widget; }
    label(parent, value, size = 16) {
        const widget = this.create(UE.TextBlock, parent), font = widget.Font;
        font.Size = size; widget.SetFont(font); widget.SetText(this.text(value));
        const color = new UE.SlateColor(); color.SpecifiedColor = new UE.LinearColor(0.88, 0.84, 0.74, 1);
        widget.SetColorAndOpacity(color); widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        if (widget.Slot.SetPadding) widget.Slot.SetPadding(new UE.Margin(10, 8, 10, 8));
        return widget;
    }
    bind(delegate, callback, temporary = false) { delegate.Add(callback); this.bindings.push({ delegate, callback, temporary }); }
    button(parent, value, callback, temporary = false) {
        const button = this.create(UE.Button, parent);
        button.SetBackgroundColor(new UE.LinearColor(0.20, 0.14, 0.105, 1));
        this.label(button, value, 16);
        this.bind(button.OnClicked, callback, temporary);
        return button;
    }
    input(parent, hint, callback) {
        const input = this.create(UE.EditableTextBox, parent);
        input.SetHintText(this.text(hint)); input.SetClearKeyboardFocusOnCommit(false);
        if (input.Slot.SetPadding) input.Slot.SetPadding(new UE.Margin(0, 8, 0, 8));
        this.bind(input.OnTextChanged, value => callback(UE.KismetTextLibrary.Conv_TextToString(value)));
        return input;
    }
    place(widget, x, y, w, h, order) {
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        if (!slot) throw new Error("LOADOUTS_CANVAS_MISMATCH");
        slot.SetPosition(new UE.Vector2D(x, y)); slot.SetSize(new UE.Vector2D(w, h)); slot.SetZOrder(order);
    }
    visible(widget, visible) { widget.SetVisibility(visible ? UE.ESlateVisibility.Visible : UE.ESlateVisibility.Collapsed); }
    layout(searchVisible) {
        const geo = this.canvas.GetCachedGeometry(), size = UE.SlateBlueprintLibrary.GetLocalSize(geo);
        if (size.X <= 0 || size.Y <= 0) return;
        const width = Math.min(1120, size.X - 48), height = Math.min(680, size.Y - 100);
        this.place(this.panel, (size.X - width) / 2, (size.Y - height) / 2, width, height, 100);
        this.place(this.entry, size.X - 320, 18, 280, 44, 80);
        const target = this.page.W_SystemPanel_V1.GetCachedGeometry();
        const origin = UE.SlateBlueprintLibrary.AbsoluteToLocal(geo, UE.SlateBlueprintLibrary.LocalToAbsolute(target, new UE.Vector2D(0, 0)));
        this.place(this.searchBar, origin.X, Math.max(10, origin.Y - 50), Math.min(520, size.X - origin.X - 24), 44, 80);
        this.visible(this.entry, !this.opened && !this.page.IsSecondStep());
        this.visible(this.searchBar, !this.opened && searchVisible);
    }
    open(value) {
        this.opened = value; this.visible(this.panel, value);
        if (value) {
            this.previousEnabled = this.page.WidgetSwitcher_53.GetIsEnabled();
            this.page.WidgetSwitcher_53.SetIsEnabled(false);
        } else {
            this.page.WidgetSwitcher_53.SetIsEnabled(this.previousEnabled ?? true);
            this.entry.SetKeyboardFocus();
        }
    }
    focused() { return this.search.HasKeyboardFocus() || this.search.HasFocusedDescendants() || this.presetSearch.HasKeyboardFocus() || this.presetSearch.HasFocusedDescendants(); }
    presets(presets, selected, select) {
        this.unbind(true); this.list.ClearChildren();
        for (const preset of presets) {
            const button = this.button(this.list, (preset.id === selected ? "• " : "") + t(preset.labelKey), () => select(preset.id), true);
            button.SetToolTipText(this.text(t(preset.summaryKey)));
        }
    }
    preview(plan, undoAvailable) {
        const { snapshot, preset } = plan, lines = [t(preset.labelKey), t(preset.summaryKey), ""];
        for (const kind of ["main", "shadow", "accessories", "medicines"]) {
            lines.push(t("category." + kind));
            const ids = kind === "accessories" ? preset.equipment.accessories.itemIds : preset.equipment[kind];
            for (const id of ids) lines.push(id === null ? t("slot.preserve") : I.label(snapshot.backpack, kind, id));
            if (kind === "accessories" && ids.length < 6) lines.push(t("slot.preserveRemaining"));
            lines.push("");
        }
        lines.push(t(preset.rotationKey), "", t("guidance.actual"), "", t("preview.changes", { count: plan.differences.length }));
        for (const change of plan.differences) lines.push(t("category." + change.kind) + " " + (change.slot + 1) + ": "
            + I.label(snapshot.backpack, change.kind, change.before) + " → " + I.label(snapshot.backpack, change.kind, change.after));
        if (plan.missingPriority.length) lines.push(t("guidance.missingPriority", { items: plan.missingPriority.map(id => I.label(snapshot.backpack, "accessories", id)).join(" / ") }));
        for (const error of plan.errors) lines.push(t(error.key, { items: error.items.map(id => {
            const kind = ["main", "shadow", "medicines"].find(kind => preset.equipment[kind].includes(id)) || "accessories";
            return I.label(snapshot.backpack, kind, id);
        }).join(" / "), count: preset.requirements.accessorySlotsMin }));
        this.description.SetText(this.text(lines.join("\n")));
        this.apply.SetIsEnabled(!plan.errors.length && plan.steps.length > 0);
        this.undo.SetIsEnabled(undoAvailable);
        this.medicines.GetChildAt(0).SetText(this.text(plan.includeMedicines ? "✓ " + t("medicine.include") : t("medicine.preserve")));
        this.message(plan.errors.length ? "state.unavailable" : plan.steps.length ? "state.ready" : "state.unchanged");
    }
    message(key) { this.status.SetText(this.text(t(key))); }
    busy(value) { this.apply.SetIsEnabled(!value); this.undo.SetIsEnabled(!value); this.medicines.SetIsEnabled(!value); this.list.SetIsEnabled(!value); }
    unbind(temporaryOnly = false) {
        for (const binding of this.bindings) if (!temporaryOnly || binding.temporary) binding.delegate.Remove(binding.callback);
        this.bindings = temporaryOnly ? this.bindings.filter(binding => !binding.temporary) : [];
    }
    dispose() {
        if (this.opened) this.page.WidgetSwitcher_53.SetIsEnabled(this.previousEnabled ?? true);
        this.unbind(); for (const widget of this.roots) widget.RemoveFromParent(); this.roots = [];
    }
}
module.exports = View;
