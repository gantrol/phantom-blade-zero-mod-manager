"use strict";
const { rgba } = require("./Appearance");
const { FloorContext } = require("./FloorContext");
const { TextureCache } = require("./TextureCache");
const { FogOverlay } = require("./FogOverlay");
const { groupPins } = require("./PlanarPins");

class PlanarMap {
    constructor(view, parent, limit = 160, onSelect) {
        this.view = view;
        this.limit = limit;
        this.onSelect = onSelect;
        this.images = [];
        this.pins = [];
        this.pinByID = new Map();
        this.floors = new FloorContext(limit);
        this.expanded = new Set();
        this.textures = new TextureCache(view, 2 * (limit + Math.min(48, Math.ceil(limit / 2))), () => this.applyTextures());

        const UE = view.UE, widget = view.create(UE.CanvasPanel, parent), slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
        slot.SetZOrder(20);
        // HitTestInvisible so MapClickSurface (ZOrder 0) receives clicks/drags unobstructed
        this.root = { widget, slot, interactive: false };
        view.visible(this.root, false);
        this.background = view.makeRect(widget, 0, 0, 1, 1, ...rgba("ink", 0.92));
        this.fog = new FogOverlay(view, widget);
        this.player = view.makePinWidget(widget, false);
        view.visible(this.player, false);

        this.scaleBg = view.makeRect(widget, 0, 0, 1, 1, ...rgba("ink", 0.72));
        this.scaleBg.slot.SetZOrder(17);
        this.scaleBorder = view.makeRect(widget, 0, 0, 1, 1, ...rgba("gold", 0.36));
        this.scaleBorder.slot.SetZOrder(17);
        this.scale = view.makeText(widget, 11, 22, 0, 140, 20, rgba("paper"));
        this.scale.slot.SetZOrder(18);
        this.scaleLine = view.makeLineWidget(widget);
        this.scaleLine.slot.SetZOrder(18);

        if (!this.onSelect) {
            // HUD Minimap wuxia ink-scroll frame, corner ticks, North badge & off-screen target pointer
            this.miniTopRule = view.makeRect(widget, 0, 0, 1, 1.5, ...rgba("gold", 0.45));
            this.miniBotRule = view.makeRect(widget, 0, 0, 1, 1.5, ...rgba("gold", 0.38));
            this.miniLeftRule = view.makeRect(widget, 0, 0, 1, 1, ...rgba("line", 0.42));
            this.miniRightRule = view.makeRect(widget, 0, 0, 1, 1, ...rgba("line", 0.42));
            this.miniHeaderWash = view.makeRect(widget, 0, 0, 1, 26, ...rgba("ink", 0.72));
            this.miniHeaderLine = view.makeRect(widget, 8, 25, 1, 1, ...rgba("gold", 0.26));
            for (const r of [this.miniHeaderWash, this.miniHeaderLine, this.miniTopRule, this.miniBotRule, this.miniLeftRule, this.miniRightRule]) {
                r.slot.SetZOrder(17);
            }
            this.miniCorners = Array.from({ length: 8 }, () => {
                const l = view.makeLineWidget(widget);
                l.slot.SetZOrder(18);
                return l;
            });
            this.northBadge = view.makeText(widget, 10, 0, 5, 22, 18, rgba("gold", 0.92));
            this.northBadge.widget.SetJustification(UE.ETextJustify.Center);
            this.northBadge.slot.SetZOrder(18);

            this.edgePin = view.makePinWidget(widget, true);
            this.edgePin.slot.SetZOrder(18);
            this.edgePin.widget.SetRenderScale(new UE.Vector2D(0.86, 0.86));
            view.visible(this.edgePin, false);
            this.edgeArrow = [view.makeLineWidget(widget), view.makeLineWidget(widget)];
            for (const l of this.edgeArrow) {
                l.slot.SetZOrder(19);
                view.visible(l, false);
            }
        }
    }

    bounds(x, y, width, height) {
        const UE = this.view.UE, v = this.view;
        this.width = width;
        this.height = height;
        this.root.slot.SetPosition(new UE.Vector2D(x, y));
        this.root.slot.SetSize(new UE.Vector2D(width, height));
        this.background.slot.SetSize(new UE.Vector2D(width, height));

        if (!this.onSelect && this.miniTopRule) {
            this.miniHeaderWash.slot.SetSize(new UE.Vector2D(width, 26));
            this.miniHeaderLine.slot.SetSize(new UE.Vector2D(Math.max(1, width - 16), 1));
            this.miniTopRule.slot.SetSize(new UE.Vector2D(width, 1.5));
            this.miniBotRule.slot.SetPosition(new UE.Vector2D(0, height - 1.5));
            this.miniBotRule.slot.SetSize(new UE.Vector2D(width, 1.5));
            this.miniLeftRule.slot.SetSize(new UE.Vector2D(1, height));
            this.miniRightRule.slot.SetPosition(new UE.Vector2D(width - 1, 0));
            this.miniRightRule.slot.SetSize(new UE.Vector2D(1, height));
            v.place(this.northBadge, width - 24, 5);
            v.textValue(this.northBadge, v.text?.dirN || "N");

            const c = 9;
            const segs = [
                [1, 1, 1 + c, 1], [1, 1, 1, 1 + c],
                [width - 1, 1, width - 1 - c, 1], [width - 1, 1, width - 1, 1 + c],
                [1, height - 1, 1 + c, height - 1], [1, height - 1, 1, height - 1 - c],
                [width - 1, height - 1, width - 1 - c, height - 1], [width - 1, height - 1, width - 1, height - 1 - c]
            ];
            segs.forEach(([x1, y1, x2, y2], idx) => v.drawLine(this.miniCorners[idx], x1, y1, x2, y2, 1.6, ...rgba("gold", 0.72), false));
        }
    }

    project(p) {
        return {
            x: this.width / 2 + (p.x - this.camera.x) / this.camera.cm,
            y: this.height / 2 + (p.y - this.camera.y) / this.camera.cm
        };
    }

    applyTextures() {
        for (const node of this.images) {
            if (node.path && !node.loaded) {
                const texture = this.textures.get(node.path);
                if (!texture) continue;
                node.image.widget.SetBrushFromTexture(texture, false);
                node.image.widget.SetRenderOpacity(1);
                node.loaded = true;
            }
        }
    }

    world(x, y) {
        return {
            x: this.camera.x + (x - this.width / 2) * this.camera.cm,
            y: this.camera.y + (y - this.height / 2) * this.camera.cm,
            z: this.camera.z,
            mapID: this.mapID
        };
    }

    pinAt(localX, localY, prevID = "", activeID = "") {
        if (localX < 0 || localY < 0 || localX > this.width || localY > this.height) {
            return { node: undefined, peers: [] };
        }
        const enterR = 20, exitR = 27;
        const hits = [];
        for (const node of this.pinByID.values()) {
            if (!node.shown || !node.point || !node.screen) continue;
            const d = Math.hypot(localX - node.screen.x, localY - node.screen.y);
            const r = node.point.id === prevID ? exitR : enterR;
            if (d > r) continue;
            const bias = (node.point.id === prevID ? -2.5 : 0) + (node.point.id === activeID ? -1.5 : 0);
            hits.push({ node, d, score: d + bias });
        }
        if (!hits.length) return { node: undefined, peers: [] };

        hits.sort((a, b) => {
            if (Math.abs(a.score - b.score) > 3.5) return a.score - b.score;
            if ((a.node.zOrder || 0) !== (b.node.zOrder || 0)) return (b.node.zOrder || 0) - (a.node.zOrder || 0);
            return a.d - b.d;
        });
        const best = hits[0].node;
        const peers = [];
        for (const node of this.pinByID.values()) {
            if (!node.shown || !node.point || !node.screen || node.point.pointKind === "cluster" || node.point.nativeLock) continue;
            if (Math.hypot(node.screen.x - best.screen.x, node.screen.y - best.screen.y) <= 24) {
                peers.push(node);
            }
        }
        peers.sort((a, b) => (b.baseZOrder || 0) - (a.baseZOrder || 0) || (a.point.id < b.point.id ? -1 : 1));
        return { node: best, peers };
    }

    selectCenter() {
        const { node } = this.pinAt(this.width * 0.5, this.height * 0.5);
        if (node?.point && this.onSelect) {
            this.onSelect(node.point);
            return true;
        }
        return false;
    }

    chromeWidgets() {
        return [];
    }

    update(atlas, layer, camera, player, points, target, coverage, exploredOnly, showFloors = true, showFog = true, trackedID = "", hoveredID = "") {
        if (this.atlasWorld !== atlas?.world) {
            this.clear();
            this.atlasWorld = atlas?.world;
        }
        this.camera = camera;
        this.mapID = atlas?.mapID;
        const v = this.view, UE = v.UE;
        v.visible(this.root, true);

        const patches = showFloors ? this.floors.patches(atlas, layer, camera, this.width, this.height) : [];
        const active = new Set(patches.map(p => p.key)), available = this.images.filter(p => !active.has(p.key));
        for (const patch of patches) {
            let node = this.images.find(p => p.key === patch.key);
            if (!node && available.length) {
                const sameTexture = available.findIndex(p => p.path === patch.path);
                node = available.splice(sameTexture < 0 ? available.length - 1 : sameTexture, 1)[0];
            }
            if (!node) {
                const widget = v.create(UE.CanvasPanel, this.root.widget);
                widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
                node = { widget, slot: UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget), image: v.makeRect(widget, 0, 0, 1, 1, 1, 1, 1, 1) };
                this.images.push(node);
            }
            node.key = patch.key;
            const p = this.project(patch), s = patch.size / camera.cm;
            node.slot.SetPosition(new UE.Vector2D(p.x, p.y));
            node.slot.SetSize(new UE.Vector2D(s, s));
            node.slot.SetZOrder(patch.zOrder ?? (patch.ghost ? 1 : 3));
            if (node.opacity !== patch.opacity) {
                node.widget.SetRenderOpacity(patch.opacity);
                node.opacity = patch.opacity;
            }
            node.image.slot.SetPosition(new UE.Vector2D((patch.tile.x - patch.x) / camera.cm, (patch.tile.y - patch.y) / camera.cm));
            node.image.slot.SetSize(new UE.Vector2D(patch.span / camera.cm, patch.span / camera.cm));
            if (node.path !== patch.path) {
                node.path = patch.path;
                node.loaded = false;
                const texture = this.textures.get(patch.path);
                node.image.widget.SetBrushFromTexture(texture);
                node.image.widget.SetRenderOpacity(texture ? 1 : 0);
                node.loaded = !!texture;
            }
            v.visible(node, true);
        }
        for (const node of this.images) if (!active.has(node.key)) v.visible(node, false);
        this.textures.update(patches.map(p => p.path));
        this.applyTextures();

        const aliases = require("./OfflineAtlas").aliases;
        const inRegion = id => {
            const worldKey = aliases[id] || id;
            return atlas?.regionWorlds ? atlas.regionWorlds.has(worldKey) : worldKey === atlas?.world;
        };
        const minTopPad = this.onSelect ? 16 : 34;
        const candidates = (points || []).filter(p => {
            const id = String(p.mapID).toLowerCase(), s = this.project(p), samePrimary = (aliases[id] || id) === atlas?.world;
            const onFloor = p.id === target?.id || p.id === trackedID || p.category === "bell" || (p.facility
                ? samePrimary && p.layer >= 0 && (layer < 0 || p.layer === layer)
                : !samePrimary || layer < 0 || p.layer < 0 || p.layer === layer);
            return inRegion(id) && onFloor &&
                s.x >= 16 && s.x <= this.width - 16 && s.y >= minTopPad && s.y <= this.height - 16 &&
                (!exploredOnly || p.explored || coverage.visible(p) || p.knownQuestPoint || p.category === "marker" || p.category === "bell");
        }).map(point => ({
            point,
            screen: this.project(point),
            priority: point.id === target?.id ? 0
                : point.id === trackedID ? 0.5
                : point.category === "bell" ? 1
                : point.facility ? (layer < 0 || point.layer === layer ? 3 : 4)
                : (layer < 0 || point.layer === layer ? 2 : 3),
            distance: Math.hypot(point.x - camera.x, point.y - camera.y) / camera.cm - (this.pinByID.has(point.id) ? 32 : 0)
        }));

        candidates.sort((a, b) => a.priority - b.priority || a.distance - b.distance || (a.point.id < b.point.id ? -1 : a.point.id > b.point.id ? 1 : 0));
        const visible = groupPins(candidates, camera, this.width, this.height, target?.id, this.expanded, this.onSelect ? 140 : 48, trackedID);
        const activePins = new Set(visible.map(p => p.point.id));
        const freePins = [];
        for (const node of this.pins) {
            if (!activePins.has(node.point?.id)) {
                if (node.point) this.pinByID.delete(node.point.id);
                node.point = undefined;
                v.visible(node, false);
                freePins.push(node);
            }
        }

        for (const { point: p, screen } of visible) {
            let node = this.pinByID.get(p.id);
            if (!node) {
                node = freePins.pop();
                if (!node) {
                    node = v.makePinWidget(this.root.widget, true);
                    node.interactive = false;
                    this.pins.push(node);
                    node.countBg = v.makeRect(node.widget, 17, -3, 20, 15, ...rgba("ink", 0.92));
                    node.countBg.slot.SetZOrder(3);
                    node.count = v.makeText(node.widget, 9, 17, -4, 20, 15, rgba("gold"));
                    node.count.slot.SetZOrder(4);
                    node.count.widget.SetJustification(UE.ETextJustify.Center);
                }
                this.pinByID.set(p.id, node);
            }
            node.point = p;
            const px = screen.x, py = screen.y;
            node.screen = { x: px, y: py };

            const selected = p.id === target?.id;
            const isTracked = !!trackedID && p.id === trackedID;
            const isHovered = !!hoveredID && p.id === hoveredID;
            const diffFloor = p.layer >= 0 && layer >= 0 && p.layer !== layer && !selected && !isTracked;
            const ghost = diffFloor && (p.facility || p.category === "bell");

            const baseZ = p.category === "bell" ? 11 : ghost ? 5 : p.facility ? 7 : 9;
            node.baseZOrder = baseZ;
            const zOrder = isHovered || selected ? 16 : isTracked ? 14 : baseZ;
            if (node.zOrder !== zOrder) {
                node.zOrder = zOrder;
                node.slot.SetZOrder(zOrder);
            }

            const scale = isHovered ? 1.28 : selected ? 1.12 : (this.onSelect ? 1.0 : 0.90);
            if (node.appliedScale !== scale) {
                node.appliedScale = scale;
                node.widget.SetRenderScale(new UE.Vector2D(scale, scale));
            }

            v.place(node, px, py);
            v.stylePin(node, p, selected, ghost ? (p.category === "bell" ? 0.48 : 0.24) : p.dimmed && !selected && !isHovered ? 0.35 : 1, isTracked, isHovered);

            const clustered = !!p.members;
            const clampRight = px + 38 > this.width;
            v.place(node.countBg, clampRight ? -5 : 17, -3);
            v.place(node.count, clampRight ? -5 : 17, -4);
            v.visible(node.countBg, clustered);
            v.visible(node.count, clustered);
            if (clustered) v.textValue(node.count, String(p.members.length));

            const showLabel = this.onSelect && !selected && !isHovered && !clustered && p.category === "bell" && p.state === "collected" && !diffFloor && camera.cm <= 110;
            v.visible(node.text, showLabel);
            if (showLabel) {
                v.place(node.text, px + 180 > this.width ? -164 : 34, 4);
                v.textValue(node.text, p.label || v.text[p.category] || v.text.marker);
            }
            v.visible(node, true);
        }


        if (player) {
            const p = this.project(player);
            v.stylePin(this.player, { category: "player", state: player.layer === layer ? "remaining" : "collected" }, false);
            v.place(this.player, p.x, p.y);
            this.player.widget.SetRenderTransformAngle(player.yaw || 0);
            this.player.slot.SetZOrder(13);
        }
        v.visible(this.player, !!player);

        // Off-minimap tracked target edge clamping (HUD minimap only)
        if (!this.onSelect && this.edgePin) {
            const showEdge = !!target && Number.isFinite(target.x) && !activePins.has(target.id);
            v.visible(this.edgePin, showEdge);
            if (showEdge) {
                const tp = this.project(target);
                const cx = this.width * 0.5, cy = (this.height + 26) * 0.5;
                const dx = tp.x - cx, dy = tp.y - cy;
                const minX = 18, maxX = this.width - 18, minY = 38, maxY = this.height - 18;
                let t = 1;
                if (dx > 0 && cx + dx > maxX) t = Math.min(t, (maxX - cx) / dx);
                if (dx < 0 && cx + dx < minX) t = Math.min(t, (minX - cx) / dx);
                if (dy > 0 && cy + dy > maxY) t = Math.min(t, (maxY - cy) / dy);
                if (dy < 0 && cy + dy < minY) t = Math.min(t, (minY - cy) / dy);
                const ex = Math.max(minX, Math.min(maxX, cx + dx * t));
                const ey = Math.max(minY, Math.min(maxY, cy + dy * t));
                v.place(this.edgePin, ex, ey);
                v.stylePin(this.edgePin, target, false, 0.96, true, false);
                v.visible(this.edgePin.text, false);

                const ang = Math.atan2(dy, dx);
                const tipX = ex + Math.cos(ang) * 15, tipY = ey + Math.sin(ang) * 15;
                const wing1X = tipX - Math.cos(ang - 0.58) * 6, wing1Y = tipY - Math.sin(ang - 0.58) * 6;
                const wing2X = tipX - Math.cos(ang + 0.58) * 6, wing2Y = tipY - Math.sin(ang + 0.58) * 6;
                v.drawLine(this.edgeArrow[0], tipX, tipY, wing1X, wing1Y, 1.8, ...rgba("gold", 0.96), true);
                v.drawLine(this.edgeArrow[1], tipX, tipY, wing2X, wing2Y, 1.8, ...rgba("gold", 0.96), true);
            } else {
                v.visible(this.edgeArrow[0], false);
                v.visible(this.edgeArrow[1], false);
            }
        }

        // Metric scale bar: bottom-right on Floor Plan (clearing native PercentageVerticalBox), compact bottom-left on Minimap
        const idealMeters = Math.min(this.onSelect ? 120 : 68, this.width * 0.25) * camera.cm / 100;
        const decade = Math.pow(10, Math.floor(Math.log10(Math.max(0.1, idealMeters))));
        const step = [5, 2, 1].find(value => value * decade <= idealMeters) || 1;
        const meters = Number((step * decade).toPrecision(6)), barPx = meters * 100 / camera.cm;
        const boxW = Math.max(this.onSelect ? 76 : 58, barPx + 24);
        const boxH = this.onSelect ? 30 : 24;
        const sx = this.onSelect ? this.width - boxW - 14 : 10;
        const sy = this.height - boxH - (this.onSelect ? 12 : 8);

        this.scaleBg.slot.SetPosition(new UE.Vector2D(sx, sy));
        this.scaleBg.slot.SetSize(new UE.Vector2D(boxW, boxH));
        this.scaleBorder.slot.SetPosition(new UE.Vector2D(sx, sy));
        this.scaleBorder.slot.SetSize(new UE.Vector2D(boxW, 1));
        v.visible(this.scaleBg, true);
        v.visible(this.scaleBorder, true);
        v.place(this.scale, sx + 10, sy + (this.onSelect ? 2 : 1));
        v.textValue(this.scale, meters + " m");
        v.drawLine(this.scaleLine, sx + 10, sy + boxH - 6, sx + 10 + barPx, sy + boxH - 6, 1.5, ...rgba("paper", 0.9), false);

        this.fog.update(atlas, layer, camera, this.width, this.height, coverage, showFog);
    }

    clear(detach = true) {
        this.textures.clear();
        this.fog.clear(detach);
        if (detach) {
            for (const node of this.images) {
                if (node.path !== undefined) node.image.widget.SetBrushFromTexture(undefined);
                node.path = node.key = undefined;
                node.loaded = false;
                this.view.visible(node, false);
            }
            for (const node of this.pins) this.view.visible(node, false);
            if (this.edgePin) {
                this.view.visible(this.edgePin, false);
                this.view.visible(this.edgeArrow[0], false);
                this.view.visible(this.edgeArrow[1], false);
            }
            this.view.visible(this.root, false);
        }
        this.pinByID.clear();
        for (const node of this.pins) node.point = undefined;
        this.expanded.clear();
        this.atlasWorld = undefined;
        this.floors.clear();
    }

    dispose(detach = true) {
        this.clear(detach);
        this.textures.dispose();
        this.fog.dispose(false);
        this.images = [];
        this.pins = [];
        this.view = undefined;
    }
}

module.exports = { PlanarMap };
