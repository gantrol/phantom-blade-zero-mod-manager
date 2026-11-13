"use strict";

let uiHelper, uiHelperResolved = false;
const lastPlayed = new Map();

function helper() {
    if (!uiHelperResolved) {
        uiHelperResolved = true;
        try { uiHelper = require("../Library/UIHelper").default; } catch { uiHelper = undefined; }
    }
    return uiHelper;
}

function allow(channel, cooldownMs = 45) {
    const now = Date.now(), prev = lastPlayed.get(channel) || 0;
    if (now - prev < cooldownMs) return false;
    lastPlayed.set(channel, now);
    return true;
}

function mapSfx(page, key, channel, cooldownMs = 45) {
    if (!page || typeof page.playMapSfx !== "function") return;
    const evt = page.MapConfig?.[key];
    if (!evt || !allow(channel || key, cooldownMs)) return;
    try { page.playMapSfx(evt); } catch {}
}

function focusPin(page, category) {
    mapSfx(page, category === "bell" ? "BellFocusSound" : "InterestPointFocusSound", "focusPin", 55);
}

function clickPin(page) {
    mapSfx(page, "InterestPointClickSound", "clickPin", 45);
}

function switchView(page) {
    mapSfx(page, "MapToMapSound", "switchView", 70);
}

function panelToggle(page, open) {
    mapSfx(page, open ? "CompletionOpenSound" : "CompletionCloseSound", "panelToggle", 70);
}

function hoverUI(page) {
    if (!page || !allow("hoverUI", 40)) return;
    const h = helper();
    if (h && typeof h.playMouseHoverSound === "function") {
        try { h.playMouseHoverSound(page, "SFX_UI_Int_Interface_Common_Hover_WordTip"); } catch {}
    }
}

module.exports = { focusPin, clickPin, switchView, panelToggle, hoverUI };
