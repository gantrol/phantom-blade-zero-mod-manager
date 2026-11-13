"use strict";
const { groups, traversalGroups } = require("./Filters");
const { rgba, layout } = require("./Appearance");
const Sound = require("./Sound");

const pages = ["items", "map", "traversal", "quests"];

class FilterPanel {
    constructor(view, action) {
        this.view = view;
        this.action = action;
        this.page = "items";
        this.height = layout.panelHeight;
        this.counts = {};
        const UE = view.UE;

        this.root = view.create(UE.Border, view.page.WidgetTree.RootWidget);
        this.root.SetPadding(new UE.Margin(0, 0, 0, 0));
        this.root.SetRenderTransformPivot(new UE.Vector2D(0, 0));
        this.root.SetBrushColor(new UE.LinearColor(0, 0, 0, 0));
        this.root.SetCursor(UE.EMouseCursor.Default);
        this.slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.root);
        this.slot.SetSize(new UE.Vector2D(layout.width, this.height));
        this.slot.SetZOrder(45);

        this.canvas = view.create(UE.CanvasPanel, this.root);
        view.makeInkSubstrate(this.canvas, layout.width, this.height, 36, 0.94);

        const title = view.makeText(this.canvas, 17, 18, 11, 240, 26, rgba("paper"));
        view.textValue(title, view.text.filters);
        view.makeButton(["Settings", "close"], 312, 7, 32, action, this.canvas);
        view.makeBrushLine(this.canvas, 16, 46, layout.width - 32, 1.8, ...rgba("gold", 0.45));

        this.tabs = {};
        this.tabCanvases = {};
        const tabDefs = [
            ["items", "filterItems", 16, 80],
            ["map", "filterMap", 100, 80],
            ["traversal", "filterTraversal", 184, 80],
            ["quests", "quest", 268, 72]
        ];
        for (const [key, label, x, w] of tabDefs) {
            const bg = view.makeRect(this.canvas, x, 50, w, 34, ...rgba("hover", 0.55));
            const text = view.makeText(this.canvas, 12, x, 56, w, 24, rgba("paper"));
            text.widget.SetJustification(UE.ETextJustify.Center);
            view.textValue(text, view.text[label]);
            const bar = view.makeRect(this.canvas, x + 10, 82, w - 20, 2, ...rgba("accent"));
            view.makeRowButton(x, 50, w, 34, "FilterTab:" + key, () => {
                if (this.page !== key) {
                    this.page = key;
                    this.focus = undefined;
                    Sound.hoverUI(view.page);
                    this.update(this.config, this.counts, this.isGamepad);
                }
            }, this.canvas);
            this.tabs[key] = { bg, text, bar };

            const sub = view.create(UE.CanvasPanel, this.canvas);
            const subSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(sub);
            subSlot.SetPosition(new UE.Vector2D(0, 0));
            subSlot.SetSize(new UE.Vector2D(layout.width, this.height));
            subSlot.SetZOrder(2);
            this.tabCanvases[key] = { widget: sub, slot: subSlot, interactive: false };
        }
        view.makeBrushLine(this.canvas, 16, 88, layout.width - 32, 1.2, ...rgba("gold", 0.30));

        // Items tab
        const itemsCanvas = this.tabCanvases.items.widget;
        this.itemsCommon = [
            this.pillRow(itemsCanvas, "remainingOnly", "remainingShort", 18, 98, 156),
            this.pillRow(itemsCanvas, "exploredOnly", "exploredOnly", 182, 98, 156)
        ];
        view.makeBrushLine(itemsCanvas, 18, 142, layout.width - 36, 1, ...rgba("line", 0.32));
        this.items = groups.map((g, i) => this.row(itemsCanvas, g.setting, g.label, 18, 150 + i * 48, 320, g.icon));
        view.makeBrushLine(itemsCanvas, 18, 446, layout.width - 36, 1.2, ...rgba("gold", 0.32));
        this.all = this.capsuleButton(itemsCanvas, "FilterAll", "remaining", "filterSelectAll", 18, 458, 156);
        this.reset = this.capsuleButton(itemsCanvas, "FilterReset", "reset", "filterReset", 182, 458, 156);

        // Map tab
        const mapCanvas = this.tabCanvases.map.widget;
        this.mapCommon = [
            this.pillRow(mapCanvas, "remainingOnly", "remainingShort", 18, 98, 156),
            this.pillRow(mapCanvas, "exploredOnly", "exploredOnly", 182, 98, 156)
        ];
        view.makeBrushLine(mapCanvas, 18, 142, layout.width - 36, 1, ...rgba("line", 0.32));
        this.map = ["showFloors", "showFog", "minimap", "npc", "treeHeart", "marker"].map((field, i) =>
            this.row(mapCanvas, field, field, 18, 150 + i * 48, 320, ["npc", "treeHeart", "marker"].includes(field) ? field : undefined)
        );

        // Traversal tab
        const travCanvas = this.tabCanvases.traversal.widget;
        this.traversalMaster = this.pillRow(travCanvas, "showTraversal", "showTraversal", 18, 98, 156);
        this.traversalExplored = this.pillRow(travCanvas, "exploredOnly", "exploredOnly", 182, 98, 156);
        view.makeBrushLine(travCanvas, 18, 142, layout.width - 36, 1, ...rgba("line", 0.32));
        this.traversal = traversalGroups.map((g, i) => this.row(travCanvas, g.setting, g.label, 18, 150 + i * 48, 320, g.icon));

        // Quests tab
        const questCanvas = this.tabCanvases.quests.widget;
        const questHeading = view.makeText(questCanvas, 13, 18, 102, 320, 24, rgba("gold"));
        view.textValue(questHeading, view.text.questGuidance);
        this.quests = require("./QuestChains").guidanceModes.map((mode, i) => {
            const key = "QuestGuidance:" + mode;
            const row = this.row(questCanvas, "questGuidance", "questGuidance_" + mode, 18, 136 + i * 48, 320, undefined, key);
            return { ...row, key, mode };
        });
        view.makeBrushLine(questCanvas, 18, 290, layout.width - 36, 1, ...rgba("line", 0.32));
        this.questHint = view.makeText(questCanvas, 11, 18, 304, 320, 110, rgba("muted"));
        this.questHint.widget.SetAutoWrapText(true);

        view.makeBrushLine(this.canvas, 18, this.height - 42, layout.width - 36, 1, ...rgba("line", 0.28));
        this.footerHint = view.makeText(this.canvas, 10, 18, this.height - 32, 320, 20, rgba("muted"));
    }

    capsuleButton(parent, key, icon, label, x, y, width) {
        const v = this.view;
        const bg = v.makeRect(parent, x, y, width, 36, ...rgba("wash", 0.92));
        const focusBg = v.makeRect(parent, x, y, width, 36, ...rgba("selected", 0.88));
        const border = v.makeRect(parent, x, y + 35, width, 1, ...rgba("gold", 0.35));
        const symbol = v.makeIconGraphic(parent, icon, x + 10, y + 8, 20, rgba("gold"));
        const text = v.makeText(parent, 11, x + 36, y + 8, width - 42, 22, rgba("paper"));
        v.textValue(text, v.text[label]);
        // Single button covering the capsule: no stacked child button
        const button = v.makeRowButton(x, y, width, 36, key, this.action, parent);
        return { key, button, text, symbol, bg, focusBg, border };
    }

    pillRow(parent, field, label, x, y, width) {
        return this.row(parent, field, label, x, y, width, undefined, "Setting:" + field, 36);
    }

    row(parent, field, label, x, y, width, icon, key = "Setting:" + field, height = 42) {
        const v = this.view;
        const bg = v.makeRect(parent, x, y, width, height, ...rgba("selected", 0.82));
        const rule = v.makeRect(parent, x + 4, y + height - 1, width - 8, 1, ...rgba("line", 0.18));
        const textOffset = icon ? 38 : 10;
        const countWidth = height >= 40 ? 64 : 0;
        const text = v.makeText(parent, 12, x + textOffset, y + Math.floor((height - 22) / 2), width - textOffset - 30 - countWidth, 22, rgba("paper"));
        v.textValue(text, v.text[label]);
        let pin;
        if (icon) {
            pin = v.makePinWidget(parent, false);
            v.place(pin, x + 18, y + Math.floor(height / 2));
            v.stylePin(pin, { category: icon, state: "remaining" }, false);
            v.visible(pin.text, false);
            v.visible(pin, true);
        }
        const count = countWidth ? v.makeText(parent, 10, x + width - 30 - countWidth, y + Math.floor((height - 18) / 2), countWidth - 4, 18, rgba("gold")) : undefined;
        if (count) count.widget.SetJustification(v.UE.ETextJustify.Right);

        const cx = x + width - 22, cy = y + Math.floor((height - 14) / 2);
        const border = [[[0, 0], [14, 0]], [[14, 0], [14, 14]], [[14, 14], [0, 14]], [[0, 14], [0, 0]]].map(([a, b]) => {
            const line = v.makeLineWidget(parent);
            v.drawLine(line, cx + a[0], cy + a[1], cx + b[0], cy + b[1], 1, ...rgba("muted"), false);
            return line;
        });
        const check = [[[2, 7], [6, 11]], [[6, 11], [13, 2]]].map(([a, b]) => {
            const line = v.makeLineWidget(parent);
            v.drawLine(line, cx + a[0], cy + a[1], cx + b[0], cy + b[1], 1.8, ...rgba("accent"), true);
            return line;
        });
        const button = v.makeRowButton(x, y, width, height, key, this.action, parent);
        return { field, key, button, text, count, pin, border, check, bg, rule };
    }

    activeRows() {
        if (this.page === "quests") return this.quests;
        if (this.page === "traversal") {
            return [this.traversalMaster, this.traversalExplored].concat(this.config?.showTraversal !== false ? this.traversal : []);
        }
        if (this.page === "map") return this.mapCommon.concat(this.map);
        return this.itemsCommon.concat(this.items, [this.all, this.reset]);
    }

    update(config, counts = this.counts, isGamepad = false) {
        this.config = config;
        this.counts = counts || {};
        this.isGamepad = !!isGamepad;
        const v = this.view;

        const allSelected = groups.every(g => config[g.setting] !== false);
        v.textValue(this.all.text, v.text[allSelected ? "filterSelectNone" : "filterSelectAll"]);
        v.textValue(this.footerHint, v.text[this.isGamepad ? "filterControlsPad" : "filterControlsKbm"]);

        for (const [key, tab] of Object.entries(this.tabs)) {
            const active = key === this.page;
            v.visible(tab.bar, active);
            v.visible(tab.bg, active);
            tab.text.widget.SetColorAndOpacity(v.color(...rgba(active ? "paper" : "muted")));
            const panel = this.tabCanvases[key];
            panel.widget.SetVisibility(active ? v.UE.ESlateVisibility.SelfHitTestInvisible : v.UE.ESlateVisibility.Collapsed);
        }

        const traversalEnabled = config.showTraversal !== false;
        if (this.page === "traversal" && !traversalEnabled && this.traversal.includes(this.focus)) {
            this.focus = this.traversalMaster;
        }
        v.textValue(this.questHint, v.text["questGuidanceTip_" + (config.questGuidance || "hints")]);

        const groupsToUpdate = [
            ...this.itemsCommon, ...this.items,
            ...this.mapCommon, ...this.map,
            this.traversalMaster, this.traversalExplored, ...this.traversal,
            ...this.quests
        ];

        for (const row of groupsToUpdate) {
            const isTravChild = this.traversal.includes(row);
            const enabled = !isTravChild || traversalEnabled;
            const checked = row.mode ? row.mode === (config.questGuidance || "hints") : config[row.field] !== false;
            row.button.widget.SetIsEnabled(enabled);
            if (row.pin) row.pin.widget.SetRenderOpacity(enabled ? (checked ? 1 : 0.45) : 0.25);
            v.visible(row.bg, enabled && this.focus === row);
            for (const node of row.check) v.visible(node, checked);
            row.text.widget.SetColorAndOpacity(v.color(...rgba(enabled && checked ? "paper" : "muted")));

            if (row.count) {
                const pair = this.counts[row.field];
                if (Array.isArray(pair)) {
                    const [rem, total] = pair;
                    v.textValue(row.count, total > 0 ? rem + "/" + total : "0");
                    row.count.widget.SetColorAndOpacity(v.color(...rgba(rem > 0 && enabled ? "gold" : "muted", enabled ? 0.92 : 0.45)));
                    v.visible(row.count, true);
                } else {
                    v.visible(row.count, false);
                }
            }
        }

        v.visible(this.all.focusBg, this.page === "items" && this.focus === this.all);
        v.visible(this.reset.focusBg, this.page === "items" && this.focus === this.reset);
    }

    wheel(delta) {
        if (!delta) return false;
        const idx = pages.indexOf(this.page);
        if (idx < 0) return false;
        const dir = delta > 0 ? -1 : 1;
        const next = Math.max(0, Math.min(pages.length - 1, idx + dir));
        if (next !== idx) {
            this.page = pages[next];
            this.focus = undefined;
            Sound.hoverUI(this.view.page);
            this.update(this.config, this.counts, this.isGamepad);
        }
        return true;
    }

    key(key) {
        if (["Escape", "Gamepad_FaceButton_Right"].includes(key)) {
            this.action("Settings");
            return true;
        }
        if (["Left", "Right", "Gamepad_DPad_Left", "Gamepad_DPad_Right", "Gamepad_LeftShoulder", "Gamepad_RightShoulder"].includes(key)) {
            const direction = ["Left", "Gamepad_DPad_Left", "Gamepad_LeftShoulder"].includes(key) ? -1 : 1;
            this.page = pages[(pages.indexOf(this.page) + direction + pages.length) % pages.length];
            this.focus = undefined;
            Sound.hoverUI(this.view.page);
            this.update(this.config, this.counts, true);
            return true;
        }
        const rows = this.activeRows();
        if (["Up", "Down", "Gamepad_DPad_Up", "Gamepad_DPad_Down"].includes(key)) {
            const direction = key === "Up" || key === "Gamepad_DPad_Up" ? -1 : 1;
            const index = rows.indexOf(this.focus);
            this.focus = rows[index < 0 ? 0 : (index + direction + rows.length) % rows.length];
            Sound.hoverUI(this.view.page);
            this.update(this.config, this.counts, true);
            return true;
        }
        if (["Enter", "SpaceBar", "Gamepad_FaceButton_Bottom"].includes(key)) {
            const row = rows.includes(this.focus) ? this.focus : rows[0];
            this.focus = row;
            Sound.clickPin(this.view.page);
            this.action(row.key || "Setting:" + row.field);
            return true;
        }
        return false;
    }

    resize() {
        const v = this.view;
        const scale = v.panelScale || layout.panelScale;
        const pos = v.panelPosition || { x: 0, y: 66 };
        this.root.SetRenderScale(new v.UE.Vector2D(scale, scale));
        this.slot.SetPosition(new v.UE.Vector2D(pos.x, pos.y));
    }

    show(open, config, counts, isGamepad) {
        this.root.SetVisibility(open ? this.view.UE.ESlateVisibility.Visible : this.view.UE.ESlateVisibility.Collapsed);
        if (open) {
            this.resize();
            this.update(config, counts, isGamepad);
        }
    }

    release() {
        this.items = this.map = this.itemsCommon = this.mapCommon = this.traversal = this.quests = [];
        this.tabs = {};
        this.tabCanvases = {};
        this.all = this.reset = this.focus = this.traversalMaster = this.traversalExplored = this.questHint = this.footerHint = undefined;
        this.config = this.counts = this.action = this.canvas = this.root = this.slot = this.view = undefined;
    }
}

module.exports = { FilterPanel };
