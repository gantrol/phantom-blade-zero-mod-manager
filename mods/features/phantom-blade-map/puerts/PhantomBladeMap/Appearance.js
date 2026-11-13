"use strict";
// Shared by the UMG view and the static design sheet. Colours are authored in sRGB.
const layout = { width: 356, panelHeight: 628, panelScale: 1.22, inset: 18, rowsY: 132, rowHeight: 52, previewHeight: 150, compactHeight: 46 };
const palette = {
    ink: "#110e0d", panel: "#171311", wash: "#211b18", paper: "#f2ece1", muted: "#a89d8e", line: "#6e5d4b",
    accent: "#c94136", selected: "#38231c", hover: "#2a201b", gold: "#d6b06a", facility: "#8ca8a2"
};
function rgba(name, alpha = 1) {
    const hex = palette[name];
    return [1, 3, 5].map(i => {
        const v = parseInt(hex.slice(i, i + 2), 16) / 255;
        return v <= .04045 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4);
    }).concat(alpha);
}
module.exports = { layout, palette, rgba };
