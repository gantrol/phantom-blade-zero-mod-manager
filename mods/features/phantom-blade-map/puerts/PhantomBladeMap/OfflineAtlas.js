"use strict";
const {FloorSurfaces}=require("./FloorSurfaces");
// Immutable numeric data only. No world objects, navigation calls or journals.
const aliases = { xuesesenlin: "fujizhilin", lujiacun: "zhengdaodaying", meiguhu: "pangzhen", meigu_yiji: "huanshuyiji2",
    huanshushi: "huanshuyiji2", duanhunshan: "duanhun", duanhunya: "duanhun" };
const alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/",base64Codes=new Uint8Array(128);
for(let i=0;i<alphabet.length;i++)base64Codes[alphabet.charCodeAt(i)]=i;
function ints(encoded) {
    const output = new Int32Array(Math.floor(encoded.replace(/=+$/, "").length * 6 / 32));
    let bits = 0, value = 0, word = 0, byte = 0, index = 0;
    for (let i=0;i<encoded.length;i++) {
        const code=encoded.charCodeAt(i);if(code===61)break;
        value = (value << 6) | base64Codes[code]; bits += 6;
        if (bits >= 8) { bits -= 8; word |= ((value >>> bits) & 255) << (byte++ * 8);
            if (byte === 4) { output[index++] = word; byte = 0; word = 0; } }
    }
    return output;
}
function open(mapID, geometry = true, regionMapIDs = []) {
    const key = String(mapID).toLowerCase(), world = aliases[key] || key;
    const index = require("./offline/index");
    if (!index.worlds.some(w => w.world === world)) return undefined;
    const raw = require("./offline/" + world);
    if (!geometry) return {world,items:raw.items,floor(){return -1;},dispose(){}};
    const surfaces = new FloorSurfaces(world,raw.navigationRevision,ints);
    const seenWorlds = new Set([world]), siblings = [];
    // Only the native page's map membership authorizes neighboring terrain.
    // Name similarity does not make before/after story worlds simultaneous.
    const extraIDs = Array.isArray(regionMapIDs) ? regionMapIDs.slice() : [];
    for (const otherID of extraIDs) {
        const otherKey = String(otherID || "").toLowerCase(), otherWorld = aliases[otherKey] || otherKey;
        if (!otherWorld || seenWorlds.has(otherWorld) || !index.worlds.some(w => w.world === otherWorld)) continue;
        seenWorlds.add(otherWorld);
        const otherRaw = require("./offline/" + otherWorld);
        siblings.push({
            world: otherWorld,
            mapID: otherID,
            layers: (otherRaw.layers || []).map(layer => ({ ...layer, tiles: layer.tiles.map(ints) }))
        });
    }
    const atlas = { world, mapID, navigationRevision:raw.navigationRevision, items: raw.items,
        floorLinks: raw.floorLinks || [],
        step(){return surfaces.step();},
        get pending(){return surfaces.pending.size>0;},
        get surfaceFailed(){return !surfaces.valid || surfaces.failed.size>0;},
        get surfaceRevision(){return surfaces.revision;},
        levels: index.levels, tilePixels: index.tilePixels,
        siblings, regionWorlds: seenWorlds,
        layers: raw.layers.map(layer => ({ ...layer, tiles: layer.tiles.map(ints) })),
        bounds(layer) {
            const bounds=[atlas.layers[layer]?.bounds,...siblings.map(s=>s.layers.find(l=>l.kind==="ground")?.bounds)].filter(Boolean);
            if(!bounds.length)return undefined;
            return [Math.min(...bounds.map(b=>b[0])),Math.min(...bounds.map(b=>b[1])),Math.max(...bounds.map(b=>b[2])),Math.max(...bounds.map(b=>b[3]))];
        },
        floor(point) {
            return surfaces.locate(point)?.layer ?? -1;
        },
        locate(point) { return surfaces.locate(point); },
        heightAtLayer(x,y,layer,referenceZ) {
            return surfaces.locate({x,y,z:referenceZ},layer)?.z;
        },
        dispose() { surfaces.dispose(); }
    };
    return atlas;
}
module.exports = { open, aliases };
