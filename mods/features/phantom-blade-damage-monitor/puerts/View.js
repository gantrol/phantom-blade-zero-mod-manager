"use strict";
const UE = require("ue"), Model = require("./Model"), { t } = require("./Locale");
const V = (x, y) => new UE.Vector2D(x, y);
const colors = { paper: [0.88, 0.84, 0.74, 1], muted: [0.65, 0.62, 0.54, 1], gold: [0.83, 0.64, 0.34, 1] };
const WIDTH = 480, COMPACT = 100, DETAIL = 380, TOTAL_WIDTH = 140, TOTAL_HEIGHT = 80;
function add(frame, parent, Type, x, y, w, h) {
    const widget = UE.NewObject(Type.StaticClass(), frame.WidgetTree);
    parent.AddChild(widget); widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
    slot.SetPosition(V(x, y)); slot.SetSize(V(w, h));
    return { widget, slot };
}
function rect(frame, parent, x, y, w, h, color) {
    const node = add(frame, parent, UE.Image, x, y, w, h), brush = new UE.SlateBrush(), tint = new UE.SlateColor();
    brush.DrawAs = UE.ESlateBrushDrawType.Image; tint.SpecifiedColor = new UE.LinearColor(1, 1, 1, 1); brush.TintColor = tint;
    node.widget.SetBrush(brush); node.widget.SetColorAndOpacity(new UE.LinearColor(...color));
    return node;
}
function label(frame, parent, x, y, w, h, size, color = "paper") {
    const node = add(frame, parent, UE.TextBlock, x, y, w, h);
    node.widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
    const font = node.widget.Font; font.Size = size; node.widget.SetFont(font);
    const tint = new UE.SlateColor(); tint.SpecifiedColor = new UE.LinearColor(...colors[color]); node.widget.SetColorAndOpacity(tint);
    node.set = value => {
        if (node.value === value) return;
        node.value = value; node.widget.SetText(UE.KismetTextLibrary.Conv_StringToText(value));
    };
    return node;
}
class View {
    constructor(frame, root) {
        this.box = add(frame, root, UE.CanvasPanel, 0, 0, WIDTH, COMPACT);
        this.box.slot.SetAlignment(V(1, 1));
        const parent = this.box.widget;
        this.background = rect(frame, parent, 0, 0, WIDTH, COMPACT, [0.025, 0.031, 0.025, 0.92]);
        this.accent = rect(frame, parent, 0, 0, WIDTH, 1, colors.gold);
        this.body = add(frame, parent, UE.CanvasPanel, 0, 0, WIDTH - TOTAL_WIDTH - 8, COMPACT);
        this.status = label(frame, this.body.widget, 8, 5, 320, 16, 8, "muted");
        this.cells = Model.order.map((id, index) => {
            const x = 8 + index % 5 * 64, y = 25 + Math.floor(index / 5) * 36;
            const title = label(frame, this.body.widget, x, y, 58, 14, 8, "muted");
            const value = label(frame, this.body.widget, x, y + 12, 53, 25, 14);
            if (index % 5 < 4) label(frame, this.body.widget, x + 52, y + 16, 12, 16, 9, "muted").set("×");
            return { id, title, value };
        });
        rect(frame, this.body.widget, 330, 25, 1, 64, [0.83, 0.64, 0.34, 0.3]);
        this.totalPane = add(frame, parent, UE.CanvasPanel, 0, 0, TOTAL_WIDTH, TOTAL_HEIGHT);
        this.totalLabel = label(frame, this.totalPane.widget, 6, 3, 132, 18, 9, "gold");
        this.total = label(frame, this.totalPane.widget, 6, 20, 134, 40, 25, "gold");
        this.note = label(frame, this.totalPane.widget, 6, 55, 132, 15, 8, "muted");
        this.shortcut = label(frame, this.totalPane.widget, 6, 68, 132, 12, 7, "muted");
        this.detail = add(frame, parent, UE.CanvasPanel, 0, -DETAIL, WIDTH, DETAIL);
        rect(frame, this.detail.widget, 0, 0, WIDTH, DETAIL, [0.025, 0.031, 0.025, 0.94]);
        this.detailTitle = label(frame, this.detail.widget, 8, 8, 464, 18, 11, "gold");
        this.rows = Array.from({ length: 10 }, (_, i) => ({
            name: label(frame, this.detail.widget, 8, 32 + i * 30, 76, 19, 9, "muted"),
            value: label(frame, this.detail.widget, 85, 32 + i * 30, 48, 19, 10),
            detail: label(frame, this.detail.widget, 138, 32 + i * 30, 334, 17, 8),
            source: label(frame, this.detail.widget, 138, 46 + i * 30, 334, 16, 7, "muted")
        }));
        this.defense = label(frame, this.detail.widget, 8, 340, 464, 18, 8, "muted");
        this.sources = label(frame, this.detail.widget, 8, 360, 464, 18, 8, "gold");
        this.setMode(1);
    }
    get expanded() { return this.mode === 2; }
    cycle() { this.setMode((this.mode + 1) % 3); }
    setMode(mode) {
        this.mode = mode;
        this.width = mode === 0 ? TOTAL_WIDTH + 8 : WIDTH;
        this.height = mode === 0 ? TOTAL_HEIGHT : COMPACT;
        this.box.slot.SetSize(V(this.width, this.height));
        this.background.slot.SetSize(V(this.width, this.height));
        this.accent.slot.SetSize(V(this.width, 1));
        this.totalPane.slot.SetPosition(V(this.width - TOTAL_WIDTH - 4, this.height - TOTAL_HEIGHT));
        this.body.widget.SetVisibility(mode === 0 ? UE.ESlateVisibility.Collapsed : UE.ESlateVisibility.HitTestInvisible);
        this.detail.widget.SetVisibility(this.expanded ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
    }
    place(size) {
        // The compact panel never moves when details open. Fit the whole stack
        // to the available viewport while keeping its lower-right corner fixed.
        const scale = Math.min(1, Math.max(0.1, (size.X - 32) / this.width), Math.max(0.1, (size.Y - 32) / (this.height + (this.expanded ? DETAIL : 0))));
        this.box.widget.SetRenderTransformPivot(V(1, 1));
        this.box.widget.SetRenderScale(V(scale, scale));
        this.box.slot.SetPosition(V(size.X - 16, size.Y - 16));
    }
    render(snapshot, effects, shortcut, clock) {
        if (!snapshot) return;
        this.shortcut.set(shortcut + " · " + t("mode." + this.mode));
        const movement = snapshot.hitMovement || snapshot.currentMovement;
        this.status.set(t("lane." + (snapshot.lane || "mixed")) + " · "
            + (snapshot.source === "hit" ? t("lastHit", { seconds: Model.number(clock - snapshot.at) }) : t(snapshot.target ? "live" : "noTarget"))
            + (movement ? " · " + t(snapshot.hitMovement ? "movement.hitShort" : "movement.liveShort", { factor: Model.number(movement.multiplier) }) : ""));
        for (const cell of this.cells) { cell.title.set(t(cell.id)); cell.value.set(Model.number(snapshot.values[cell.id])); }
        this.totalLabel.set(t("total")); this.total.set(Model.number(snapshot.total));
        this.note.set(t(snapshot.source === "hit" ? "settlement" : "waiting"));
        if (!this.expanded) return;
        this.detailTitle.set(t("details"));
        for (let i = 0; i < Model.order.length; i++) {
            const id = Model.order[i], row = this.rows[i];
            row.name.set(t(id)); row.value.set(Model.number(snapshot.values[id]));
            let detail;
            if (id === "attack") detail = t("attack.note");
            else if (id === "correction") detail = t("correction.note");
            else if (id === "preTake") {
                const stages = snapshot.stages, tag = snapshot.lane === "True" ? "Damage.Damage.Target.TrueDamage" : "Damage.Damage.Target.GeneralDamage." + snapshot.lane;
                detail = stages ? [0, 1, 2].map(p => "P" + p + " " + Model.number(stages[p]?.[tag])).join(" → ") : t("waiting");
            } else detail = (snapshot.attributes[id] || []).map(item => t("family." + item.name.slice(item.name.indexOf("_") + 1)) + " " + Model.number(item.raw) + " → ×" + Model.number(item.value)).join("  ×  ") || t("notApplicable");
            row.detail.set(detail);
            const attributes = snapshot.attributes[id]?.map(item => item.name);
            const sources = effects.filter(effect => effect.group === id && (!attributes || attributes.includes(effect.attribute)));
            if (id === "correction" && movement) sources.unshift({ movement, captured: !!snapshot.hitMovement });
            const effect = sources.length ? sources[Math.floor(clock / 3) % sources.length] : undefined;
            if (!effect) { row.source.set(t("noEffects")); continue; }
            if (effect.movement) {
                row.source.set(t(effect.captured ? "movement.hit" : "movement.live", {
                    name: t("effect.animMovement." + movement.level), factor: Model.number(movement.multiplier),
                    metres: Model.number(movement.distance), cap: Model.number(movement.cap * 100)
                }));
                continue;
            }
            const sourceKey = "effect." + effect.source, localized = t(sourceKey);
            const source = localized === sourceKey ? effect.source : localized;
            const duration = effect.remaining < 0 ? t("permanent") : Model.number(effect.remaining) + "s";
            row.source.set(t("currentSource") + " · "
                + (effect.conditional ? t("conditional") : Model.number(effect.value) + " " + t("op." + effect.op))
                + " · " + duration + (effect.stacks > 1 ? " ×" + effect.stacks : "") + " · " + source);
        }
        this.defense.set(t("defense", { shield: Model.number(snapshot.shield), maximum: Model.number(snapshot.shieldMax) }));
        this.sources.set(t("effects", { count: effects.length + (snapshot.currentMovement ? 1 : 0) }));
    }
}
module.exports = { View };
