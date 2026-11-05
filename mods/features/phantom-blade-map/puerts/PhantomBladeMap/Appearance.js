"use strict";
// Shared by the UMG view and the static design sheet. Colours are authored in sRGB.
const layout = { width: 352, panelScale: 1.25, inset: 20, rowsY: 130, rowHeight: 52, previewHeight: 154, compactHeight: 90 };
const palette = {
    ink: "#242522", panel: "#1c1d1a", paper: "#eee7d6", muted: "#b3afa0", line: "#54564c",
    accent: "#c56a54", selected: "#3b352f", gold: "#c3ad78", facility: "#7ea8a2"
};
function rgba(name, alpha = 1) {
    const hex = palette[name];
    return [1, 3, 5].map(i => {
        const v = parseInt(hex.slice(i, i + 2), 16) / 255;
        return v <= .04045 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4);
    }).concat(alpha);
}
module.exports = { layout, palette, rgba };
