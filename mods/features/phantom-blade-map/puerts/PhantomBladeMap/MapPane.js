"use strict";
const { PlanarMap } = require("./PlanarMap");
const { rgba, layout } = require("./Appearance");
const { aliases } = require("./OfflineAtlas");
const Diagnostics = require("./Diagnostics");

const BAR_W = 408;
const BAR_H = 36;
const PILL_W = 284;
const PILL_H = 26;

const sameWorld = (atlas, point) => {
    const id = String(point?.mapID || "").toLowerCase();
    if (!atlas || !id) return false;
    const worldKey = aliases[id] || id;
    return atlas.regionWorlds ? atlas.regionWorlds.has(worldKey) : worldKey === atlas.world;
};

class MapPane {
    constructor(view, action) {
        this.view = view;
        this.onAction = action;
        this.camera = { x: 0, y: 0, z: 0, cm: 50 };
        this.targetCam = undefined;
        this.panRevision = 0;
        this.initialized = false;
        this.left = 20;
        this.top = 66;
        this.barScale = 1;
        this.shown = false;

        const UE = view.UE, root = view.page.WidgetTree.RootWidget;
        this.background = view.makeRect(root, 0, 0, 1, 1, ...rgba("ink", 0.76));
        this.background.slot.SetZOrder(18);
        this.frameBorder = view.makeRect(root, 0, 0, 1, 1, ...rgba("gold", 0.28));
        this.frameBorder.slot.SetZOrder(21);

        this.map = new PlanarMap(view, root, 160, p => this.handlePinConfirm(p));

        // Floor controls and map controls wrap into two rows on narrow maps.
        this.hudBar = view.create(UE.Border, root);
        this.hudBar.SetPadding(new UE.Margin(0, 0, 0, 0));
        this.hudBar.SetBrushColor(new UE.LinearColor(0, 0, 0, 0));
        this.hudBar.SetCursor(UE.EMouseCursor.Default);
        this.hudBar.SetRenderTransformPivot(new UE.Vector2D(0, 0));
        this.hudBarSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.hudBar);
        this.hudBarSlot.SetSize(new UE.Vector2D(BAR_W, BAR_H));
        this.hudBarSlot.SetZOrder(30);

        const barCanvas = view.create(UE.CanvasPanel, this.hudBar);
        this.barBackground = view.makeRect(barCanvas, 0, 0, BAR_W, BAR_H, ...rgba("panel", 0.90));
        this.barRule = view.makeBrushLine(barCanvas, 8, BAR_H - 1.5, BAR_W - 16, 1.5, ...rgba("gold", 0.42));
        this.floorLabel = view.makeText(barCanvas, 12, 12, 7, 132, 22, rgba("paper"));
        this.pageUp = view.makeButton(["PageUp", "up"], 148, 3, 30, action, barCanvas);
        this.pageDown = view.makeButton(["PageDown", "down"], 180, 3, 30, action, barCanvas);
        this.floorDivider = view.makeRect(barCanvas, 214, 9, 1, 18, ...rgba("line", 0.42));
        this.zoomInBtn = view.makeButton(["PlanZoomIn", "plus"], 219, 3, 30, action, barCanvas);
        this.zoomOutBtn = view.makeButton(["PlanZoomOut", "minus"], 251, 3, 30, action, barCanvas);
        this.centerButton = view.makeButton(["PlanCenter", "center"], 283, 3, 30, action, barCanvas);
        this.fitButton = view.makeButton(["PlanFit", "fit"], 315, 3, 30, action, barCanvas);
        this.viewDivider = view.makeRect(barCanvas, 349, 9, 1, 18, ...rgba("line", 0.42));
        // Pill parts are siblings; move a shared canvas so the label and hit area stay together.
        this.nativePillRoot = view.makeSubCanvas(barCanvas, 46, 28);
        view.setSubVisible(this.nativePillRoot, true);
        this.nativePill = view.makePillButton(this.nativePillRoot.widget, 0, 0, 46, 28, "ViewNative", view.text.nativeShort, undefined, action);
        view.stylePill(this.nativePill, { active: false, tone: "gold" });

        // Conditional cross-floor jump pill (284 x 26) below the capsule
        this.statusPill = view.create(UE.Border, root);
        this.statusPill.SetPadding(new UE.Margin(0, 0, 0, 0));
        this.statusPill.SetBrushColor(new UE.LinearColor(0, 0, 0, 0));
        this.statusPill.SetCursor(UE.EMouseCursor.Default);
        this.statusPill.SetRenderTransformPivot(new UE.Vector2D(0, 0));
        this.statusPillSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.statusPill);
        this.statusPillSlot.SetSize(new UE.Vector2D(PILL_W, PILL_H));
        this.statusPillSlot.SetZOrder(30);
        this.statusPill.SetVisibility(UE.ESlateVisibility.Collapsed);

        const pillCanvas = view.create(UE.CanvasPanel, this.statusPill);
        view.makeRect(pillCanvas, 0, 0, PILL_W, PILL_H, ...rgba("panel", 0.88));
        view.makeBrushLine(pillCanvas, 6, PILL_H - 1, PILL_W - 12, 1, ...rgba("gold", 0.34));
        this.targetStatus = view.makeText(pillCanvas, 11, 10, 3, PILL_W - 20, 20, rgba("gold"));
        this.statusButton = view.makeRowButton(0, 0, PILL_W, PILL_H, "StatusFloorJump", action, pillCanvas);

        this.crosshair = Array.from({ length: 4 }, () => {
            const l = view.makeLineWidget(this.map.root.widget);
            l.slot.SetZOrder(26);
            return l;
        });

        this.hudBar.SetVisibility(UE.ESlateVisibility.Collapsed);
    }

    handlePinConfirm(p) {
        if (!p || p.nativeLock) return false;
        if (p.pointKind === "cluster") {
            this.expandCluster(p);
            return true;
        }
        if (p.pointKind === "floorLink") {
            this.onAction("FloorLink:" + p.id);
            return true;
        }
        this.onAction((p.category === "bell" ? "Bell:" : p.facility ? "Facility:" : "SelectID:") + p.id);
        return true;
    }

    expandCluster(cluster) {
        if (!cluster?.members?.length) return;
        this.map.expanded = new Set(cluster.members.map(point => point.id));
        this.fitToBounds = false;
        this.animateTo(cluster.x, cluster.y, cluster.z, Math.max(8, this.camera.cm * 0.48));
        this.onAction("PlanRefresh");
    }

    animateTo(x, y, z, cm = this.camera.cm) {
        this.targetCam = {
            x: Number.isFinite(x) ? x : this.camera.x,
            y: Number.isFinite(y) ? y : this.camera.y,
            z: Number.isFinite(z) ? z : this.camera.z,
            cm: Math.max(8, Math.min(2400, cm))
        };
        this.panRevision++;
    }

    focus(p) {
        if (!p) return;
        this.pendingFocus = { ...p };
        this.panRevision++;
    }

    center(p) {
        this.targetCam = undefined;
        this.camera.x = p.x;
        this.camera.y = p.y;
        this.camera.z = p.z;
        this.fitToBounds = false;
    }

    fit(bounds) {
        if (!bounds) return;
        this.targetCam = undefined;
        this.fitToBounds = true;
        this.camera.x = (bounds[0] + bounds[2]) * 0.5;
        this.camera.y = (bounds[1] + bounds[3]) * 0.5;
        this.camera.cm = Math.max(
            8,
            (bounds[2] - bounds[0]) / Math.max(1, (this.map.width || 600) - 120),
            (bounds[3] - bounds[1]) / Math.max(1, (this.map.height || 500) - 160)
        );
        this.panRevision++;
    }

    clampCamera(atlas, layer) {
        const b = atlas?.bounds(layer);
        if (!b) return;
        const padX = Math.max(4500, (b[2] - b[0]) * 0.35, (this.map.width || 600) * this.camera.cm * 0.45);
        const padY = Math.max(4500, (b[3] - b[1]) * 0.35, (this.map.height || 500) * this.camera.cm * 0.45);
        this.camera.x = Math.max(b[0] - padX, Math.min(b[2] + padX, this.camera.x));
        this.camera.y = Math.max(b[1] - padY, Math.min(b[3] + padY, this.camera.y));
    }

    panByPixels(dx, dy, atlas, layer) {
        if (!dx && !dy) return false;
        this.targetCam = undefined;
        this.fitToBounds = false;
        this.camera.x -= dx * this.camera.cm;
        this.camera.y -= dy * this.camera.cm;
        this.clampCamera(atlas, layer);
        this.panRevision++;
        return true;
    }

    moveByAxis(axisX, axisY, dt, atlas, layer) {
        if (!axisX && !axisY) return false;
        const stepDt = dt > 0 && dt < 0.2 ? dt : 0.016;
        const speedPx = 680 * stepDt;
        return this.panByPixels(-axisX * speedPx, -axisY * speedPx, atlas, layer);
    }

    zoomAtPoint(zoomSteps, mousePageLocal, atlas, layer) {
        if (!zoomSteps) return false;
        this.targetCam = undefined;
        this.fitToBounds = false;
        const w = this.map.width || 600, h = this.map.height || 500;
        let lx = w * 0.5, ly = h * 0.5;
        if (mousePageLocal) {
            const rx = mousePageLocal.X - this.left, ry = mousePageLocal.Y - this.top;
            if (rx >= 0 && rx <= w && ry >= 0 && ry <= h) {
                lx = rx;
                ly = ry;
            }
        }
        const wx = this.camera.x + (lx - w * 0.5) * this.camera.cm;
        const wy = this.camera.y + (ly - h * 0.5) * this.camera.cm;
        const nextCm = Math.max(8, Math.min(2400, this.camera.cm * Math.pow(0.80, zoomSteps)));
        if (Math.abs(nextCm - this.camera.cm) < 0.001) return true;
        if (zoomSteps < 0) this.map.expanded.clear();
        this.camera.cm = nextCm;
        this.camera.x = wx - (lx - w * 0.5) * nextCm;
        this.camera.y = wy - (ly - h * 0.5) * nextCm;
        this.clampCamera(atlas, layer);
        this.panRevision++;
        return true;
    }

    tick(deltaSeconds = 0.016) {
        if (!this.shown || !this.targetCam) return false;
        const dt = deltaSeconds > 0 && deltaSeconds < 0.25 ? deltaSeconds : 0.016;
        const blend = 1 - Math.exp(-dt / 0.045);
        const dx = this.targetCam.x - this.camera.x;
        const dy = this.targetCam.y - this.camera.y;
        const dcm = this.targetCam.cm - this.camera.cm;
        if (Math.hypot(dx, dy) < this.camera.cm * 0.6 && Math.abs(dcm) < 0.15) {
            this.camera.x = this.targetCam.x;
            this.camera.y = this.targetCam.y;
            this.camera.z = this.targetCam.z;
            this.camera.cm = this.targetCam.cm;
            this.targetCam = undefined;
        } else {
            this.camera.x += dx * blend;
            this.camera.y += dy * blend;
            this.camera.cm += dcm * blend;
        }
        this.panRevision++;
        return true;
    }

    action(key, player, atlas, layer, target) {
        if (!sameWorld(atlas, player)) player = undefined;
        if (!sameWorld(atlas, target)) target = undefined;
        this.fitToBounds = false;
        const distance = this.camera.cm * Math.min(this.map.width || 500, this.map.height || 500) * 0.18;
        const move = { PlanLeft: [-1, 0], PlanRight: [1, 0], PlanUp: [0, -1], PlanDown: [0, 1] }[key];
        if (move) {
            this.targetCam = undefined;
            this.camera.x += move[0] * distance;
            this.camera.y += move[1] * distance;
            this.clampCamera(atlas, layer);
        } else if (key === "PlanZoomIn") {
            this.zoomAtPoint(1.2, undefined, atlas, layer);
        } else if (key === "PlanZoomOut") {
            this.zoomAtPoint(-1.2, undefined, atlas, layer);
        } else if (key === "PlanCenter") {
            if (player) this.focus(player);
        } else if (key === "PlanFit") {
            this.fit(atlas?.bounds(layer));
        } else if (key !== "PlanRefresh") {
            return false;
        }
        if (key !== "PlanRefresh") this.map.expanded.clear();
        this.panRevision++;
        return true;
    }

    mouseToLocal(mousePageLocal) {
        if (!mousePageLocal) return undefined;
        return { x: mousePageLocal.X - this.left, y: mousePageLocal.Y - this.top };
    }

    isUnderToolbar(localX, localY) {
        const contains = r => r && localX >= r.x - 2 && localX <= r.x + r.width + 4 &&
            localY >= r.y - 2 && localY <= r.y + r.height + 4;
        return !!(contains(this.barBounds) || (this.statusShown && contains(this.statusBounds)));
    }

    pinAtPagePos(mousePageLocal, isGamepad, prevID = "", activeID = "") {
        if (!this.shown) return { node: undefined, peers: [] };
        let lx = (this.map.width || 0) * 0.5, ly = (this.map.height || 0) * 0.5;
        if (!isGamepad) {
            const loc = this.mouseToLocal(mousePageLocal);
            if (!loc || this.isUnderToolbar(loc.x, loc.y)) return { node: undefined, peers: [] };
            lx = loc.x;
            ly = loc.y;
        }
        return this.map.pinAt(lx, ly, prevID, activeID);
    }

    syncCursor(isGamepad, hasHoveredPin) {
        const page = this.view?.page, UE = this.view?.UE;
        if (!page || !UE) return;
        const shouldShow = this.shown && !page.lastWantSystemCursor;
        if (!shouldShow) {
            if (this.cursor) this.cursor.SetVisibility(UE.ESlateVisibility.Collapsed);
            return;
        }
        if (!this.cursor && page.WB_Cursor) {
            try {
                this.cursor = page.GetOrCreateNormalWidget(page.WB_Cursor.GetClass());
                if (this.cursor) {
                    page.WidgetTree.RootWidget.AddChild(this.cursor);
                    this.cursorSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.cursor);
                    this.cursorSlot.SetAlignment(new UE.Vector2D(0.5, 0.5));
                    this.cursorSlot.SetSize(new UE.Vector2D(128, 128));
                    this.cursorSlot.SetZOrder(35);
                }
            } catch {}
        }
        if (!this.cursor || !this.cursorSlot) return;
        // This cursor sits above MapClickSurface; its children must also pass
        // clicks through so the native surface can dispatch pin confirmation.
        this.cursor.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        const pos = isGamepad
            ? new UE.Vector2D(this.left + (this.map.width || 0) * 0.5, this.top + (this.map.height || 0) * 0.5)
            : page.getMouseLocalPosition();
        if (pos) this.cursorSlot.SetPosition(pos);
        if (this.cursorFocused !== !!hasHoveredPin) {
            this.cursorFocused = !!hasHoveredPin;
            try { page.playCursorFocusAnim?.(this.cursor, this.cursorFocused); } catch {}
        }
    }

    update(atlas, layer, player, points, target, coverage, config, label, trackedID = "", hoveredID = "") {
        const v = this.view, UE = v.UE, size = v.page.getMapPageLocalSize(), scale = v.panelScale || layout.panelScale;
        const left = 20, top = 66;
        const sidebarLeft = v.sidebarLeft ? v.sidebarLeft() : (config.showList ? size.X - layout.width * scale - 20 : size.X);
        const rightLimit = sidebarLeft < size.X ? sidebarLeft - 14 : size.X - 20;
        const mapWidth = Math.max(1, rightLimit - left), mapHeight = Math.max(1, size.Y - 134);

        this.left = left;
        this.top = top;
        this.shown = true;

        this.background.slot.SetSize(new UE.Vector2D(size.X, size.Y));
        v.visible(this.background, true);
        this.frameBorder.slot.SetPosition(new UE.Vector2D(left - 1, top - 1));
        this.frameBorder.slot.SetSize(new UE.Vector2D(mapWidth + 2, 1));
        v.visible(this.frameBorder, true);

        const oldWidth = this.map.width, oldHeight = this.map.height;
        this.map.bounds(left, top, mapWidth, mapHeight);
        this.layoutToolbar(scale);

        if (!sameWorld(atlas, player)) player = undefined;
        if (!sameWorld(atlas, target)) target = undefined;
        const worldChanged = this.world !== atlas?.world, layerChanged = this.layer !== layer, b = atlas?.bounds(layer);
        const focus = sameWorld(atlas, this.pendingFocus) ? this.pendingFocus : undefined;
        const outside = b && (this.camera.x < b[0] || this.camera.x > b[2] || this.camera.y < b[1] || this.camera.y > b[3]);

        if (focus || worldChanged || !this.initialized || (layerChanged && outside)) {
            const anchor = focus || (player?.layer === layer ? player : undefined) || (target?.layer === layer ? target : undefined);
            if (anchor) {
                if (!worldChanged && this.initialized && focus && Math.hypot(focus.x - this.camera.x, focus.y - this.camera.y) < 36000) {
                    this.animateTo(focus.x, focus.y, focus.z, this.camera.cm);
                } else {
                    this.center(anchor);
                    if (worldChanged) this.camera.cm = 50;
                }
            } else {
                this.fit(b);
            }
            Diagnostics.record("map-pane-frame", {
                world: atlas?.world, layer,
                focus: focus ? "selection" : anchor && anchor === player ? "player" : anchor && anchor === target ? "target" : "bounds",
                camera: { ...this.camera }
            });
        } else if (this.fitToBounds && (oldWidth !== this.map.width || oldHeight !== this.map.height)) {
            this.fit(b);
        }

        this.pendingFocus = undefined;
        this.world = atlas?.world;
        this.layer = layer;
        this.initialized = true;

        this.centerButton.widget.SetIsEnabled(!!player);
        this.fitButton.widget.SetIsEnabled(!!b);
        const storey = l => (l.kind === "under" ? -l.depth : l.depth);
        const floors = (atlas?.layers || []).map((l, i) => ({ i, height: storey(l) })).sort((a, b) => b.height - a.height);
        const floorIndex = floors.findIndex(f => f.i === layer);
        this.pageUp.widget.SetIsEnabled(floorIndex > 0);
        this.pageDown.widget.SetIsEnabled(floorIndex >= 0 && floorIndex < floors.length - 1);

        const heightKey = [layer, Math.round(this.camera.x), Math.round(this.camera.y), atlas?.surfaceRevision].join(":");
        if (heightKey !== this.heightKey) {
            this.heightKey = heightKey;
            this.camera.z = atlas?.heightAtLayer(this.camera.x, this.camera.y, layer, this.camera.z) ?? this.camera.z;
        }

        coverage.sample(v.page, UE, () =>
            this.map.update(atlas, layer, this.camera, player, points, target, coverage, config.exploredOnly, config.showFloors, config.showFog, trackedID, hoveredID)
        );

        const cx = this.map.width * 0.5, cy = this.map.height * 0.5;
        [[-8, 0, -3, 0], [3, 0, 8, 0], [0, -8, 0, -3], [0, 3, 0, 8]].forEach(([a, b2, c, d], i) =>
            v.drawLine(this.crosshair[i], cx + a, cy + b2, cx + c, cy + d, 1, ...rgba("muted", 0.45), false)
        );

        v.textValue(this.floorLabel, atlas ? label : v.text.offlineMissing);

        const tIdx = target ? atlas?.floor(target) : -1, targetFloor = tIdx >= 0 ? atlas?.layers[tIdx] : undefined;
        const pIdx = player?.layer ?? -1, playerFloor = pIdx >= 0 ? atlas?.layers[pIdx] : undefined;
        let statusText = "";
        if (targetFloor && tIdx !== layer) {
            statusText = v.text.targetFloor + " · " + v.text[targetFloor.kind] + (targetFloor.depth ? " " + targetFloor.depth : "") + " · " + (v.text.clickSwitchFloor || "") + " ›";
        } else if (playerFloor && pIdx !== layer) {
            statusText = v.text.playerFloor + " · " + v.text[playerFloor.kind] + (playerFloor.depth ? " " + playerFloor.depth : "") + " · " + (v.text.clickSwitchFloor || "") + " ›";
        }
        v.textValue(this.targetStatus, statusText);
        this.statusShown = !!statusText;
        this.statusPill.SetVisibility(this.statusShown ? UE.ESlateVisibility.Visible : UE.ESlateVisibility.Collapsed);
    }

    layoutToolbar(scale) {
        const v = this.view, UE = v.UE;
        const margin = Math.min(12, this.map.width / 4), available = this.map.width - margin * 2;
        const wrap = available < BAR_W * scale;
        const width = wrap ? 222 : BAR_W, height = wrap ? BAR_H * 2 : BAR_H;
        const s = Math.min(scale, available / width, Math.max(1, this.map.height - 20) / (height + 4 + PILL_H));
        this.hudBar.SetVisibility(UE.ESlateVisibility.Visible);
        const key = [this.left, this.top, width, height, s, available].join(":");
        if (key === this.toolbarKey) return;
        this.barScale = s;
        this.hudBarSlot.SetSize(new UE.Vector2D(width, height));
        this.barBackground.slot.SetSize(new UE.Vector2D(width, height));
        v.place(this.barRule, 8, height - 1.5);
        this.barRule.slot.SetSize(new UE.Vector2D(width - 16, 1.5));
        v.visible(this.floorDivider, !wrap);
        [this.zoomInBtn, this.zoomOutBtn, this.centerButton, this.fitButton].forEach((button, i) =>
            v.place(button, (wrap ? 12 : 219) + i * 32, (wrap ? BAR_H : 0) + 3));
        v.place(this.viewDivider, wrap ? 155 : 349, (wrap ? BAR_H : 0) + 9);
        v.place(this.nativePillRoot, wrap ? 166 : 354, (wrap ? BAR_H : 0) + 4);
        this.hudBar.SetRenderScale(new UE.Vector2D(s, s));
        this.hudBarSlot.SetPosition(new UE.Vector2D(this.left + margin, this.top + 10));

        const statusScale = Math.min(s, available / PILL_W);
        this.statusPill.SetRenderScale(new UE.Vector2D(statusScale, statusScale));
        this.statusPillSlot.SetPosition(new UE.Vector2D(this.left + margin, this.top + 10 + (height + 4) * s));
        this.barBounds = { x: margin, y: 10, width: width * s, height: height * s };
        this.statusBounds = { x: margin, y: 10 + (height + 4) * s, width: PILL_W * statusScale, height: PILL_H * statusScale };
        this.toolbarKey = key;
    }

    chromeWidgets() {
        if (!this.shown) return [];
        return [this.hudBar, this.statusShown ? this.statusPill : undefined].filter(Boolean);
    }

    clear(detach = true) {
        this.shown = false;
        this.statusShown = false;
        this.targetCam = undefined;
        this.map.clear(detach);
        if (this.view) {
            const UE = this.view.UE;
            this.hudBar?.SetVisibility(UE.ESlateVisibility.Collapsed);
            this.statusPill?.SetVisibility(UE.ESlateVisibility.Collapsed);
            this.cursor?.SetVisibility(UE.ESlateVisibility.Collapsed);
            if (detach) {
                this.view.visible(this.background, false);
                this.view.visible(this.frameBorder, false);
            }
        }
    }

    dispose(detach = true) {
        if (detach && this.cursor) {
            try { this.view?.page?.RemoveWidget?.(this.cursor); } catch {}
        }
        this.cursor = this.cursorSlot = undefined;
        this.clear(detach);
        this.map.dispose(false);
        this.hudBar = this.hudBarSlot = this.statusPill = this.statusPillSlot = undefined;
        this.view = this.onAction = undefined;
    }
}

module.exports = { MapPane };
