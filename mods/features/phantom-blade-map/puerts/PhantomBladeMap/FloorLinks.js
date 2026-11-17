"use strict";
const { aliases } = require("./OfflineAtlas");

function sameWorld(atlas, point) {
    const id = String(point.mapID || "").toLowerCase();
    return (aliases[id] || id) === atlas.world;
}
function floorName(atlas, layer, text) {
    const value = atlas.layers[layer];
    return value ? text[value.kind] + (value.depth ? " " + value.depth : "") : text.floorUnknown;
}
function points(atlas, layer, facilities, text, selectedLayers = new Set([layer])) {
    if (!atlas || layer < 0) return [];
    const output = [];
    const add = (id, from, to, category, evidence) => {
        if (!selectedLayers.has(from.layer) || to.layer < 0 || to.layer === from.layer) return;
        const floor = atlas.layers[to.layer], sourceFloor = atlas.layers[from.layer];
        if (!floor || !sourceFloor || floor.group !== sourceFloor.group) return;
        const height = l => l.kind === "under" ? -l.depth : l.depth;
        const up = height(floor) > height(sourceFloor);
        output.push({ ...from, id: "floorLink:" + atlas.world + ":" + id,
            pointKind: "floorLink", facility: true, category, state: "remaining", evidence,
            destination: { ...to, mapID: atlas.mapID }, icon: category + (up ? "Upper" : "Lower"),
            label: (up ? "▲ " : "▼ ") + floorName(atlas, to.layer, text) });
    };
    for (const link of atlas.floorLinks) {
        const point = row => ({ x: row[0], y: row[1], z: row[2], layer: row[3], mapID: atlas.mapID });
        const a = point(link.a), b = point(link.b);
        add(link.id + ":a", a, b, "stairs", link.evidence);
        add(link.id + ":b", b, a, "stairs", link.evidence);
    }
    const groups = new Map();
    for (const point of facilities || []) {
        if (!point.pairedEndpoint || !sameWorld(atlas, point)) continue;
        if (!groups.has(point.facilityID)) groups.set(point.facilityID, []);
        groups.get(point.facilityID).push(point);
    }
    for (const [id, pair] of groups) {
        if (pair.length !== 2) continue;
        const [a, b] = pair.map(p => ({ ...p, layer: atlas.floor(p) }));
        add("facility:" + id + ":a", a, b, a.category, "native-paired-anchors");
        add("facility:" + id + ":b", b, a, b.category, "native-paired-anchors");
    }
    return output;
}
module.exports = { points, floorName };
