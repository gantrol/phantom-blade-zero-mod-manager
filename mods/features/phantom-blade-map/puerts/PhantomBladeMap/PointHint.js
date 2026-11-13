"use strict";
const Conditions = require("./Conditions");

const TEXT_FLIP_ENTER_GAP = 50;
const TEXT_FLIP_EXIT_GAP = 90;

function resolveLockSource(lockData, catalogPoints, UE) {
    if (!lockData?.widget || !catalogPoints?.length) return undefined;
    try {
        const weaponID = String(lockData.widget.ShaChiWeaponID || "");
        if (weaponID && weaponID !== "None") {
            const matches = catalogPoints.filter(p => p.reward === weaponID || (p.category === "shadowWeapon" && String(p.id).toLowerCase().includes(weaponID.toLowerCase())));
            if (matches.length) return matches.find(p => p.state !== "collected") || matches[0];
        }
        const showText = lockData.widget.ShowText ? UE.KismetTextLibrary.Conv_TextToString(lockData.widget.ShowText) : "";
        if (showText) {
            const matches = catalogPoints.filter(p => (p.category === "key" || p.category === "shadowWeapon") && p.label && showText.includes(p.label));
            if (matches.length) return matches.find(p => p.state !== "collected") || matches[0];
        }
    } catch {}
    return undefined;
}

class PointHint {
    constructor(view, open) {
        this.view = view;
        this.onOpen = open;
        this.links = [];
        this.flipped = false;
        this.anchorID = "";
    }

    update(point, parent, position, context = {}) {
        if (!point || !parent || !position) { this.hide(); return; }
        const { page, UE, cpp, text } = this.view;
        const root = page.WidgetTree.RootWidget;
        this.point = point;
        this.links = Conditions.links(point, context.chains || [], text, UE, page);
        this.lastSeen = Date.now();

        const absolute = UE.SlateBlueprintLibrary.LocalToAbsolute(parent.GetCachedGeometry(), position);
        const anchor = UE.SlateBlueprintLibrary.AbsoluteToLocal(root.GetCachedGeometry(), absolute);
        const size = page.getMapPageLocalSize();
        if (anchor.X < 0 || anchor.Y < 0 || anchor.X > size.X || anchor.Y > size.Y) { this.hide(); return; }

        if (!this.widget) {
            const widget = this.widget = page.GetOrCreateNormalWidget(page.BellTextClass);
            widget.InitStateByNormal();
            page.resetSharedTextImagesToFull?.(widget);
            root.AddChild(widget);
            this.slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
            this.slot.SetAutoSize(true);
            this.slot.SetZOrder(47);
            this.button = this.view.makeRowButton(0, 0, 1, 1, "", () => this.open(), root);
            this.button.slot.SetZOrder(48);
            this.button.widget.SetCursor(UE.EMouseCursor.Hand);
        }

        const player = context.player;
        const sameMap = player && point.mapID && String(point.mapID).toLowerCase() === String(player.mapID || "").toLowerCase() && Number.isFinite(point.x);
        const dist = sameMap ? Math.round(Math.hypot(point.x - player.x, point.y - player.y) / 100) + " m" : "";
        const isSelected = !!context.selected && point.id === context.selected;
        const isTracked = !!context.trackedID && point.id === context.trackedID;
        const isPad = !!context.isGamepad;
        const stackTotal = context.overlapTotal || 1;
        const stackIndex = context.overlapIndex || 1;

        const actionable = point.pointKind !== "lockSource" && !point.globalDocument && point.pointKind !== "pending";
        const key = JSON.stringify([
            point.id, point.pointKind, point.label, point.state, point.availability,
            point.requirement, point.requirements, point.members?.length,
            dist, isSelected, isTracked, isPad, stackIndex, stackTotal, this.links
        ]);

        const targetChanged = this.anchorID !== point.id;
        if (targetChanged) {
            this.anchorID = point.id;
            this.flipped = false;
        }

        if (key !== this.key) {
            let line1 = "", line2 = "";
            const stackTag = stackTotal > 1 ? "[" + stackIndex + "/" + stackTotal + "] " : "";
            if (point.pointKind === "lockSource") {
                const subName = point.mapID ? (text["region_" + String(point.mapID).toLowerCase()] || point.mapID) : "";
                line1 = [text.lockSourcePrefix, point.label || text[point.category]].filter(Boolean).join("：");
                const cond = point.availability ? Conditions.describe(point, text, UE, page).replace(/\n/g, " · ") : "";
                line2 = [subName, dist, text[point.state], cond, text.lockTrackSourceHint].filter(Boolean).join(" · ");
            } else if (point.pointKind === "cluster") {
                line1 = point.label || text[point.category] || text.marker;
                line2 = text.mapCluster.replace("{0}", point.members.length);
            } else {
                const stateTag = isTracked ? text.tracking
                    : point.state === "unknown" ? text.unknown
                    : point.category === "bell" ? text[point.state === "collected" ? "bellActivated" : "bellUnactivated"]
                    : "";
                line1 = stackTag + [point.label || text[point.category] || text.marker, stateTag].filter(Boolean).join(" · ");
                const cond = Conditions.describe(point, text, UE, page).replace(/\n/g, " · ");
                const subName = point.mapID ? (text["region_" + String(point.mapID).toLowerCase()] || point.mapID) : "";
                const kind = point.stage || (point.collectionKey && text[point.collectionKey] !== point.label ? text[point.collectionKey] : "");
                let actionHint = "";
                if (isSelected) {
                    if (point.category === "bell" && point.state === "collected") actionHint = text[isPad ? "bellHintPad" : "bellHintKbm"];
                    else if (isTracked) actionHint = text[isPad ? "untrackHintPad" : "untrackHintKbm"];
                    else if (!point.availability && point.state !== "collected" && point.pointKind !== "mapMarker") actionHint = text[isPad ? "trackHintPad" : "trackHintKbm"];
                } else if (stackTotal > 1) {
                    actionHint = text.overlapHint;
                }
                const meta = cond || [kind, subName, dist].filter(Boolean).join(" · ");
                line2 = [meta, actionHint].filter(Boolean).join(" · ");
            }
            if (this.links.length) {
                const linkPrompt = this.links.length === 1 ? text.questOpenPrerequisite : text.questChoosePrerequisite;
                line2 = [line2, linkPrompt].filter(Boolean).join(" · ");
            }

            try {
                if (this.widget.InputActionWidget_C_61) {
                    const showGlyph = actionable && isPad;
                    this.widget.InputActionWidget_C_61.SetRenderOpacity(showGlyph ? 1 : 0);
                    if (showGlyph && this.widget["Default Input Actions"]) {
                        this.widget.InputActionWidget_C_61.SetInputActions(this.widget["Default Input Actions"]);
                    }
                    page.applyInputFirstLineLock?.(this.widget, showGlyph);
                    if (this.widget.VerticalBox_1?.Slot?.SetPadding) {
                        this.widget.VerticalBox_1.Slot.SetPadding(new UE.Margin(showGlyph ? 0 : 7.97, 8, 5, 8));
                    }
                }
            } catch {}

            let branchApplied = false;
            try {
                const branch = this.widget.branch;
                if (branch && typeof branch.SetPBZFormatText === "function") {
                    this.widget.PBZCommonLocText_45.SetWrapTextWidth(310);
                    this.widget.InitTextByFormatText(cpp.FText.FromString(line1));
                    if (line2) {
                        const wrapQ = this.widget.WrapQuestBranch;
                        const cjk = page.isCJKLang ? page.isCJKLang() : true;
                        branch.SetWrapTextWidth(wrapQ ? (cjk ? wrapQ.X : wrapQ.Y) : 310);
                        branch.SetPBZFormatText(cpp.FText.FromString(line2));
                        branch.SetVisibility(UE.ESlateVisibility.SelfHitTestInvisible);
                    } else {
                        branch.SetVisibility(UE.ESlateVisibility.Collapsed);
                    }
                    branchApplied = true;
                }
            } catch {}

            if (!branchApplied) {
                try { this.widget.branch?.SetVisibility(UE.ESlateVisibility.Collapsed); } catch {}
                this.widget.PBZCommonLocText_45.SetWrapTextWidth(320);
                this.widget.InitTextByFormatText(cpp.FText.FromString([line1, line2].filter(Boolean).join("\n")));
            }

            this.key = key;
            this.widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
            this.widget.ForceLayoutPrepass();
            const desired = this.widget.GetDesiredSize();
            this.width = Math.max(120, desired.X);
            this.height = Math.max(42, desired.Y);
            if (targetChanged) {
                try { page.playWidgetAnimationIfAvailable?.(this.widget, "AI_MapFogClearBellTextIntro"); } catch {}
            }
        }

        try { page.syncBranchSpacerToInput?.(this.widget); } catch {}

        const safeRight = Math.max(220, Math.min(size.X - 16, (context.sidebarLeft || size.X) - 14));
        const rightGap = safeRight - (anchor.X + 24 + this.width);
        if (this.flipped) {
            if (rightGap > TEXT_FLIP_EXIT_GAP) this.flipped = false;
        } else if (this.width > 0 && rightGap < TEXT_FLIP_ENTER_GAP) {
            this.flipped = true;
        }

        const rawX = this.flipped ? anchor.X - this.width - 24 : anchor.X + 24;
        const x = Math.max(16, Math.min(rawX, safeRight - this.width));
        const yOff = point.pointKind === "lockSource" ? 34 : -this.height * 0.45;
        const y = Math.max(66, Math.min(anchor.Y + yOff, size.Y - this.height - 68));

        this.slot.SetPosition(new UE.Vector2D(x, y));
        this.widget.SetVisibility(UE.ESlateVisibility.HitTestInvisible);

        const canClickLink = this.links.length > 0 && !context.occluded && anchor.X < safeRight + 8;
        this.button.slot.SetPosition(new UE.Vector2D(x, y));
        this.button.slot.SetSize(new UE.Vector2D(this.width, this.height));
        this.view.visible(this.button, canClickLink);
        this.shown = true;
    }

    hovered() {
        return this.shown && this.button?.shown && this.button.widget.IsHovered();
    }

    open() {
        if (!this.shown || !this.links.length) return false;
        this.onOpen(this.links, this.point);
        this.hide();
        return true;
    }

    leave() {
        if (!this.hovered() && Date.now() - this.lastSeen > 220) this.hide();
    }

    chromeWidgets() {
        return this.shown && this.button?.shown ? [this.button.widget] : [];
    }

    hide() {
        if (this.widget) {
            try { this.widget.branch?.SetVisibility(this.view.UE.ESlateVisibility.Collapsed); } catch {}
            this.widget.SetVisibility(this.view.UE.ESlateVisibility.Collapsed);
        }
        if (this.button) this.view.visible(this.button, false);
        this.shown = false;
        this.anchorID = "";
    }

    dispose(detach = true) {
        if (detach && this.widget) {
            try { this.widget.branch?.SetVisibility(this.view.UE.ESlateVisibility.Collapsed); } catch {}
            this.view.page.RemoveWidget(this.widget);
        }
        if (detach && this.button) this.view.visible(this.button, false);
        this.widget = this.slot = this.button = this.view = this.onOpen = this.point = undefined;
        this.links = [];
    }
}

module.exports = { PointHint, resolveLockSource };
