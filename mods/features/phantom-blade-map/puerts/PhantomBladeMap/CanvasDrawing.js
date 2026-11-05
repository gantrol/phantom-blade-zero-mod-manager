"use strict";
const {collectibles}=require("./Icons");
const {rgba}=require("./Appearance");
class CanvasDrawing {
    constructor(page,UE,cpp,text) {
        this.page=page;this.UE=UE;this.cpp=cpp;this.text=text;
        this.widgets=[];this.owned=new Set();this.roots=[];
    }
    create(type, parent) {
        const widget = this.UE.NewObject(type.StaticClass(), this.page);
        if (!this.owned.has(parent)) this.roots.push(widget);
        this.widgets.push(widget); this.owned.add(widget); parent.AddChild(widget); return widget;
    }
    color(r, g, b, a = 1) {
        const color = new this.UE.SlateColor();
        color.SpecifiedColor = new this.UE.LinearColor(r, g, b, a); return color;
    }
    makeRect(parent, x, y, width, height, r, g, b, a = 1) {
        const UE = this.UE, widget = this.create(UE.Image, parent), brush = new UE.SlateBrush();
        brush.DrawAs = UE.ESlateBrushDrawType.Image;
        brush.TintColor = this.color(1, 1, 1, 1);
        widget.SetBrush(brush);
        widget.SetColorAndOpacity(new UE.LinearColor(r, g, b, a));
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        slot.SetPosition(new UE.Vector2D(x, y)); slot.SetSize(new UE.Vector2D(width, height));
        return { widget, slot };
    }
    makeLineWidget(parent) {
        const UE = this.UE, widget = this.create(UE.Image, parent), brush = new UE.SlateBrush();
        brush.DrawAs = UE.ESlateBrushDrawType.Image;
        brush.TintColor = this.color(1, 1, 1, 1);
        widget.SetBrush(brush);
        widget.SetRenderTransformPivot(new UE.Vector2D(0, .5));
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        slot.SetAlignment(new UE.Vector2D(0, .5));
        return { widget, slot };
    }
    drawLine(node, x1, y1, x2, y2, thickness, r, g, b, a = 1) {
        const dx = x2 - x1, dy = y2 - y1, len = Math.hypot(dx, dy);
        if (len < .5) { this.visible(node, false); return false; }
        node.widget.SetColorAndOpacity(new this.UE.LinearColor(r, g, b, a));
        node.slot.SetPosition(new this.UE.Vector2D(x1, y1));
        node.slot.SetSize(new this.UE.Vector2D(len, thickness));
        node.widget.SetRenderTransformAngle(Math.atan2(dy, dx) * 180 / Math.PI);
        this.visible(node, true);
        return true;
    }
    makeText(parent, size, x = 0, y = 0, width = 40, height = 32, rgba = [.88, .84, .74, 1]) {
        const UE = this.UE, widget = this.create(UE.TextBlock, parent);
        const font = widget.Font, source = this.page.MapText?.Font || this.page.PercentageTextBlock?.Font;
        if (source) { font.FontObject = source.FontObject; font.TypefaceFontName = source.TypefaceFontName; }
        font.Size = size; widget.SetFont(font);
        widget.SetAutoWrapText(false);
        widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
        widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        widget.SetColorAndOpacity(this.color(rgba[0], rgba[1], rgba[2], rgba[3] ?? 1));
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        if (slot) {
            slot.SetPosition(new UE.Vector2D(x, y));
            slot.SetSize(new UE.Vector2D(width, height));
        }
        return { widget, slot };
    }
    makePinWidget(parent = this.page.Gadget) {
        const UE = this.UE, canvas = this.create(UE.CanvasPanel, parent);
        canvas.SetVisibility(UE.ESlateVisibility.Collapsed);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(canvas);
        slot.SetSize(new UE.Vector2D(32, 32));
        slot.SetAlignment(new UE.Vector2D(.5, .5));
        slot.SetZOrder(15);
        const text = this.makeText(canvas, 12, 38, 5, 180, 24, rgba("paper"));
        return { widget: canvas, slot, text, strokes: [] };
    }
    stylePin(node, point, selected, opacity=1) {
        const icon=point.icon || point.category;
        const isFacility=!!point.facility || point.pointKind==="facility" || ["stairs","ladder","zipline","grapple","rappel","slide","wallRun","wallClimb"].includes(point.category);
        const baseAlpha=isFacility && !selected ? .78 : 1;
        const alpha=opacity*baseAlpha*(point.state==="unknown"?(selected ? .75 : .5):1);
        if(node.opacity!==alpha){node.widget.SetRenderOpacity(alpha);node.opacity=alpha;}
        const collected = point.state === "collected", key = icon + ":" + selected + ":" + collected + ":" + isFacility + ":" + (point.availability || "");
        if (node.styleKey !== key) {
            const ink = rgba("ink", collected ? .65 : isFacility && !selected ? .82 : .95);
            const paletteName = selected || point.category === "marker"
                ? "accent"
                : point.availability
                    ? "muted"
                    : isFacility
                        ? "facility"
                        : ["bell","weapon","shadowWeapon","accessory","medicine","amber","blackstone","flower","treasure","quest"].includes(point.category)
                            ? "gold"
                            : "paper";
            const tint = rgba(paletteName, collected ? .35 : 1);
            const outlineWidth = isFacility && !selected ? 2.4 : 3.4;
            const strokeWidth = isFacility && !selected ? 1.2 : 1.65;
            let i = 0;
            for (const path of collectibles[icon] || collectibles.unknown) for (let p = 1; p < path.length; p++) {
                const a = path[p - 1], b = path[p];
                if (!node.strokes[i]) node.strokes[i] = [this.makeLineWidget(node.widget), this.makeLineWidget(node.widget)];
                const [outline, stroke] = node.strokes[i++];
                // The narrow dark edge keeps pale ink readable on the original paper map.
                outline.slot.SetZOrder(0); stroke.slot.SetZOrder(1);
                this.drawLine(outline, a[0] + 4, a[1] + 4, b[0] + 4, b[1] + 4, outlineWidth, ...ink);
                this.drawLine(stroke, a[0] + 4, a[1] + 4, b[0] + 4, b[1] + 4, strokeWidth, ...tint);
            }
            for (; i < node.strokes.length; i++) for (const stroke of node.strokes[i]) this.visible(stroke, false);
            node.styleKey = key;
        }
        const named=selected || point.category==="treasure";
        this.visible(node.text, named);
        if (named) {
            this.textValue(node.text, point.label || this.text.marker);
            node.text.widget.SetToolTipText(this.cpp.FText.FromString([point.label,this.text[point.availability]].filter(Boolean).join(" · ")));
        }
    }

    textValue(node,value) { if(node.value!==value){node.widget.SetText(this.cpp.FText.FromString(value));node.value=value;} }
    visible(node,value) {
        const visibility=value?(node.interactive?this.UE.ESlateVisibility.Visible:this.UE.ESlateVisibility.HitTestInvisible):this.UE.ESlateVisibility.Collapsed;
        if(node.vis!==visibility){node.widget.SetVisibility(visibility);node.vis=visibility;}
    }
    place(node,x,y){node.slot.SetPosition(new this.UE.Vector2D(x,y));}
    release(detach=true) {
        if(detach)for(const root of this.roots)if(this.UE.KismetSystemLibrary.IsValid(root))root.RemoveFromParent();
        this.widgets=[];this.roots=[];this.owned.clear();this.page=undefined;
    }
}
module.exports={CanvasDrawing};
