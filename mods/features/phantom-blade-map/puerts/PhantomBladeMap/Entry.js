"use strict";

// Installed after MainStart, before the first map widget requests its mixin.
// Session state belongs to the open page; shared game data stays unmodified.
const SLOT = "__phantomBladeMapSession";

function report(error) {
    require("./Diagnostics").record("error", {
        error: String(error),
        stack: String(error?.stack || "").slice(0, 2000)
    });
}

function close(page, reason = "finish") {
    const session = page[SLOT];
    page[SLOT] = undefined;
    page.__pbmRevealUnexplored = false;
    if (session) session.dispose(reason);
}

function revealing(page) {
    const session = page?.[SLOT];
    return session ? session.revealUnexplored() : !!page?.__pbmRevealUnexplored;
}

function install(MapInterface) {
    const proto = MapInterface.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__pbmInstalled")) return;
    for (const name of ["BP_InitWidget", "Tick", "FinishCurrentWidget", "ReleaseScriptReferences"]) {
        if (typeof proto[name] !== "function") throw new Error("MAP_INTERFACE_MISMATCH: " + name);
    }
    Object.defineProperty(proto, "__pbmInstalled", { value: true });

    const teleportTip = proto.showTeleportTip;
    if (typeof teleportTip === "function") proto.showTeleportTip = function (textKey, ...args) {
        if (textKey === "UI_Map_33") {
            try { require("./TeleportDiagnostics").record(this); }
            catch (error) { report(error); }
        }
        return teleportTip.call(this, textKey, ...args);
    };

    const maskCovered = proto.isPositionCoveredByMask;
    if (typeof maskCovered === "function") proto.isPositionCoveredByMask = function (...args) {
        if (revealing(this)) return false;
        return maskCovered.apply(this, args);
    };

    const mapUnlocked = proto.isMapUnlocked;
    if (typeof mapUnlocked === "function") proto.isMapUnlocked = function (mapID, ...rest) {
        if (revealing(this) && mapID && String(mapID).toLowerCase() !== "none") return true;
        return mapUnlocked.call(this, mapID, ...rest);
    };

    const bigMapUnlocked = proto.isBigMapUnlocked;
    if (typeof bigMapUnlocked === "function") proto.isBigMapUnlocked = function (bigMapID, ...rest) {
        if (revealing(this) && bigMapID && String(bigMapID).toLowerCase() !== "none") return true;
        return bigMapUnlocked.call(this, bigMapID, ...rest);
    };

    const init = proto.BP_InitWidget;
    proto.BP_InitWidget = function (...args) {
        try { close(this, "reinitialize"); } catch (error) { report(error); }
        try { this.__pbmRevealUnexplored = require("./Session").shouldRevealUnexplored(); } catch (error) { report(error); }
        const result = init.apply(this, args);
        try { this[SLOT] = require("./Session").open(this); } catch (error) { report(error); }
        return result;
    };

    const tick = proto.Tick;
    proto.Tick = function (...args) {
        const result = tick.apply(this, args);
        const session = this[SLOT];
        if (session) {
            try { session.tick(args[1]); }
            catch (error) {
                report(error);
                try { close(this, "error"); } catch (cleanupError) { report(cleanupError); }
            }
        }
        return result;
    };

    const chrome = proto.getChromeWidgets;
    if (typeof chrome === "function") proto.getChromeWidgets = function (...args) {
        return chrome.apply(this, args).concat(this[SLOT]?.chromeWidgets() || []);
    };

    // Mouse wheel zoom routing: sidebar/filter wheel scroll first, then Floor Plan zoom-to-cursor
    const zoomAtMouse = proto.zoomToScaleAtMouse;
    if (typeof zoomAtMouse === "function") proto.zoomToScaleAtMouse = function (newScale, ...rest) {
        const session = this[SLOT];
        if (session) {
            try {
                const rate = (this.MaxSizeScale - this.MinSizeScale) * 0.1;
                if (session.onMouseWheelZoom(newScale, this.MapSizeScale, rate)) return;
            } catch (error) { report(error); }
        }
        return zoomAtMouse.call(this, newScale, ...rest);
    };

    // Gamepad trigger zoom on Floor Plan
    const zoomToScale = proto.ZoomToScale;
    if (typeof zoomToScale === "function") proto.ZoomToScale = function (newScale, ...rest) {
        const session = this[SLOT];
        if (session && session.surveyVisible()) {
            try {
                const rate = (this.MaxSizeScale - this.MinSizeScale) * 0.1;
                if (session.onGamepadZoom(newScale, this.MapSizeScale, rate)) return;
            } catch (error) { report(error); }
        }
        return zoomToScale.call(this, newScale, ...rest);
    };

    // Independent LMB drag panning on Floor Plan via C++ FMapInputProcessor callback
    const moveCursor = proto.OnMoveCurosr;
    if (typeof moveCursor === "function") proto.OnMoveCurosr = function (lastPos, currentDesktopPos, ...rest) {
        const session = this[SLOT];
        if (session?.surveyVisible()) {
            try {
                if (this.mapPressArmed === true && this.mapPressDragged !== true && this.isMapPressDisplaced?.(currentDesktopPos)) {
                    this.mapPressDragged = true;
                }
                if (!this.CanUIInput?.() || this.pressStartedInsidePage !== true) return;
                const localPos = this.getMouseLocalPosition();
                if (this.wantSystemCursorNow?.()) {
                    this.lastMousePosition = localPos;
                    return;
                }
                if (this.lastMousePosition) {
                    const dx = localPos.X - this.lastMousePosition.X;
                    const dy = localPos.Y - this.lastMousePosition.Y;
                    this.lastMousePosition = localPos;
                    session.panSurveyByPixels(dx, dy);
                } else {
                    this.lastMousePosition = localPos;
                }
            } catch (error) { report(error); }
            return;
        }
        return moveCursor.call(this, lastPos, currentDesktopPos, ...rest);
    };

    // Gamepad stick & edge panning on Floor Plan
    const moveX = proto.MoveX;
    if (typeof moveX === "function") proto.MoveX = function (axisValue, ...rest) {
        const session = this[SLOT];
        if (session?.surveyVisible()) {
            try {
                if (Math.abs(axisValue) >= 0.35) session.moveSurveyAxis(axisValue, 0);
            } catch (error) { report(error); }
            return;
        }
        return moveX.call(this, axisValue, ...rest);
    };

    const moveY = proto.MoveY;
    if (typeof moveY === "function") proto.MoveY = function (axisValue, ...rest) {
        const session = this[SLOT];
        if (session?.surveyVisible()) {
            try {
                if (Math.abs(axisValue) >= 0.35) session.moveSurveyAxis(0, -axisValue);
            } catch (error) { report(error); }
            return;
        }
        return moveY.call(this, axisValue, ...rest);
    };

    const checkMouseBox = proto.CheckMouseBox;
    if (typeof checkMouseBox === "function") proto.CheckMouseBox = function (...args) {
        if (this[SLOT]?.surveyVisible()) {
            try {
                this.dropFinishedMapPress?.();
                this.SlotWidgetGroup?.ClearSelection?.();
            } catch (error) { report(error); }
            return;
        }
        return checkMouseBox.apply(this, args);
    };

    const surfaceClick = proto.onMapSurfaceClicked;
    if (typeof surfaceClick === "function") proto.onMapSurfaceClicked = function (...args) {
        const session = this[SLOT];
        if (session?.surveyVisible()) {
            try {
                const armed = this.mapPressArmed === true;
                this.mapPressArmed = false;
                const UE = require("ue");
                const releasePos = UE.WidgetLayoutLibrary.GetMousePositionOnPlatform();
                const heldMs = Date.now() - (this.mapPressStartMs || 0);
                const dragged = this.mapPressDragged === true || !!this.isMapPressDisplaced?.(releasePos);
                if (!armed || heldMs >= 500 || dragged) return;
                if (!this.CanUIInput?.() || this.wantSystemCursorNow?.()) return;
                session.onSurveySurfaceClick();
            } catch (error) { report(error); }
            return;
        }
        return surfaceClick.apply(this, args);
    };

    // Native click surface & controller confirm both reach readyToTeleport
    const confirm = proto.readyToTeleport;
    if (typeof confirm === "function") proto.readyToTeleport = function (...args) {
        try { if (this[SLOT]?.confirmMapPoint()) return; }
        catch (error) { report(error); return; }
        return confirm.apply(this, args);
    };

    // Authentic WB_Cursor Focus animation on MOD pins
    const cursorOverIcon = proto.isCursorOverIcon;
    if (typeof cursorOverIcon === "function") proto.isCursorOverIcon = function (inputType, ...rest) {
        const nativeOver = cursorOverIcon.call(this, inputType, ...rest);
        if (nativeOver) return true;
        try { return !!(this[SLOT]?.isCursorOverModPin(inputType)); }
        catch (error) { report(error); return false; }
    };

    // Ordered Back-stack navigation (Filter -> Quest subchain -> Floor Plan -> Native Exit)
    const backInput = proto.BackInput;
    if (typeof backInput === "function") proto.BackInput = function (...args) {
        if (!this.isOpenCompletion?.() && this.CanUIInput?.()) {
            try { if (this[SLOT]?.handleBack()) return; }
            catch (error) { report(error); }
        }
        return backInput.apply(this, args);
    };

    const resetView = proto.resetMapView;
    if (typeof resetView === "function") proto.resetMapView = function (...args) {
        try { if (this[SLOT]?.resetSurveyView()) return; }
        catch (error) { report(error); }
        return resetView.apply(this, args);
    };

    const tracking = proto.refreshTrackedHighlight;
    if (typeof tracking === "function") proto.refreshTrackedHighlight = function (...args) {
        const result = tracking.apply(this, args);
        this[SLOT]?.invalidateTracking();
        return result;
    };

    const keyDown = proto.OnPreviewKeyDown;
    proto.OnPreviewKeyDown = function (geometry, event) {
        const session = this[SLOT];
        if (session) {
            try { if (session.key(event)) return require("ue").WidgetBlueprintLibrary.Handled(); }
            catch (error) { report(error); }
        }
        return keyDown ? keyDown.call(this, geometry, event) : require("ue").WidgetBlueprintLibrary.Unhandled();
    };

    // Native Finish/Release are the ownership boundaries
    for (const name of ["FinishCurrentWidget", "ReleaseScriptReferences"]) {
        const original = proto[name];
        proto[name] = function (...args) {
            try { close(this, name === "ReleaseScriptReferences" ? "slate-release" : "finish"); } catch (error) { report(error); }
            return original.apply(this, args);
        };
    }
}

module.exports = { install };
