"use strict";
const { collectibles } = require("./Icons");
const { rgba } = require("./Appearance");

class CanvasDrawing {
    constructor(page, UE, cpp, text) {
        this.page = page;
        this.UE = UE;
        this.cpp = cpp;
        this.text = text;
        this.widgets = [];
        this.owned = new Set();
        this.roots = [];
    }

    create(type, parent) {
        const widget = this.UE.NewObject(type.StaticClass(), this.page);
        if (!this.owned.has(parent)) this.roots.push(widget);
        this.widgets.push(widget);
        this.owned.add(widget);
        parent.AddChild(widget);
        return widget;
    }

    color(r, g, b, a = 1) {
        const color = new this.UE.SlateColor();
        color.SpecifiedColor = new this.UE.LinearColor(r, g, b, a);
        return color;
    }

    makeRect(parent, x, y, width, height, r, g, b, a = 1) {
        const UE = this.UE, widget = this.create(UE.Image, parent), brush = new UE.SlateBrush();
        brush.DrawAs = UE.ESlateBrushDrawType.Image;
        brush.TintColor = this.color(1, 1, 1, 1);
        widget.SetBrush(brush);
        widget.SetColorAndOpacity(new UE.LinearColor(r, g, b, a));
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        slot.SetPosition(new UE.Vector2D(x, y));
        slot.SetSize(new UE.Vector2D(width, height));
        return { widget, slot };
    }

    makeBrushLine(parent, x, y, width, height, r, g, b, a = 1) {
        const node = this.makeRect(parent, x, y, width, height, r, g, b, a);
        const resource = this.page?.Image_169?.Brush?.ResourceObject;
        if (resource) {
            const brush = new this.UE.SlateBrush();
            brush.DrawAs = this.UE.ESlateBrushDrawType.Image;
            brush.ResourceObject = resource;
            brush.TintColor = this.color(1, 1, 1, 1);
            node.widget.SetBrush(brush);
        }
        return node;
    }

    makeInkSubstrate(parent, width, height, featherLeft = 36, bodyAlpha = 0.91) {
        const parts = [];
        if (featherLeft > 0) {
            const steps = 8, stepW = featherLeft / steps;
            for (let i = 0; i < steps; i++) {
                const t = (i + 0.5) / steps;
                const smooth = t * t * (3 - 2 * t);
                const strip = this.makeRect(parent, -featherLeft + i * stepW, 0, stepW + 0.8, height, ...rgba("ink", bodyAlpha * smooth * 0.78));
                strip.slot.SetZOrder(0);
                parts.push(strip);
            }
        }
        const body = this.makeRect(parent, 0, 0, width, height, ...rgba("panel", bodyAlpha));
        body.slot.SetZOrder(0);
        const headerWash = this.makeRect(parent, 0, 0, width, 48, ...rgba("ink", 0.42));
        headerWash.slot.SetZOrder(0);
        const spine = this.makeRect(parent, 0, 10, 1, height - 20, ...rgba("gold", 0.24));
        spine.slot.SetZOrder(1);
        const topRule = this.makeBrushLine(parent, 8, 0, width - 16, 1.5, ...rgba("gold", 0.34));
        topRule.slot.SetZOrder(1);
        const bottomRule = this.makeBrushLine(parent, 8, height - 1.5, width - 16, 1.5, ...rgba("gold", 0.28));
        bottomRule.slot.SetZOrder(1);
        parts.push(body, headerWash, spine, topRule, bottomRule);
        return parts;
    }

    makeLineWidget(parent) {
        const UE = this.UE, widget = this.create(UE.Image, parent), brush = new UE.SlateBrush();
        brush.DrawAs = UE.ESlateBrushDrawType.Image;
        brush.TintColor = this.color(1, 1, 1, 1);
        widget.SetBrush(brush);
        widget.SetRenderTransformPivot(new UE.Vector2D(0, 0.5));
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        slot.SetAlignment(new UE.Vector2D(0, 0.5));
        return { widget, slot };
    }

    drawLine(node, x1, y1, x2, y2, thickness, r, g, b, a = 1, capExtend = true) {
        const dx = x2 - x1, dy = y2 - y1, len = Math.hypot(dx, dy);
        if (len < 0.45) { this.visible(node, false); return false; }
        const ext = capExtend ? Math.min(len * 0.25, thickness * 0.34) : 0;
        const ux = dx / len, uy = dy / len;
        node.widget.SetColorAndOpacity(new this.UE.LinearColor(r, g, b, a));
        node.slot.SetPosition(new this.UE.Vector2D(x1 - ux * ext, y1 - uy * ext));
        node.slot.SetSize(new this.UE.Vector2D(len + ext * 2, thickness));
        node.widget.SetRenderTransformAngle(Math.atan2(dy, dx) * 180 / Math.PI);
        this.visible(node, true);
        return true;
    }

    makeText(parent, size, x = 0, y = 0, width = 40, height = 32, colorRgba = [0.88, 0.84, 0.74, 1]) {
        const UE = this.UE, widget = this.create(UE.TextBlock, parent);
        const font = widget.Font, source = this.page.MapText?.Font || this.page.PercentageTextBlock?.Font;
        if (source) { font.FontObject = source.FontObject; font.TypefaceFontName = source.TypefaceFontName; }
        font.Size = size;
        widget.SetFont(font);
        widget.SetAutoWrapText(false);
        widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        widget.SetColorAndOpacity(this.color(colorRgba[0], colorRgba[1], colorRgba[2], colorRgba[3] ?? 1));
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        if (slot) {
            slot.SetPosition(new UE.Vector2D(x, y));
            slot.SetSize(new UE.Vector2D(width, height));
        }
        return { widget, slot };
    }

    makePinWidget(parent = this.page.Gadget, withPlate = true) {
        const UE = this.UE, canvas = this.create(UE.CanvasPanel, parent);
        canvas.SetVisibility(UE.ESlateVisibility.Collapsed);
        canvas.SetRenderTransformPivot(new UE.Vector2D(0.5, 0.5));
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(canvas);
        slot.SetSize(new UE.Vector2D(32, 32));
        slot.SetAlignment(new UE.Vector2D(0.5, 0.5));
        slot.SetZOrder(15);

        let plate, plateRim;
        if (withPlate) {
            plateRim = this.makeRect(canvas, 6, 6, 20, 20, ...rgba("line", 0.42));
            plateRim.widget.SetRenderTransformPivot(new UE.Vector2D(0.5, 0.5));
            plateRim.widget.SetRenderTransformAngle(45);
            plateRim.slot.SetZOrder(0);
            plate = this.makeRect(canvas, 7, 7, 18, 18, ...rgba("ink", 0.78));
            plate.widget.SetRenderTransformPivot(new UE.Vector2D(0.5, 0.5));
            plate.widget.SetRenderTransformAngle(45);
            plate.slot.SetZOrder(0);
        }

        const text = this.makeText(canvas, 12, 36, 4, 180, 24, rgba("paper"));
        text.widget.SetShadowOffset(new UE.Vector2D(1, 1));
        text.widget.SetShadowColorAndOpacity(new UE.LinearColor(0, 0, 0, 0.92));
        text.slot.SetZOrder(5);
        return { widget: canvas, slot, text, plate, plateRim, strokes: [], scale: 1 };
    }

    stylePin(node, point, selected, opacity = 1, tracked = false, hovered = false) {
        const icon = point.icon || point.category;
        const isFacility = !!point.facility || point.pointKind === "facility" || ["stairs", "ladder", "zipline", "grapple", "rappel", "slide", "wallRun", "wallClimb"].includes(point.category);
        const isBell = point.category === "bell", isPlayer = point.category === "player";
        const baseAlpha = isFacility && !selected && !hovered && !tracked ? 0.80 : 1;
        const alpha = opacity * baseAlpha * (point.state === "unknown" ? (selected || hovered ? 0.85 : 0.58) : 1);
        if (node.opacity !== alpha) { node.widget.SetRenderOpacity(alpha); node.opacity = alpha; }

        const collected = point.state === "collected";
        const key = [icon, selected, tracked, hovered, collected, isFacility, point.availability || "", !!point.nativeLock].join(":");
        if (node.styleKey !== key) {
            const dimmed = collected && !isBell && !isPlayer && !point.nativeLock;
            if (node.plate) {
                const showPlate = !isPlayer;
                this.visible(node.plate, showPlate);
                this.visible(node.plateRim, showPlate);
                if (showPlate) {
                    const plateAlpha = dimmed ? 0.42 : selected || hovered || tracked ? 0.90 : isFacility ? 0.62 : 0.78;
                    node.plate.widget.SetColorAndOpacity(new this.UE.LinearColor(...rgba(selected ? "selected" : "ink", plateAlpha)));
                    const rimPalette = tracked ? "gold" : selected ? "accent" : hovered ? "paper" : "line";
                    const rimAlpha = tracked || selected ? 0.88 : hovered ? 0.65 : dimmed ? 0.22 : 0.40;
                    node.plateRim.widget.SetColorAndOpacity(new this.UE.LinearColor(...rgba(rimPalette, rimAlpha)));
                }
            }
            const ink = rgba("ink", dimmed ? 0.65 : isFacility && !selected && !hovered ? 0.84 : 0.98);
            const paletteName = point.nativeLock ? (collected ? "facility" : "accent")
                : isPlayer || point.category === "marker" ? "accent"
                : point.availability ? (require("./Conditions").pending(point) ? "muted" : "accent")
                : selected ? "accent"
                : tracked ? "gold"
                : isBell ? (collected ? "gold" : "paper")
                : isFacility ? "facility"
                : ["weapon", "shadowWeapon", "accessory", "medicine", "amber", "blackstone", "flower", "treasure", "quest"].includes(point.category) ? "gold"
                : "paper";
            const tint = rgba(paletteName, dimmed ? 0.38 : isBell && !collected && !selected && !hovered ? 0.72 : isPlayer && collected ? 0.75 : 1);
            const outlineWidth = isPlayer ? 4.2 : selected || hovered ? 3.6 : isFacility ? 2.4 : 3.1;
            const strokeWidth = isPlayer ? 2.1 : selected || hovered ? 1.9 : isFacility ? 1.25 : 1.65;
            const shape = (collectibles[icon] || collectibles.unknown).slice();
            if (point.nativeLock && !collected) shape.push([[3, 3], [21, 21]]);
            if ((selected || tracked) && !isPlayer) shape.unshift([[12, -3], [27, 12], [12, 27], [-3, 12], [12, -3]]);

            let i = 0;
            for (const path of shape) {
                for (let p = 1; p < path.length; p++) {
                    const a = path[p - 1], b = path[p];
                    if (!node.strokes[i]) node.strokes[i] = [this.makeLineWidget(node.widget), this.makeLineWidget(node.widget)];
                    const [outline, stroke] = node.strokes[i++];
                    outline.slot.SetZOrder(1);
                    stroke.slot.SetZOrder(2);
                    const ringStroke = p <= 4 && (selected || tracked) && !isPlayer && path === shape[0];
                    const strokeTint = ringStroke ? rgba(selected ? "accent" : "gold", 0.95) : tint;
                    this.drawLine(outline, a[0] + 4, a[1] + 4, b[0] + 4, b[1] + 4, outlineWidth, ...ink, true);
                    this.drawLine(stroke, a[0] + 4, a[1] + 4, b[0] + 4, b[1] + 4, strokeWidth, ...strokeTint, true);
                }
            }
            for (; i < node.strokes.length; i++) {
                for (const s of node.strokes[i]) this.visible(s, false);
            }
            node.styleKey = key;
        }
        this.visible(node.text, false);
    }

    textValue(node, value) {
        if (node && node.value !== value) {
            node.widget.SetText(this.cpp.FText.FromString(value));
            node.value = value;
        }
    }

    visible(node, value) {
        if (!node?.widget) return;
        const visibility = value
            ? (node.interactive ? this.UE.ESlateVisibility.Visible : this.UE.ESlateVisibility.HitTestInvisible)
            : this.UE.ESlateVisibility.Collapsed;
        if (node.vis !== visibility) {
            node.widget.SetVisibility(visibility);
            node.vis = visibility;
        }
        node.shown = !!value;
    }

    place(node, x, y) {
        node.slot.SetPosition(new this.UE.Vector2D(x, y));
    }

    release(detach = true) {
        if (detach) {
            for (const root of this.roots) {
                if (this.UE.KismetSystemLibrary.IsValid(root)) root.RemoveFromParent();
            }
        }
        this.widgets = [];
        this.roots = [];
        this.owned.clear();
        this.page = undefined;
    }
}

module.exports = { CanvasDrawing };
