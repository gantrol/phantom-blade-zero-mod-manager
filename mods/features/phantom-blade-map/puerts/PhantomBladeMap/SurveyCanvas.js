"use strict";
const { rgba } = require("./Appearance");

function clip(a, b, width, height) {
    const dx = b[0] - a[0], dy = b[1] - a[1]; let low = 0, high = 1;
    for (const [p, q] of [[-dx, a[0]], [dx, width - a[0]], [-dy, a[1]], [dy, height - a[1]]]) {
        if (!p && q < 0) return;
        if (!p) continue;
        const t = q / p;
        if (p < 0) low = Math.max(low, t); else high = Math.min(high, t);
        if (low > high) return;
    }
    return [a[0] + low * dx, a[1] + low * dy, a[0] + high * dx, a[1] + high * dy];
}

// All geometry uses one metric transform. Native artwork has its own distorted
// projection and is deliberately outside this widget's coordinate system.
class SurveyCanvas {
    constructor(view, action) {
        this.view = view; this.action = action; this.scale = .035; this.camera = null;
        this.pins = []; this.layers = []; this.cutLabels = []; this.active = 0; this.dirty = true;
        const { UE, page } = view;
        this.root = view.create(UE.Border, page.WidgetTree.RootWidget);
        this.root.SetPadding(new UE.Margin(0, 0, 0, 0));
        this.root.SetBrushColor(new UE.LinearColor(...rgba("ink", 1)));
        this.root.SetClipping(UE.EWidgetClipping.ClipToBounds);
        this.slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.root); this.slot.SetZOrder(35);
        this.canvas = view.create(UE.CanvasPanel, this.root);
        this.title = view.makeText(this.canvas, 18, 22, 18, 450, 30, rgba("paper"));
        this.floor = view.makeText(this.canvas, 12, 22, 50, 450, 22, rgba("muted"));
        this.body = view.create(UE.CanvasPanel, this.canvas); this.body.SetClipping(UE.EWidgetClipping.ClipToBounds);
        this.bodySlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.body);
        this.bodySlot.SetPosition(new UE.Vector2D(20, 84));
        this.surface = view.makeRowButton(0, 0, 1, 1, "SurveySurface", () => this.click(), this.body);
        const surfaceStyle = this.surface.widget.WidgetStyle;
        for (const name of ["Normal", "Hovered", "Pressed"]) { const brush = surfaceStyle[name]; brush.TintColor = view.color(0, 0, 0, 0); surfaceStyle[name] = brush; }
        this.surface.widget.SetStyle(surfaceStyle);
        this.surface.slot.SetZOrder(30);
        this.pressed = () => { if (!this.visible) return; const p = page.getMouseLocalPosition(); this.drag = { x: p.X, y: p.Y, moved: false }; };
        this.released = () => { this.ignoreClick = !!this.drag?.moved; this.drag = null; };
        this.surface.widget.OnPressed.Add(this.pressed); this.surface.widget.OnReleased.Add(this.released);
        view.listeners.push({ widget: this.surface.widget, name: "OnPressed", handler: this.pressed }, { widget: this.surface.widget, name: "OnReleased", handler: this.released });
        for (let i = 0; i < 2; i++) {
            const canvas = view.create(UE.CanvasPanel, this.body), slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(canvas);
            canvas.SetVisibility(UE.ESlateVisibility.Collapsed);
            this.layers.push({ canvas, slot, lines: [] });
        }
        this.playerLines = [view.makeLineWidget(this.body), view.makeLineWidget(this.body)];
        for (const line of this.playerLines) line.slot.SetZOrder(24);
        this.playerHeight = view.makeText(this.body, 11, 0, 0, 90, 22, rgba("accent")); this.playerHeight.slot.SetZOrder(24);
        this.endpoint = view.makeText(this.body, 11, 0, 0, 190, 26, rgba("gold")); this.endpoint.slot.SetZOrder(24);
        this.buttons = [
            ["SurveyCenter", "center", "centerPlayer"], ["SurveyFit", "route", "fitRoute"],
            ["SurveyZoomOut", "minus", "zoomOut"], ["SurveyZoomIn", "plus", "zoomIn"]
        ].map(spec => view.makeButton(spec, 0, 20, 36, action, this.canvas));
        this.status = view.makeText(this.canvas, 12, 22, 0, 600, 24, rgba("paper"));
        this.detail = view.makeText(this.canvas, 11, 22, 0, 600, 22, rgba("muted"));
        this.scaleLabel = view.makeText(this.canvas, 10, 22, 0, 160, 20, rgba("muted"));
        this.scaleLine = view.makeLineWidget(this.canvas);
        this.show(false);
    }
    show(value) {
        this.visible = value;
        this.root.SetVisibility(value ? this.view.UE.ESlateVisibility.Visible : this.view.UE.ESlateVisibility.Collapsed);
        if (!value) this.drag = null;
    }
    update(model) {
        const view = this.view, UE = view.UE, size = view.page.getMapPageLocalSize();
        const width = Math.max(240, size.X - 404), height = Math.max(240, size.Y - 184);
        if (width !== this.width || height !== this.height) {
            this.width = width; this.height = height; this.w = width - 40; this.h = height - 180;
            this.slot.SetPosition(new UE.Vector2D(28, 100)); this.slot.SetSize(new UE.Vector2D(width, height));
            this.bodySlot.SetSize(new UE.Vector2D(this.w, this.h)); this.surface.slot.SetSize(new UE.Vector2D(this.w, this.h));
            for (const layer of this.layers) layer.slot.SetSize(new UE.Vector2D(this.w, this.h));
            for (let i = 0; i < this.buttons.length; i++) view.place(this.buttons[i], width - 166 + i * 36, 18);
            view.place(this.status, 22, height - 82); view.place(this.detail, 22, height - 55); view.place(this.scaleLabel, 22, height - 28);
            for (const node of [this.status, this.detail]) node.slot.SetSize(new UE.Vector2D(width - 44, 24));
            this.title.slot.SetSize(new UE.Vector2D(Math.max(100, width - 200), 30)); this.dirty = true;
        }
        if (!this.model || this.model.source !== model.source) { this.camera = null; this.dirty = true; }
        if (this.model?.key !== model.key) this.dirty = true;
        this.model = model;
        if (!this.camera) this.center(model.target || model.player || model.snapshot.cells[0] || { x: 0, y: 0 });
        view.textValue(this.title, model.name);
        view.textValue(this.floor, model.floorText);
        const route = model.route, target = model.target, text = view.text;
        view.textValue(this.status, target ? target.label + " · " + text[route?.state || "routeSelect"] : text.routeSelect);
        const next = route?.transitions[0];
        let detail = target && model.player ? text.height + " " + this.elevation(target.z - model.player.z) : "";
        if (route?.points.length > 1) detail += " · " + text.routeLength + " " + Math.round(route.distance / 100) + "m";
        if (next) detail += " · " + text.routeNext + " " + this.elevation(next.dz);
        else if (route?.state === "routeOtherArea") detail += " · " + (text["region_" + target.mapID.toLowerCase()] || target.mapID);
        else if (route?.approach > 100) detail += " · " + text.routeApproach + " " + Math.round(route.approach / 100) + "m";
        view.textValue(this.detail, detail);
        this.show(true);
    }
    elevation(z) { return (z > 100 ? "↑" : z < -100 ? "↓" : "") + Math.round(Math.abs(z) / 100) + "m"; }
    center(point) { if (point) { this.camera = { x: point.x, y: point.y }; this.dirty = true; } }
    fit() {
        const points = this.model?.route?.points.length ? this.model.route.points.concat(this.model.target) : [this.model?.player, this.model?.target].filter(Boolean);
        if (!points.length) return;
        const xs = points.map(p => p.x), ys = points.map(p => p.y), x1 = Math.min(...xs), x2 = Math.max(...xs), y1 = Math.min(...ys), y2 = Math.max(...ys);
        this.center({ x: (x1 + x2) / 2, y: (y1 + y2) / 2 });
        this.scale = Math.max(.002, Math.min(.15, this.w / (x2 - x1 + 1600), this.h / (y2 - y1 + 1600))); this.dirty = true;
    }
    zoom(factor) { this.scale = Math.max(.002, Math.min(.15, this.scale * factor)); this.dirty = true; }
    pan(x, y) { if (this.camera) { this.camera.x += x / this.scale; this.camera.y += y / this.scale; this.dirty = true; } }
    project(p) { return [(p.x - this.camera.x) * this.scale + this.w / 2, (p.y - this.camera.y) * this.scale + this.h / 2]; }
    click() {
        if (this.ignoreClick) { this.ignoreClick = false; return; }
        const p = this.view.page.getMouseLocalPosition(), x = p.X - 48, y = p.Y - 184;
        let best, distance = 28;
        for (const point of this.shownPoints || []) { const xy = this.project(point), d = Math.hypot(xy[0] - x, xy[1] - y); if (d < distance) { best = point; distance = d; } }
        if (best) this.action("SelectID:" + best.id);
    }
    plan() {
        const { snapshot, focus, route } = this.model, commands = [], cuts = [];
        const add = (a, b, thickness, tint) => { const line = clip(a, b, this.w, this.h); if (line && Math.hypot(line[2] - line[0], line[3] - line[1]) >= .8) commands.push([...line, thickness, tint]); };
        // Filled strips mean measured walkable coverage, not building walls.
        // Coarsening is display-only; the saved metre grid remains unchanged.
        const bin = Math.max(100, Math.ceil(3 / this.scale / 100) * 100), rows = new Map();
        for (const p of snapshot.cells) {
            const gx = Math.floor(p.x / bin), gy = Math.floor(p.y / bin);
            const xy = this.project(p); if (xy[0] < -bin * this.scale || xy[0] > this.w + bin * this.scale || xy[1] < -bin * this.scale || xy[1] > this.h + bin * this.scale) continue;
            if (!rows.has(gy)) rows.set(gy, new Set()); rows.get(gy).add(gx);
        }
        for (const [gy, xs] of rows) {
            const sorted = [...xs].sort((a, b) => a - b); let begin = sorted[0], end = begin;
            const flush = () => add(this.project({ x: begin * bin, y: (gy + .5) * bin }), this.project({ x: (end + 1) * bin, y: (gy + .5) * bin }), bin * this.scale + .4, rgba("paper", .085));
            for (const x of sorted.slice(1)) { if (x > end + 1) { flush(); begin = x; } end = x; } flush();
        }
        for (const s of snapshot.lines) {
            const a = this.project({ x: s[1], y: s[2] }), b = this.project({ x: s[4], y: s[5] });
            if (s[7] !== 4) { add(a, b, 1.2, rgba("paper", .8)); continue; }
            // Old sampling frontiers remain useful coverage data. Dashed dim
            // edges distinguish them from confirmed obstacles and route lines.
            const line = clip(a, b, this.w, this.h);
            if (!line) continue;
            const dx = line[2] - line[0], dy = line[3] - line[1], length = Math.hypot(dx, dy);
            const spacing = Math.max(12, length / 12);
            for (let d = 0; d < length; d += spacing) {
                const end = Math.min(d + spacing / 3, length);
                add([line[0] + dx * d / length, line[1] + dy * d / length], [line[0] + dx * end / length, line[1] + dy * end / length], 1, rgba("muted", .25));
            }
        }
        if (route) for (let i = 1; i < route.points.length; i++) {
            let a = route.points[i - 1], b = route.points[i];
            if (focus?.strictSlice) {
                const dz = b.z - a.z;
                if (!dz) { if (a.z < focus.minZ || a.z > focus.maxZ) continue; }
                else {
                    let low = Math.max(0, Math.min((focus.minZ - a.z) / dz, (focus.maxZ - a.z) / dz));
                    let high = Math.min(1, Math.max((focus.minZ - a.z) / dz, (focus.maxZ - a.z) / dz));
                    if (low > high) continue;
                    const mix = t => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });
                    if (low > 0) cuts.push({ ...mix(low), direction: dz > 0 ? "fromBelow" : "fromAbove" });
                    if (high < 1) cuts.push({ ...mix(high), direction: dz > 0 ? "toAbove" : "toBelow" });
                    const from = mix(low), to = mix(high); a = from; b = to;
                }
            }
            add(this.project(a), this.project(b), 2.4, rgba("gold"));
        }
        return { commands, cuts };
    }
    step() {
        if (!this.visible || !this.model) return;
        const view = this.view, UE = view.UE;
        if (this.drag) {
            const p = view.page.getMouseLocalPosition(), dx = p.X - this.drag.x, dy = p.Y - this.drag.y;
            if (Math.hypot(dx, dy) > 1) { this.pan(-dx, -dy); this.drag.moved = true; this.drag.x = p.X; this.drag.y = p.Y; }
        }
        if (this.dirty && !this.job) {
            this.dirty = false;
            this.job = { ...this.plan(), cursor: 0, layer: this.layers[1 - this.active], camera: { ...this.camera }, scale: this.scale };
        }
        if (this.job) {
            const job = this.job, started = Date.now();
            for (let n = 0; job.cursor < job.commands.length && n < 40 && Date.now() - started < 2; n++, job.cursor++) {
                if (!job.layer.lines[job.cursor]) job.layer.lines.push(view.makeLineWidget(job.layer.canvas));
                const [x1, y1, x2, y2, width, tint] = job.commands[job.cursor];
                view.drawLine(job.layer.lines[job.cursor], x1, y1, x2, y2, width, ...tint);
            }
            if (job.cursor === job.commands.length) {
                for (let i = job.cursor; i < job.layer.lines.length; i++) view.visible(job.layer.lines[i], false);
                this.layers[this.active].canvas.SetVisibility(UE.ESlateVisibility.Collapsed);
                this.active = 1 - this.active; this.geometryCamera = job.camera; this.geometryScale = job.scale; this.cuts = job.cuts;
                job.layer.canvas.SetVisibility(UE.ESlateVisibility.HitTestInvisible); this.job = null;
            }
        }
        // Move the last complete geometry while a replacement is prepared.
        if (this.geometryCamera) {
            const ratio = this.scale / this.geometryScale;
            const canvas = this.layers[this.active].canvas;
            canvas.SetRenderTransformPivot(new UE.Vector2D(.5, .5)); canvas.SetRenderScale(new UE.Vector2D(ratio, ratio));
            canvas.SetRenderTranslation(new UE.Vector2D((this.geometryCamera.x - this.camera.x) * this.scale, (this.geometryCamera.y - this.camera.y) * this.scale));
        }
        const model = this.model, focus = model.focus;
        const onFloor = p => !focus?.strictSlice || p.z >= focus.minZ && p.z <= focus.maxZ;
        this.shownPoints = model.points.filter(p => p.mapID.toLowerCase() === model.source.toLowerCase() && (onFloor(p) || p.id === model.target?.id)).filter(p => { const xy = this.project(p); return xy[0] >= 16 && xy[0] <= this.w - 16 && xy[1] >= 16 && xy[1] <= this.h - 16; });
        // Bound allocation per frame; the complete list remains selectable.
        for (let i = 0, created = 0; i < this.shownPoints.length; i++) {
            if (!this.pins[i]) { if (created++ >= 2) break; this.pins.push(view.makePinWidget(this.body)); }
            const point = this.shownPoints[i], pin = this.pins[i], xy = this.project(point);
            view.stylePin(pin, point, point.id === model.target?.id); view.place(pin, xy[0], xy[1]); view.visible(pin, true);
        }
        for (let i = this.shownPoints.length; i < this.pins.length; i++) view.visible(this.pins[i], false);
        const player = model.player;
        if (player) {
            const p = this.project(player), angle = (player.yaw || 0) * Math.PI / 180;
            const at = (a, r) => [p[0] + Math.cos(a) * r, p[1] + Math.sin(a) * r];
            const tip = at(angle, 11), left = at(angle + 2.6, 8), right = at(angle - 2.6, 8);
            view.drawLine(this.playerLines[0], ...left, ...tip, 2.5, ...rgba("accent"));
            view.drawLine(this.playerLines[1], ...right, ...tip, 2.5, ...rgba("accent"));
            view.place(this.playerHeight, p[0] + 14, p[1] - 8);
            view.textValue(this.playerHeight, this.elevation(player.z - focus.centerZ));
            view.visible(this.playerHeight, !onFloor(player));
        } else { for (const line of this.playerLines) view.visible(line, false); view.visible(this.playerHeight, false); }
        const endpoint = model.route?.endpoint;
        view.visible(this.endpoint, !!endpoint && onFloor(endpoint));
        if (endpoint) { const p = this.project(endpoint); view.place(this.endpoint, p[0] + 8, p[1] + 8); view.textValue(this.endpoint, view.text[model.route.state === "routeReady" ? "routeEnd" : "routeFrontier"]); }
        const cuts = (this.cuts || []).filter(p => { const xy = this.project(p); return xy[0] >= 0 && xy[0] < this.w - 100 && xy[1] >= 0 && xy[1] < this.h - 20; }).slice(0, 6);
        for (let i = 0; i < cuts.length; i++) {
            if (!this.cutLabels[i]) { const label = view.makeText(this.body, 11, 0, 0, 130, 22, rgba("gold")); label.slot.SetZOrder(24); this.cutLabels.push(label); }
            const p = this.project(cuts[i]); view.place(this.cutLabels[i], p[0], p[1]); view.textValue(this.cutLabels[i], view.text[cuts[i].direction]); view.visible(this.cutLabels[i], true);
        }
        for (let i = cuts.length; i < this.cutLabels.length; i++) view.visible(this.cutLabels[i], false);
        const metres = this.scale * 1000 > 60 ? 10 : this.scale * 5000 > 60 ? 50 : 100;
        view.textValue(this.scaleLabel, metres + " m");
        view.drawLine(this.scaleLine, 80, this.height - 18, 80 + metres * 100 * this.scale, this.height - 18, 1, ...rgba("muted"));
    }
    dispose() {
        this.visible = false;
        this.model = this.job = this.camera = this.drag = this.geometryCamera = null;
        this.pins = []; this.layers = []; this.cutLabels = []; this.cuts = []; this.shownPoints = [];
        this.buttons = []; this.playerLines = [];
        this.root = this.slot = this.canvas = this.body = this.bodySlot = undefined;
        this.title = this.floor = this.surface = this.playerHeight = this.endpoint = this.scaleLabel = this.scaleLine = undefined;
        this.pressed = this.released = this.view = this.action = undefined;
        // View owns the widgets and their delegates; no native calls here.
    }
}
module.exports = { SurveyCanvas };
