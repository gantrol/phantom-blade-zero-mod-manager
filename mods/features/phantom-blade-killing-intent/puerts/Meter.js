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
class Meter {
    constructor(frame, root, source, height) {
        const thickness = config.arcThickness, halfAngle = config.arcDegrees * Math.PI / 360;
        const radius = (height - thickness) / (2 * Math.sin(halfAngle));
        const points = Array.from({ length: config.arcSegments + 1 }, (_, i) => {
            const angle = -halfAngle + 2 * halfAngle * i / config.arcSegments;
            return V(thickness / 2 + radius * (Math.cos(angle) - Math.cos(halfAngle)),
                thickness / 2 + radius * (Math.sin(angle) + Math.sin(halfAngle)));
        });
        this.width = thickness + radius * (1 - Math.cos(halfAngle));
        this.height = height;
        const canvas = add(frame, root, UE.CanvasPanel);
        this.widget = canvas.widget;
        canvas.slot.SetSize(V(this.width, height));
        canvas.slot.SetAlignment(V(0.5, 0.5));
        this.widget.SetRenderTransformPivot(V(0.5, 0.5));
        // Clone the native yellow bar's material and overrides once. The source
        // instance and all gameplay attributes remain owned by the game.
        this.material = UE.KismetMaterialLibrary.CreateDynamicMaterialInstance(frame, source.Parent);
        this.material.CopyParameterOverrides(source);
        this.material.SetScalarParameterValue("ShowUpRatio", 1);
        // Bend one continuous native strip into an arc. Each rotated clip shows
        // its own interval of the SAME material/UV span, preserving the texture
        // and a single continuous fill boundary rather than repeating a bar.
        const segmentLength = 2 * radius * Math.sin(halfAngle / config.arcSegments);
        const stripLength = segmentLength * config.arcSegments;
        for (let i = 0; i < config.arcSegments; i++) {
            const a = points[i], b = points[i + 1], clip = add(frame, this.widget, UE.CanvasPanel);
            clip.slot.SetPosition(a);
            clip.slot.SetSize(V(segmentLength + 0.2, thickness));
            clip.slot.SetAlignment(V(0, 0.5));
            clip.widget.SetRenderTransformPivot(V(0, 0.5));
            clip.widget.SetRenderTransformAngle(Math.atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI);
            clip.widget.SetClipping(UE.EWidgetClipping.ClipToBoundsAlways);
            rect(frame, clip.widget, 0, 0.5, segmentLength + 0.2, thickness - 1, [0.025, 0.023, 0.017, 0.65]);
            const fill = add(frame, clip.widget, UE.Image);
            fill.widget.SetBrushFromMaterial(this.material);
            fill.slot.SetPosition(V(-i * segmentLength, 0));
            fill.slot.SetSize(V(stripLength, thickness));
        }
        this.show(false);
    }
    setRatio(value) {
        const ratio = Math.max(0, Math.min(1, value));
        if (this.ratio === ratio) return;
        this.ratio = ratio;
        this.material.SetScalarParameterValue("BarRatio_FG", ratio);
        this.material.SetScalarParameterValue("BarRatio_BG", ratio);
    }
    place(x, y) { this.widget.SetRenderTranslation(V(x, y)); }
    show(visible) {
        if (this.visible === visible) return;
        this.visible = visible;
        this.widget.SetVisibility(visible ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
    }
    dispose() { this.widget.RemoveFromParent(); }
}
module.exports = { Meter };
