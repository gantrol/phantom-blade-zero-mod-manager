"use strict";

// Installed after MainStart, before the first map widget requests its mixin.
// Session state belongs to the open page; shared game data stays unmodified.
const SLOT = "__phantomBladeMapSession";

function report(error) { require("./Diagnostics").record("error", { error: String(error), stack: String(error?.stack || "").slice(0, 2000) }); }
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
    for (const name of ["BP_InitWidget", "Tick", "FinishCurrentWidget", "ReleaseScriptReferences"])
        if (typeof proto[name] !== "function") throw new Error("MAP_INTERFACE_MISMATCH: " + name);
    Object.defineProperty(proto, "__pbmInstalled", { value: true });

    const maskCovered = proto.isPositionCoveredByMask;
    if (typeof maskCovered === "function") proto.isPositionCoveredByMask = function (...args) {
        if (revealing(this)) return false;
        return maskCovered.apply(this, args);
    };
    const mapUnlocked = proto.isMapUnlocked;
    if (typeof mapUnlocked === "function") proto.isMapUnlocked = function (mapID, ... rest) {
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
            try { session.tick(); }
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
    const tracking = proto.refreshTrackedHighlight;
    const surfaceClick=proto.onMapSurfaceClicked;
    if(typeof surfaceClick==="function")proto.onMapSurfaceClicked=function(...args) {
        if(this[SLOT]?.surveyVisible())return;
        return surfaceClick.apply(this,args);
    };
    // The native click surface covers Gadget pins. Its validated click and
    // controller confirm both reach this method after native input handling.
    const confirm = proto.readyToTeleport;
    if (typeof confirm === "function") proto.readyToTeleport = function (...args) {
        try { if (this[SLOT]?.confirmMapPoint()) return; }
        catch (error) { report(error); return; }
        return confirm.apply(this, args);
    };
    if (typeof tracking === "function") proto.refreshTrackedHighlight = function (...args) {
        const result = tracking.apply(this,args);
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
    // PrepareMapPageExit also clears the opening fade's black mask. It is not
    // a page-close signal; native Finish/Release are the ownership boundaries.
    for (const name of ["FinishCurrentWidget", "ReleaseScriptReferences"]) {
        const original = proto[name];
        proto[name] = function (...args) {
            try { close(this, name === "ReleaseScriptReferences" ? "slate-release" : "finish"); } catch (error) { report(error); }
            return original.apply(this, args);
        };
    }
}
module.exports = { install };
