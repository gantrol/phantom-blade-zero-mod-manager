"use strict";
const UE = require("ue");
const config = require("./Config");
const V = (x, y) => new UE.Vector2D(x, y);

function add(frame, parent, Type) {
    const widget = UE.NewObject(Type.StaticClass(), frame.WidgetTree);
    parent.AddChild(widget);
    widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
    return { widget, slot: UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget) };
}
function rect(frame, parent, x, y, width, height, color) {
    const node = add(frame, parent, UE.Image), brush = new UE.SlateBrush();
    brush.DrawAs = UE.ESlateBrushDrawType.Image;
    const tint = new UE.SlateColor();
    tint.SpecifiedColor = new UE.LinearColor(1, 1, 1, 1);
    brush.TintColor = tint;
    node.widget.SetBrush(brush);
    node.widget.SetColorAndOpacity(new UE.LinearColor(...color));
    node.slot.SetPosition(V(x, y));
    node.slot.SetSize(V(width, height));
    return node;
}
// Geometry is built once; only the filled lengths change during sampling.
class ShallowArc {
    constructor(frame, parent, width, rise, thickness, color, x, y) {
        const canvas = add(frame, parent, UE.CanvasPanel);
        this.widget = canvas.widget;
        canvas.slot.SetPosition(V(x, y));
        canvas.slot.SetSize(V(width, rise + thickness));
        this.segments = [];
        const count = config.overheadSegments;
        const point = t => V(thickness / 2 + t * (width - thickness),
            thickness / 2 + rise * (2 * t - 1) ** 2);
        for (let i = 0; i < count; i++) {
            const a = point(i / count), b = point((i + 1) / count);
            const length = Math.hypot(b.X - a.X, b.Y - a.Y);
            const height = thickness * (0.15 + 0.85 * Math.sin(Math.PI * (i + 0.5) / count));
            const angle = Math.atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            const track = rect(frame, this.widget, a.X, a.Y, length + 0.2, height,
                [0.015, 0.013, 0.012, 0.82]);
            const fill = rect(frame, this.widget, a.X, a.Y, length, height, color);
            for (const node of [track, fill]) {
                node.slot.SetAlignment(V(0, 0.5));
                node.widget.SetRenderTransformPivot(V(0, 0.5));
                node.widget.SetRenderTransformAngle(angle);
            }
            this.segments.push({ ...fill, length, height });
        }
        this.setRatio(0);
    }
    setRatio(value) {
        const ratio = Math.max(0, Math.min(1, value));
        if (this.ratio === ratio) return;
        this.ratio = ratio;
        this.segments.forEach((node, i) => {
            const fraction = Math.max(0, Math.min(1, ratio * this.segments.length - i));
            if (node.fraction === fraction) return;
            node.fraction = fraction;
            node.widget.SetVisibility(fraction > 0 ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
            if (fraction > 0) node.slot.SetSize(V(node.length * fraction + (fraction === 1 ? 0.2 : 0), node.height));
        });
    }
    show(visible) {
        if (this.visible === visible) return;
        this.visible = visible;
        this.widget.SetVisibility(visible ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
    }
}

class OverheadMeters {
    constructor(frame, root) {
        const width = config.playerWidth, rise = config.playerRise, thickness = config.playerThickness;
        const tensionWidth = width * config.tensionWidthRatio;
        const tensionRise = rise * config.tensionWidthRatio ** 2;
        const tensionThickness = thickness * config.tensionThicknessRatio;
        const tensionHeight = tensionRise + tensionThickness;
        this.width = width;
        this.height = tensionHeight + config.overheadGap + rise + thickness;
        const canvas = add(frame, root, UE.CanvasPanel);
        this.widget = canvas.widget;
        canvas.slot.SetSize(V(this.width, this.height));
        // Position is the bottom center, just above the projected head.
        canvas.slot.SetAlignment(V(0.5, 1));
        this.tension = new ShallowArc(frame, this.widget, tensionWidth, tensionRise,
            tensionThickness, config.tensionColor, (width - tensionWidth) / 2, 0);
        this.intent = new ShallowArc(frame, this.widget, width, rise, thickness,
            config.intentColor, 0, tensionHeight + config.overheadGap);
        this.setTension(undefined);
        this.show(false);
    }
    setIntent(current, max) {
        const ready = Number.isFinite(current) && Number.isFinite(max) && max > 0;
        this.intent.show(ready);
        if (ready) this.intent.setRatio(current / max);
        return ready;
    }
    setTension(raw) {
        const ready = Number.isFinite(raw);
        this.tension.show(ready);
        // Native FTensionReadout.Raw is signed: -100 calm, 0 neutral, +100 high.
        if (ready) this.tension.setRatio((raw + 100) / 200);
    }
    place(x, y) { this.widget.SetRenderTranslation(V(x, y)); }
    show(visible) {
        if (this.visible === visible) return;
        this.visible = visible;
        this.widget.SetVisibility(visible ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
    }
}
module.exports = { OverheadMeters };
