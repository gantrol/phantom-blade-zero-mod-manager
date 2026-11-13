"use strict";
const { paths } = require("./Icons");
const { layout, rgba } = require("./Appearance");
const { CanvasDrawing } = require("./CanvasDrawing");
const { FilterPanel } = require("./FilterPanel");
const { pinMass, collectNativeAnchors, resolveNativeMapOverlaps, arbitrateCanvasPin } = require("./PinLayout");
const Sound = require("./Sound");

const QUEST_EFFECT_WIDGET_PATH = "/Game/PBZContent/UI/InGame/Map/W_QuestFX.W_QuestFX_C";

class View extends CanvasDrawing {
    constructor(page, UE, cpp, text, action) {
        super(page, UE, cpp, text);
        this.action = action;
        this.pins = [];
        this.buttons = [];
        this.listeners = [];
        this.pending = [];
        this.cursor = 0;
        this.pinByID = new Map();
        this.pinWork = new Map();
        this.queuedPins = new Set();
        this.rawPositions = new Map();
        this.pinPositions = new Map();
        this.freePins = [];
        this.rows = [];
        this.panelPosition = { x: 0, y: 66 };
        this.panelScale = layout.panelScale;

        try {
            const root = page.WidgetTree.RootWidget;

            // Main fixed-frame sidebar (356 x 628)
            this.chrome = this.create(UE.Border, root);
            this.chrome.SetPadding(new UE.Margin(0, 0, 0, 0));
            this.chrome.SetBrushColor(new UE.LinearColor(0, 0, 0, 0));
            this.chrome.SetVisibility(UE.ESlateVisibility.Visible);
            this.chrome.SetCursor(UE.EMouseCursor.Default);
            this.chrome.SetRenderTransformPivot(new UE.Vector2D(0, 0));
            this.chromeSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.chrome);
            this.chromeSlot.SetAnchors(new UE.Anchors(new UE.Vector2D(0, 0), new UE.Vector2D(0, 0)));
            this.chromeSlot.SetAlignment(new UE.Vector2D(0, 0));
            this.chromeSlot.SetSize(new UE.Vector2D(layout.width, layout.panelHeight));
            this.chromeSlot.SetZOrder(44);

            // Collapsed edge tab so mouse users can always re-open the sidebar
            const tabBorder = this.create(UE.Border, root);
            tabBorder.SetPadding(new UE.Margin(0, 0, 0, 0));
            tabBorder.SetBrushColor(new UE.LinearColor(0, 0, 0, 0));
            tabBorder.SetCursor(UE.EMouseCursor.Default);
            tabBorder.SetRenderTransformPivot(new UE.Vector2D(0, 0));
            const tabSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(tabBorder);
            tabSlot.SetAnchors(new UE.Anchors(new UE.Vector2D(0, 0), new UE.Vector2D(0, 0)));
            tabSlot.SetAlignment(new UE.Vector2D(0, 0));
            tabSlot.SetSize(new UE.Vector2D(98, 34));
            tabSlot.SetZOrder(44);
            const tabCanvas = this.create(UE.CanvasPanel, tabBorder);
            this.makeRect(tabCanvas, 0, 0, 98, 34, ...rgba("panel", 0.92));
            this.makeBrushLine(tabCanvas, 4, 0, 90, 1.5, ...rgba("gold", 0.40));
            this.makeBrushLine(tabCanvas, 4, 32.5, 90, 1.5, ...rgba("gold", 0.32));
            this.makeIconGraphic(tabCanvas, "previous", 8, 9, 16, rgba("gold"));
            const tabLabel = this.makeText(tabCanvas, 12, 26, 6, 66, 22, rgba("paper"));
            this.textValue(tabLabel, text.expandPanel || text.journal);
            this.makeRowButton(0, 0, 98, 34, "F8", action, tabCanvas);
            this.collapsedTab = { widget: tabBorder, slot: tabSlot, interactive: true };
            this.visible(this.collapsedTab, false);

            this.canvas = this.create(UE.CanvasPanel, this.chrome);
            this.makeInkSubstrate(this.canvas, layout.width, layout.panelHeight, 36, 0.92);

            // Zone 1: Header (y = 0..46) — strictly non-overlapping intervals
            this.heading = this.makeText(this.canvas, 16, 16, 11, 138, 24, rgba("paper"));
            this.textValue(this.heading, text.journal);
            this.makeRect(this.canvas, 158, 8, 114, 30, ...rgba("ink", 0.58));
            this.nativeTab = this.makePillButton(this.canvas, 160, 9, 54, 28, "ViewNative", text.nativeShort, undefined, action);
            this.surveyTab = this.makePillButton(this.canvas, 216, 9, 54, 28, "ViewSurvey", text.surveyShort, undefined, action);
            this.settingsButton = this.makeButton(["Settings", "filter"], 278, 7, 32, action);
            this.filterDot = this.makeRect(this.canvas, 303, 10, 5, 5, ...rgba("gold"));
            this.visible(this.filterDot, false);
            this.collapseButton = this.makeButton(["F8", "next"], 314, 7, 32, action);
            this.headingRule = this.makeBrushLine(this.canvas, 14, 45, layout.width - 28, 1.6, ...rgba("gold", 0.45));

            // Zone 2: Mode Tabs & Mutually Exclusive Sub-Bars (y = 49..126)
            this.collectiblesTab = this.makePillButton(this.canvas, 16, 50, 158, 32, "CollectibleList", text.collectibles, "chest", action);
            this.questsTab = this.makePillButton(this.canvas, 182, 50, 158, 32, "QuestList", text.quest, "quest", action);

            this.collectibleSub = this.makeSubCanvas(this.canvas, layout.width, layout.panelHeight);
            this.remainingPill = this.makePillButton(this.collectibleSub.widget, 16, 88, 96, 30, "RemainingOnly", text.remainingShort, "remaining", action);
            this.minimapPill = this.makePillButton(this.collectibleSub.widget, 118, 88, 88, 30, "Minimap", text.minimap, "compass", action);
            this.markerPill = this.makePillButton(this.collectibleSub.widget, 212, 88, 84, 30, "F9", text.markerShort, "pin", action);
            this.locatePlayerTop = this.makeButton(["PlayerFloor", "center"], 306, 87, 32, action, this.collectibleSub.widget);

            this.questRootSub = this.makeSubCanvas(this.canvas, layout.width, layout.panelHeight);
            this.guidanceTabs = require("./QuestChains").guidanceModes.map((mode, i) =>
                this.makePillButton(this.questRootSub.widget, 16 + i * 110, 88, 104, 30, "QuestGuidance:" + mode, text["questGuidance_" + mode], undefined, action)
            );

            this.questChainSub = this.makeSubCanvas(this.canvas, layout.width, layout.panelHeight);
            this.questBackPill = this.makePillButton(this.questChainSub.widget, 16, 88, 88, 30, "QuestBack", text.questBack, "previous", action);
            this.questChainTitle = this.makeText(this.questChainSub.widget, 11, 110, 92, 230, 22, rgba("gold"));

            this.bodyRule = this.makeBrushLine(this.canvas, 14, 125, layout.width - 28, 1.4, ...rgba("gold", 0.36));

            // Zone 3: Fixed 6-Row List + Slim Scrollbar + Pager (y = 130..472)
            for (let i = 0; i < 6; i++) {
                const y = 130 + i * 51;
                const bg = this.makeRect(this.canvas, 14, y, 324, 49, ...rgba("selected", 0.84));
                const bar = this.makeRect(this.canvas, 14, y + 7, 3, 35, ...rgba("accent"));
                const icon = this.makePinWidget(this.canvas, false);
                this.place(icon, 36, y + 24);
                icon.slot.SetZOrder(2);
                const title = this.makeText(this.canvas, 13, 58, y + 5, 236, 22, rgba("paper"));
                const detail = this.makeText(this.canvas, 10, 58, y + 28, 236, 17, rgba("muted"));
                const rule = this.makeRect(this.canvas, 58, y + 48, 278, 1, ...rgba("line", 0.16));
                const btn = this.makeRowButton(14, y, 284, 49, "SelectRow:" + i, action);
                const track = this.makeButton(["TrackRow:" + i, "quest"], 304, y + 9, 30, action);
                const trackingBar = this.makeRect(this.canvas, 311, y + 41, 16, 2, ...rgba("gold"));
                const stepMark = this.makeText(this.canvas, 15, 25, y + 12, 22, 24, rgba("gold"));
                stepMark.widget.SetJustification(UE.ETextJustify.Center);
                const stepLine = this.makeRect(this.canvas, 35, y + 34, 1, 26, ...rgba("gold", 0.24));
                this.rows.push({ y, bg, bar, icon, title, detail, rule, btn, track, trackingBar, stepMark, stepLine });
            }

            this.scrollRail = this.makeRect(this.canvas, 345, 132, 2, 300, ...rgba("line", 0.22));
            this.scrollThumb = this.makeRect(this.canvas, 344.5, 132, 3, 50, ...rgba("gold", 0.72));

            this.emptyText = this.makeText(this.canvas, 12, 22, 160, 310, 56, rgba("muted"));
            this.emptyText.widget.SetAutoWrapText(true);

            this.summary = this.makeText(this.canvas, 11, 18, 444, 198, 20, rgba("gold"));
            this.previousPage = this.makeButton(["PagePrevious", "previous"], 222, 440, 28, action);
            this.pageInfo = this.makeText(this.canvas, 11, 252, 444, 56, 20, rgba("muted"));
            this.pageInfo.widget.SetJustification(UE.ETextJustify.Center);
            this.nextPage = this.makeButton(["PageNext", "next"], 310, 440, 28, action);

            // Zone 4: Fixed-Height Contextual Inspector Dock (y = 474..624)
            this.dockRule = this.makeBrushLine(this.canvas, 14, 474, layout.width - 28, 1.6, ...rgba("gold", 0.42));

            this.inspectorSub = this.makeSubCanvas(this.canvas, layout.width, layout.panelHeight);
            this.makeRect(this.inspectorSub.widget, 14, 480, 328, 138, ...rgba("wash", 0.54));
            this.navigationTitle = this.makeText(this.inspectorSub.widget, 13, 20, 485, 224, 22, rgba("paper"));
            this.overlapPill = this.makePillButton(this.inspectorSub.widget, 248, 483, 88, 24, "OverlapCycle", "", undefined, action);
            this.navigationDetail = this.makeText(this.inspectorSub.widget, 11, 20, 511, 316, 66, rgba("muted"));
            this.navigationDetail.widget.SetAutoWrapText(true);
            this.locateTargetPill = this.makePillButton(this.inspectorSub.widget, 18, 582, 68, 30, "TargetFloor", text.locateShort, "pin", action);
            this.secondaryNavPill = this.makePillButton(this.inspectorSub.widget, 92, 582, 116, 30, "SecondaryNavAction", "", undefined, action);
            this.primaryNavPill = this.makePillButton(this.inspectorSub.widget, 214, 582, 122, 30, "PrimaryNavAction", "", "quest", action);

            this.idleSub = this.makeSubCanvas(this.canvas, layout.width, layout.panelHeight);
            this.makeRect(this.idleSub.widget, 14, 482, 328, 58, ...rgba("wash", 0.46));
            this.autoTitle = this.makeText(this.idleSub.widget, 12, 22, 489, 222, 22, rgba("paper"));
            this.textValue(this.autoTitle, text.autoCollect);
            this.autoStatus = this.makeText(this.idleSub.widget, 10, 22, 512, 222, 18, rgba("muted"));
            this.autoTogglePill = this.makePillButton(this.idleSub.widget, 252, 495, 38, 32, "AutoCollect", "", "play", action);
            this.autoStopPill = this.makePillButton(this.idleSub.widget, 296, 495, 38, 32, "ClearTracking", "", "stop", action);
            this.idleStatus = this.makeText(this.idleSub.widget, 10, 18, 548, 320, 36, rgba("gold"));
            this.idleStatus.widget.SetAutoWrapText(true);
            this.controlsFooter = this.makeText(this.idleSub.widget, 10, 18, 594, 320, 20, rgba("muted"));

            this.resize();
            this.setPanel({ rows: [] }, false);
        } catch (error) {
            try { this.dispose(); } catch (cleanup) { console.warn("[PhantomBladeMap] " + String(cleanup)); }
            throw error;
        }
    }

    makeSubCanvas(parent, width, height) {
        const sub = this.create(this.UE.CanvasPanel, parent);
        const slot = this.UE.WidgetLayoutLibrary.SlotAsCanvasSlot(sub);
        slot.SetPosition(new this.UE.Vector2D(0, 0));
        slot.SetSize(new this.UE.Vector2D(width, height));
        slot.SetZOrder(2);
        return { widget: sub, slot };
    }

    setSubVisible(sub, show) {
        if (!sub?.widget) return;
        const vis = show ? this.UE.ESlateVisibility.SelfHitTestInvisible : this.UE.ESlateVisibility.Collapsed;
        if (sub.vis !== vis) {
            sub.widget.SetVisibility(vis);
            sub.vis = vis;
        }
    }

    makeIconGraphic(parent, icon, x, y, size, colorRgba = rgba("paper")) {
        const UE = this.UE, canvas = this.create(UE.CanvasPanel, parent);
        canvas.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(canvas);
        slot.SetPosition(new UE.Vector2D(x, y));
        slot.SetSize(new UE.Vector2D(size, size));
        const graphic = { widget: canvas, slot, size, lines: [] };
        this.setIconGraphic(graphic, icon, colorRgba);
        return graphic;
    }

    setIconGraphic(graphic, icon, colorRgba = rgba("paper")) {
        if (!graphic?.widget) return;
        const key = [icon || "", ...colorRgba].join(":");
        if (graphic.key === key) return;
        graphic.key = key;
        const shape = paths[icon] || [];
        const span = Math.max(12, graphic.size - 4);
        const inset = (graphic.size - span) * 0.5, scale = span / 24;
        let used = 0;
        for (const path of shape) {
            for (let i = 1; i < path.length; i++) {
                const a = path[i - 1], b = path[i];
                if (!graphic.lines[used]) graphic.lines[used] = this.makeLineWidget(graphic.widget);
                this.drawLine(
                    graphic.lines[used++],
                    a[0] * scale + inset, a[1] * scale + inset,
                    b[0] * scale + inset, b[1] * scale + inset,
                    1.35, colorRgba[0], colorRgba[1], colorRgba[2], colorRgba[3] ?? 1, true
                );
            }
        }
        for (; used < graphic.lines.length; used++) this.visible(graphic.lines[used], false);
    }

    makeRowButton(x, y, width, height, key, action, parent = this.canvas) {
        const UE = this.UE, button = this.create(UE.Button, parent);
        button.IsFocusable = false;
        const style = button.WidgetStyle;
        style.NormalPadding = new UE.Margin(0, 0, 0, 0);
        style.PressedPadding = new UE.Margin(0, 0, 0, 0);
        for (const [name, alpha] of [["Normal", 0], ["Hovered", 0.11], ["Pressed", 0.20], ["Disabled", 0]]) {
            const brush = style[name];
            brush.DrawAs = UE.ESlateBrushDrawType.Image;
            brush.TintColor = this.color(...rgba("paper", alpha));
            style[name] = brush;
        }
        button.SetStyle(style);
        button.SetBackgroundColor(new UE.LinearColor(1, 1, 1, 1));
        button.SetCursor(UE.EMouseCursor.Hand);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(button);
        slot.SetPosition(new UE.Vector2D(x, y));
        slot.SetSize(new UE.Vector2D(width, height));
        const handler = () => action(key);
        button.OnClicked.Add(handler);
        this.buttons.push({ button, handler, key });
        try {
            if (button.OnHovered?.Add) {
                const hoverHandler = () => Sound.hoverUI(this.page);
                button.OnHovered.Add(hoverHandler);
                this.listeners.push({ widget: button, name: "OnHovered", handler: hoverHandler });
            }
        } catch {}
        return { widget: button, slot, interactive: true };
    }

    makeButton([key, icon], x, y, size, action, parent = this.canvas) {
        const UE = this.UE, node = this.makeRowButton(x, y, size, size, key, action, parent);
        const box = this.create(UE.SizeBox, node.widget);
        box.SetWidthOverride(size);
        box.SetHeightOverride(size);
        const canvas = this.create(UE.CanvasPanel, box);
        canvas.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const graphic = { widget: canvas, size, lines: [] };
        const span = Math.min(20, size - 8), inset = (size - span) * 0.5, scale = span / 24;
        for (const path of (paths[icon] || [])) {
            for (let i = 1; i < path.length; i++) {
                const a = path[i - 1], b = path[i];
                const line = this.makeLineWidget(canvas);
                this.drawLine(line, a[0] * scale + inset, a[1] * scale + inset, b[0] * scale + inset, b[1] * scale + inset, 1.3, ...rgba("paper"));
                graphic.lines.push(line);
            }
        }
        node.graphic = graphic;
        return node;
    }

    makePillButton(parent, x, y, width, height, key, labelText, iconName, action) {
        const bg = this.makeRect(parent, x, y, width, height, ...rgba("wash", 0.86));
        const activeBg = this.makeRect(parent, x, y, width, height, ...rgba("selected", 0.90));
        const border = this.makeRect(parent, x, y + height - 1, width, 1, ...rgba("line", 0.34));
        const bar = this.makeRect(parent, x + 6, y + height - 2, Math.max(8, width - 12), 2, ...rgba("accent", 0.95));
        this.visible(activeBg, false);
        this.visible(bar, false);

        const iconOnly = !!iconName && !labelText && width <= 44;
        const iconSize = iconOnly ? 18 : 16;
        const icon = iconName
            ? this.makeIconGraphic(parent, iconName, iconOnly ? x + Math.floor((width - iconSize) * 0.5) : x + 7, y + Math.floor((height - iconSize) * 0.5), iconSize, rgba("paper"))
            : undefined;
        let label;
        if (!iconOnly) {
            const lx = iconName ? x + 25 : x + 4;
            const lw = Math.max(12, width - (iconName ? 29 : 8));
            label = this.makeText(parent, 11, lx, y + Math.floor((height - 20) * 0.5), lw, 20, rgba("paper"));
            if (!iconName) label.widget.SetJustification(this.UE.ETextJustify.Center);
            if (labelText) this.textValue(label, labelText);
        }
        const button = this.makeRowButton(x, y, width, height, key, action, parent);
        return { x, y, width, height, bg, activeBg, border, bar, icon, label, button, iconName };
    }

    stylePill(pill, opts = {}) {
        if (!pill) return;
        const show = opts.visible !== false;
        for (const part of [pill.bg, pill.border, pill.icon, pill.label, pill.button]) {
            if (part) this.visible(part, show);
        }
        if (!show) {
            this.visible(pill.activeBg, false);
            this.visible(pill.bar, false);
            return;
        }
        const enabled = opts.enabled !== false;
        const active = !!opts.active;
        const tone = opts.tone || (active ? "paper" : "muted");
        const barTone = opts.barTone || "accent";
        pill.button.widget.SetIsEnabled(enabled);
        this.visible(pill.activeBg, active);
        this.visible(pill.bar, active);
        pill.bar.widget.SetColorAndOpacity(new this.UE.LinearColor(...rgba(barTone, 0.95)));
        const fg = rgba(enabled ? tone : "muted", enabled ? 1 : 0.42);
        if (pill.label) {
            if (opts.text !== undefined) this.textValue(pill.label, opts.text);
            pill.label.widget.SetColorAndOpacity(this.color(...fg));
        }
        if (pill.icon) {
            this.setIconGraphic(pill.icon, opts.icon || pill.iconName, fg);
        }
    }

    showSettings(open, config, action, counts, isGamepad) {
        this.settingsOpen = open;
        if (open && !this.filterPanel) this.filterPanel = new FilterPanel(this, action);
        this.filterPanel?.show(open, config, counts, isGamepad);
        this.syncChromeVisibility();
    }

    filterKey(key) {
        return !!(this.settingsOpen && this.filterPanel?.key(key));
    }

    wheelFilter(delta) {
        return !!(this.settingsOpen && this.filterPanel?.wheel(delta));
    }

    setToggles(config, hasCustomFilter = false) {
        this.config = config;
        const isSurvey = config.view === "survey";
        this.stylePill(this.nativeTab, { active: !isSurvey, tone: !isSurvey ? "paper" : "muted" });
        this.stylePill(this.surveyTab, { active: isSurvey, tone: isSurvey ? "paper" : "muted" });
        this.stylePill(this.remainingPill, { active: !!config.remainingOnly, tone: config.remainingOnly ? "paper" : "muted" });
        this.stylePill(this.minimapPill, { active: !!config.minimap, tone: config.minimap ? "paper" : "muted" });
        this.stylePill(this.markerPill, { active: false, tone: "paper" });
        this.visible(this.filterDot, !!hasCustomFilter);
        this.syncChromeVisibility();
    }

    setListMode(mode) {
        this.listMode = mode;
        const isQuest = mode === "quest";
        this.stylePill(this.collectiblesTab, { active: !isQuest, tone: !isQuest ? "paper" : "muted" });
        this.stylePill(this.questsTab, { active: isQuest, tone: isQuest ? "paper" : "muted" });
    }

    resize() {
        const size = this.page.getMapPageLocalSize();
        if (!(size.X > 0 && size.Y > 0)) return;
        const scale = Math.max(0.65, Math.min(layout.panelScale, (size.Y - 124) / layout.panelHeight, (size.X - 48) / layout.width));
        const x = size.X - layout.width * scale - 20;
        const y = Math.max(62, (size.Y - layout.panelHeight * scale) * 0.5);
        if (size.X !== this.viewportWidth || size.Y !== this.viewportHeight || scale !== this.panelScale) {
            this.viewportWidth = size.X;
            this.viewportHeight = size.Y;
            this.panelScale = scale;
            this.panelPosition = { x, y };
            this.chrome.SetRenderScale(new this.UE.Vector2D(scale, scale));
            this.chromeSlot.SetPosition(new this.UE.Vector2D(x, y));
            this.collapsedTab.widget.SetRenderScale(new this.UE.Vector2D(scale, scale));
            this.collapsedTab.slot.SetPosition(new this.UE.Vector2D(size.X - 98 * scale - 20, 66));
        }
        if (this.settingsOpen) this.filterPanel?.resize();
    }

    sidebarLeft() {
        const showFull = this.settingsOpen || (this.controlsAllowed && this.config?.visibility && this.config?.showList);
        return showFull ? this.panelPosition.x : (this.viewportWidth || this.page.getMapPageLocalSize().X);
    }

    isMouseInSidebar() {
        const showFull = this.settingsOpen || (this.controlsAllowed && this.config?.visibility && this.config?.showList);
        if (!showFull) return false;
        const mp = this.page.getMouseLocalPosition?.();
        if (!mp) return false;
        const { x, y } = this.panelPosition, scale = this.panelScale || 1;
        return mp.X >= x - 12 && mp.X <= x + layout.width * scale + 4 && mp.Y >= y && mp.Y <= y + layout.panelHeight * scale;
    }

    setPanel(model, visible, heading = this.text.remaining, status = "", isGamepad = false) {
        this.panelDesired = !!visible;
        this.syncChromeVisibility();
        if (!visible) return;

        this.textValue(this.heading, model?.regionLabel || this.text.journal);

        // Mode tab counts
        const colBadge = model?.collectibleCountText ? this.text.collectibles + " · " + model.collectibleCountText : this.text.collectibles;
        const questBadge = model?.questCountText ? this.text.quest + " · " + model.questCountText : this.text.quest;
        const questMode = model?.listMode === "quest";
        this.stylePill(this.collectiblesTab, { active: !questMode, tone: !questMode ? "paper" : "muted", text: colBadge });
        this.stylePill(this.questsTab, { active: questMode, tone: questMode ? "paper" : "muted", text: questBadge });

        // Mutually exclusive sub-bars
        const chain = model?.questChain;
        const hasQuestBack = questMode && (!!chain || !!model?.questReturn);
        this.setSubVisible(this.collectibleSub, !questMode);
        this.setSubVisible(this.questRootSub, questMode && !hasQuestBack);
        this.setSubVisible(this.questChainSub, hasQuestBack);

        if (!questMode) {
            this.locatePlayerTop.widget.SetIsEnabled(model?.playerLocal !== false);
        } else if (hasQuestBack) {
            const backLabel = model?.questReturn && !chain ? this.text.questReturnTarget : this.text.questBack;
            this.stylePill(this.questBackPill, { active: false, tone: "paper", text: backLabel, icon: "previous" });
            this.textValue(this.questChainTitle, model?.questOverview || heading);
        } else {
            const curGuidance = this.config?.questGuidance || "hints";
            require("./QuestChains").guidanceModes.forEach((mode, i) => {
                const active = mode === curGuidance;
                this.stylePill(this.guidanceTabs[i], { active, tone: active ? "paper" : "muted" });
            });
        }

        // 6-Row List
        const rows = model?.rows || [];
        for (let i = 0; i < this.rows.length; i++) {
            const row = this.rows[i], item = rows[i], show = !!item;
            for (const name of ["icon", "title", "detail", "rule", "btn"]) this.visible(row[name], show);
            const step = questMode && item?.step;
            this.visible(row.stepMark, show && !!step);
            this.visible(row.stepLine, show && !!step && !!rows[i + 1]?.step);
            if (step) {
                this.visible(row.icon, false);
                this.textValue(row.stepMark, step.status === "completed" ? "✓" : step.status === "active" ? "◆" : "◇");
                row.stepMark.widget.SetColorAndOpacity(this.color(...rgba(step.status === "active" ? "accent" : step.status === "completed" ? "gold" : "muted")));
            }
            const showTrack = show && (!questMode ? (item.canTrack || item.tracked) : (item.canTrack || item.tracked));
            this.visible(row.bg, show && item.selected);
            this.visible(row.bar, show && item.selected);
            this.visible(row.track, showTrack);
            this.visible(row.trackingBar, show && item.tracked);

            if (show) {
                // Non-overlapping row click box vs track button
                const textW = showTrack ? 236 : 276;
                const btnW = showTrack ? 284 : 324;
                row.title.slot.SetSize(new this.UE.Vector2D(textW, 22));
                row.detail.slot.SetSize(new this.UE.Vector2D(textW, 17));
                row.btn.slot.SetSize(new this.UE.Vector2D(btnW, 49));
                row.track.widget.SetIsEnabled(item.canTrack !== false || item.tracked);

                if (!step) {
                    this.stylePin(row.icon, item, item.selected, 1, item.tracked, false);
                    this.visible(row.icon.text, false);
                }
                this.textValue(row.title, item.title);
                const muted = ["completed", "future"].includes(item.step?.status) || item.reward?.status === "claimed";
                row.title.widget.SetColorAndOpacity(this.color(...rgba(item.tracked ? "gold" : item.selected || !muted ? "paper" : "muted")));
                this.textValue(row.detail, item.detail);
            }
        }

        this.textValue(this.emptyText, model?.emptyText || this.text.noRemaining);
        this.visible(this.emptyText, !rows.length);

        // Scrollbar & Summary Pager
        const totalItems = model?.totalEntries || rows.length;
        const pageOffset = model?.pageOffset || 0;
        const hasPaging = !!(model?.hasPreviousPage || model?.hasNextPage);
        this.visible(this.scrollRail, totalItems > 6);
        this.visible(this.scrollThumb, totalItems > 6);
        if (totalItems > 6) {
            const railH = 300;
            const thumbH = Math.max(28, Math.round(railH * (6 / totalItems)));
            const maxOff = Math.max(1, totalItems - 6);
            const thumbY = 132 + Math.round((railH - thumbH) * Math.min(1, pageOffset / maxOff));
            this.scrollThumb.slot.SetPosition(new this.UE.Vector2D(344.5, thumbY));
            this.scrollThumb.slot.SetSize(new this.UE.Vector2D(3, thumbH));
        }

        this.textValue(this.summary, heading);
        this.textValue(this.pageInfo, model?.pageText || "");
        this.previousPage.widget.SetIsEnabled(!!model?.hasPreviousPage);
        this.nextPage.widget.SetIsEnabled(!!model?.hasNextPage);
        for (const node of [this.previousPage, this.nextPage, this.pageInfo]) this.visible(node, hasPaging);

        // Contextual Inspector Dock vs Idle Dock (mutually exclusive)
        const nav = model?.navigation;
        this.setSubVisible(this.inspectorSub, !!nav);
        this.setSubVisible(this.idleSub, !nav);

        if (nav) {
            const hasOverlap = (nav.overlapTotal || 0) > 1;
            this.navigationTitle.slot.SetSize(new this.UE.Vector2D(hasOverlap ? 222 : 316, 22));
            this.textValue(this.navigationTitle, nav.title || "");
            this.navigationTitle.widget.SetColorAndOpacity(this.color(...rgba(nav.canTeleport || nav.tracked ? "gold" : "paper")));

            if (hasOverlap) {
                const label = (this.text.overlapSwitch || "{0}/{1}")
                    .replace("{0}", String(nav.overlapIndex || 1))
                    .replace("{1}", String(nav.overlapTotal));
                this.stylePill(this.overlapPill, { visible: true, active: true, tone: "gold", barTone: "gold", text: label });
            } else {
                this.stylePill(this.overlapPill, { visible: false });
            }

            this.textValue(this.navigationDetail, nav.detail || "");
            this.stylePill(this.locateTargetPill, { visible: true, active: false, tone: "paper", enabled: !!nav.targetLocated });

            if (nav.prerequisites) {
                this.stylePill(this.secondaryNavPill, { visible: true, active: false, tone: "gold", text: this.text.questOpenPrerequisite });
            } else if (nav.canOpenQuestChain) {
                this.stylePill(this.secondaryNavPill, { visible: true, active: false, tone: "gold", text: this.text.openQuestChain });
            } else {
                this.stylePill(this.secondaryNavPill, { visible: false });
            }

            const showPrimary = !!(nav.canTeleport || nav.tracked || nav.canTrack);
            if (showPrimary) {
                const pIcon = nav.canTeleport ? "route" : nav.tracked ? "stop" : "quest";
                const pText = nav.canTeleport ? this.text.bellTeleport : nav.tracked ? this.text.clearTracking : this.text.trackingTarget;
                const pTone = nav.canTeleport || !nav.tracked ? "gold" : "paper";
                const pBar = nav.tracked ? "accent" : "gold";
                this.stylePill(this.primaryNavPill, { visible: true, active: true, tone: pTone, barTone: pBar, text: pText, icon: pIcon, enabled: true });
            } else {
                this.stylePill(this.primaryNavPill, { visible: false });
            }
        } else {
            const automatic = model?.autoCollect || { active: !!this.config?.autoCollect };
            this.autoTitle.widget.SetColorAndOpacity(this.color(...rgba(automatic.active ? "accent" : "paper")));
            this.textValue(this.autoStatus, automatic.status || (automatic.active ? this.text.autoCollectRunning : this.text.autoCollectStart));
            this.stylePill(this.autoTogglePill, {
                visible: true,
                active: !!automatic.active,
                tone: automatic.active ? "gold" : "paper",
                barTone: "gold",
                icon: automatic.active ? "pause" : "play"
            });
            this.stylePill(this.autoStopPill, {
                visible: true,
                active: false,
                tone: "paper",
                icon: "stop",
                enabled: !!(automatic.active || automatic.tracked)
            });

            const hintLine = status || this.text[isGamepad ? "inspectHintPad" : "inspectHintKbm"] || "";
            this.textValue(this.idleStatus, hintLine);
            this.idleStatus.widget.SetColorAndOpacity(this.color(...rgba(status ? "gold" : "muted")));
            this.textValue(this.controlsFooter, this.text[isGamepad ? "controlsPad" : "controlsKbm"] || this.text.controls);
        }
    }

    currentZoomScale() {
        return Math.max(0.65, Math.min(1.15, typeof this.page?.getElementZoomScale === "function" ? this.page.getElementZoomScale() : 1));
    }

    setPins(points, settings, selected, trackedID, region) {
        if (settings.view !== "native") {
            this.resetPins();
            return;
        }
        const allowed = new Set(Array.from(this.page.getallowedMapID(), id => String(id).toLowerCase()));
        const pending = points
            .filter(p => allowed.has(String(p.mapID).toLowerCase()) && (!settings.remainingOnly || p.state !== "collected"))
            .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
        const size = this.page.CurrentMapSize();
        const layoutKey = [region, size.X, size.Y].join(":");
        if (layoutKey !== this.pinLayoutKey) {
            this.pinLayoutKey = layoutKey;
            this.rawPositions.clear();
            this.pinPositions.clear();
            this.lastMapOffsetX = this.lastMapOffsetY = undefined;
            this.overlapDirty = true;
        }
        const active = new Set(pending.map(p => p.id));
        for (const [id, node] of this.pinByID) {
            if (!active.has(id)) {
                this.visible(node, false);
                this.pinByID.delete(id);
                this.rawPositions.delete(id);
                this.pinPositions.delete(id);
                this.freePins.push(node);
                this.overlapDirty = true;
            }
        }
        for (const id of this.pinWork.keys()) {
            if (!active.has(id)) this.pinWork.delete(id);
        }
        if (this.drawState?.selected !== selected || this.drawState?.trackedID !== trackedID) {
            this.overlapDirty = true;
        }
        this.drawState = { ...settings, selected, trackedID };

        for (const point of pending) {
            const id = point.id;
            const key = JSON.stringify([
                layoutKey, settings.visibility, id === selected, id === trackedID, point.dimmed,
                point.mapID, point.x, point.y, point.z, point.u, point.v,
                point.category, point.icon, point.state, point.availability, point.requirements, point.label
            ]);
            if ((this.pinWork.get(id)?.key || this.pinByID.get(id)?.renderKey) === key) continue;
            this.pinWork.set(id, { point, key });
            if (!this.queuedPins.has(id)) {
                this.queuedPins.add(id);
                this.pending.push(id);
            }
        }
    }

    refreshPinOverlapLayout(zoomScale) {
        const modItems = [];
        const selected = this.drawState?.selected || "";
        const tracked = this.drawState?.trackedID || "";
        for (const [id, node] of this.pinByID) {
            const raw = this.rawPositions.get(id);
            if (!raw || !node.point) continue;
            modItems.push({
                id,
                rawX: raw.x,
                rawY: raw.y,
                mass: pinMass(node.point, selected, tracked)
            });
        }
        const anchors = collectNativeAnchors(this.page, this.UE);
        this.lastNativeIconCount = anchors.length;
        this.pinPositions = resolveNativeMapOverlaps(modItems, anchors, zoomScale);
        this.lastLayoutZoom = zoomScale;
        this.overlapDirty = false;
        this.lastMapOffsetX = this.lastMapOffsetY = undefined;

        for (const [id, node] of this.pinByID) {
            const pos = this.pinPositions.get(id);
            if (pos) node.slot.SetPosition(new this.UE.Vector2D(pos.px, pos.py));
        }
    }

    stepPins(deltaSeconds = 0.016, hoveredID = "", trackedID = "") {
        const UE = this.UE, page = this.page, settings = this.drawState;
        if (!settings || !settings.visibility || settings.view !== "native") {
            this.releaseTrackedFX();
            return;
        }
        const zoomScale = this.currentZoomScale();

        if (this.cursor < this.pending.length) {
            const size = page.CurrentMapSize();
            for (let processed = 0; this.cursor < this.pending.length && processed < 28; this.cursor++, processed++) {
                const id = this.pending[this.cursor], work = this.pinWork.get(id);
                this.queuedPins.delete(id);
                this.pinWork.delete(id);
                if (!work) continue;
                const { point, key } = work;
                let node = this.pinByID.get(id);
                if (!node) {
                    node = this.freePins.pop();
                    if (!node) {
                        node = this.makePinWidget(page.Gadget, true);
                        node.interactive = false;
                        this.pins.push(node);
                    }
                    node.currentScale = zoomScale;
                    this.pinByID.set(id, node);
                }
                const rawPos = point.u !== undefined
                    ? new UE.Vector2D(point.u * size.X, point.v * size.Y)
                    : page.ConvertWorldLocationToScreen(new UE.Vector(point.x, point.y, point.z), point.mapID);
                this.rawPositions.set(id, { x: rawPos.X, y: rawPos.Y });
                if (!this.pinPositions.has(id)) {
                    this.pinPositions.set(id, { x: rawPos.X, y: rawPos.Y, px: rawPos.X, py: rawPos.Y });
                    node.slot.SetPosition(new UE.Vector2D(rawPos.X, rawPos.Y));
                }

                node.pointID = point.id;
                node.point = point;
                node.isFacility = !!point.facility || point.pointKind === "facility";
                node.renderKey = key;
                this.overlapDirty = true;
            }
            if (this.cursor === this.pending.length) {
                this.pending = [];
                this.cursor = 0;
            }
        }

        const nativeCount = page.focusItemMap?.size || 0;
        if (
            this.pinByID.size > 0 &&
            this.cursor === this.pending.length &&
            (this.overlapDirty || this.lastLayoutZoom === undefined || Math.abs(zoomScale - this.lastLayoutZoom) > 0.008 || nativeCount !== this.lastNativeIconCount)
        ) {
            this.refreshPinOverlapLayout(zoomScale);
        }

        const mapSlot = page.PBZMapCanvasSlotWeak;
        const offset = mapSlot?.GetPosition?.();
        const offsetChanged = offset && (offset.X !== this.lastMapOffsetX || offset.Y !== this.lastMapOffsetY);
        if (offsetChanged) {
            this.lastMapOffsetX = offset.X;
            this.lastMapOffsetY = offset.Y;
        }

        const dt = deltaSeconds > 0 && deltaSeconds < 0.25 ? deltaSeconds : 0.016;
        const blend = 1 - Math.exp(-dt / 0.042);
        const selectedID = settings.selected || "";
        const activeTrackedID = trackedID || settings.trackedID || "";

        for (const [id, node] of this.pinByID) {
            const point = node.point;
            if (!point) continue;
            const p = this.pinPositions.get(id);
            const inTopBar = p && offset && (p.py + offset.Y < 58);
            this.visible(node, !inTopBar);
            if (inTopBar) continue;

            const isSelected = id === selectedID;
            const isTracked = id === activeTrackedID;
            const isHovered = id === hoveredID;
            const isMarker = point.category === "marker";
            const isCollected = point.state === "collected";

            const baseZ = isMarker ? 24 : !isCollected && !node.isFacility ? 18 : node.isFacility ? 12 : 8;
            node.baseZOrder = baseZ;
            const zOrder = isHovered || isSelected ? 50 : isTracked ? 38 : baseZ;
            if (node.zOrder !== zOrder) {
                node.zOrder = zOrder;
                node.slot.SetZOrder(zOrder);
            }

            const targetScale = zoomScale * (isHovered ? 1.28 : isSelected ? 1.12 : 1.0);
            const cur = node.currentScale ?? zoomScale;
            const nextScale = Math.abs(targetScale - cur) < 0.004 ? targetScale : cur + (targetScale - cur) * blend;
            if (node.appliedScale !== nextScale) {
                node.currentScale = nextScale;
                node.appliedScale = nextScale;
                node.widget.SetRenderScale(new UE.Vector2D(nextScale, nextScale));
            }

            this.stylePin(node, point, isSelected, point.dimmed && !isSelected && !isHovered ? 0.35 : 1, isTracked, isHovered);
        }

        this.syncTrackedFX(activeTrackedID, zoomScale);
    }

    syncTrackedFX(trackedID, zoomScale) {
        const node = trackedID ? this.pinByID.get(trackedID) : undefined;
        const pos = node?.shown ? this.pinPositions.get(trackedID) : undefined;
        if (!node || !pos) {
            this.releaseTrackedFX();
            return;
        }
        try {
            const page = this.page, UE = this.UE;
            const da = page.getQuestUIAsset?.();
            if (!da?.TrackFX) {
                this.releaseTrackedFX();
                return;
            }
            const fxMat = da.TrackFX.Get(UE.EPBZQuestType?.Story ?? 0) || da.TrackFX.Get(0) || da.TrackFX.Get(1);
            if (!fxMat) {
                this.releaseTrackedFX();
                return;
            }
            if (!this.trackedFX) {
                if (!this.trackedFXClass) this.trackedFXClass = UE.Class.Load(QUEST_EFFECT_WIDGET_PATH);
                if (!this.trackedFXClass) return;
                this.trackedFX = page.GetOrCreateNormalWidget(this.trackedFXClass);
                if (!this.trackedFX) return;
            }
            if (!page.Gadget.HasChild(this.trackedFX)) {
                page.Gadget.AddChild(this.trackedFX);
                const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.trackedFX);
                slot.SetAutoSize(true);
                slot.SetAlignment(new UE.Vector2D(0.5, 0.5));
                slot.SetZOrder(4);
            }
            this.trackedFX.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
            if (this.trackedFX.Image_37 && this.trackedFXMat !== fxMat) {
                this.trackedFX.Image_37.SetBrushFromMaterial(fxMat);
                this.trackedFXMat = fxMat;
            }
            const fxSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.trackedFX);
            if (fxSlot) fxSlot.SetPosition(new UE.Vector2D(pos.px, pos.py));
            const s = (node.appliedScale || zoomScale) * 0.68;
            this.trackedFX.SetRenderScale(new UE.Vector2D(s, s));
        } catch {
            this.releaseTrackedFX();
        }
    }

    releaseTrackedFX() {
        if (!this.trackedFX) return;
        try { this.page?.RemoveWidget?.(this.trackedFX); } catch {}
        this.trackedFX = undefined;
        this.trackedFXMat = undefined;
    }

    arbitratePin(position, prevID = "", activeID = "") {
        return arbitrateCanvasPin(position, this.pinByID, this.pinPositions, this.page, this.UE, prevID, this.currentZoomScale(), activeID);
    }

    pinAt(position) {
        return this.arbitratePin(position).node;
    }

    resetPins() {
        this.releaseTrackedFX();
        this.pending = [];
        this.cursor = 0;
        this.pinLayoutKey = undefined;
        this.lastLayoutZoom = undefined;
        this.pinByID.clear();
        this.pinWork.clear();
        this.queuedPins.clear();
        this.rawPositions.clear();
        this.pinPositions.clear();
        for (const node of this.pins) this.visible(node, false);
        this.freePins = this.pins.slice();
    }

    hide(includeControls = false) {
        this.resetPins();
        this.panelDesired = false;
        this.settingsOpen = false;
        this.filterPanel?.show(false, this.config);
        this.showControls(!includeControls);
    }

    showControls(value) {
        this.controlsAllowed = !!value;
        this.syncChromeVisibility();
    }

    syncChromeVisibility() {
        const overlayOn = !!this.controlsAllowed && (this.config ? !!this.config.visibility : true);
        const showFull = overlayOn && !this.settingsOpen && !!this.panelDesired;
        const showCollapsed = overlayOn && !this.settingsOpen && !this.panelDesired;
        if (this.controlsVisible !== showFull) {
            this.controlsVisible = showFull;
            this.chrome.SetVisibility(showFull ? this.UE.ESlateVisibility.Visible : this.UE.ESlateVisibility.Collapsed);
        }
        this.visible(this.collapsedTab, showCollapsed);
    }

    chromeWidgets() {
        return [this.chrome, this.collapsedTab?.widget, this.filterPanel?.root].filter(Boolean);
    }

    resources() {
        return {
            widgets: this.widgets.length,
            roots: this.roots.length,
            delegates: this.listeners.length + this.buttons.length,
            pendingPins: this.pinWork.size
        };
    }

    dispose(detach = true) {
        const report = { before: this.resources(), detachedRoots: 0, removedDelegates: 0, errors: [] };
        const remove = operation => {
            try { operation(); return true; }
            catch (error) { report.errors.push(String(error)); return false; }
        };
        if (detach) remove(() => this.releaseTrackedFX());
        else this.trackedFX = this.trackedFXMat = undefined;

        for (const { widget, name, handler } of this.listeners) {
            if (remove(() => widget[name].Remove(handler))) report.removedDelegates++;
        }
        for (const { button, handler } of this.buttons) {
            if (remove(() => button.OnClicked.Remove(handler))) report.removedDelegates++;
        }
        if (detach) {
            for (const widget of this.roots) {
                if (remove(() => widget.RemoveFromParent())) report.detachedRoots++;
            }
        }
        this.filterPanel?.release();
        this.filterPanel = undefined;
        this.settingsOpen = false;
        this.buttons = [];
        this.listeners = [];
        this.widgets = [];
        this.pins = [];
        this.pending = [];
        this.rows = [];
        this.pinByID.clear();
        this.pinWork.clear();
        this.queuedPins.clear();
        this.rawPositions.clear();
        this.pinPositions.clear();
        this.freePins = [];
        this.pinLayoutKey = undefined;
        this.roots = [];
        this.owned.clear();
        this.cursor = 0;
        this.config = this.drawState = undefined;
        this.guidanceTabs = [];
        this.chrome = this.chromeSlot = this.collapsedTab = this.canvas = this.page = this.action = undefined;
        report.remainingReferences = this.resources();
        if (report.errors.length) require("./Diagnostics").record("widget-cleanup-error", report);
        return report;
    }
}

module.exports = { View };
