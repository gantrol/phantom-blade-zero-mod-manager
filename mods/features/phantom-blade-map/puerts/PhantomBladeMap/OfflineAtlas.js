"use strict";
const {FloorSurfaces}=require("./FloorSurfaces");
// Immutable numeric data only. No world objects, navigation calls or journals.
const aliases = { xuesesenlin: "fujizhilin", lujiacun: "zhengdaodaying", meiguhu: "pangzhen", meigu_yiji: "huanshuyiji2",
    huanshushi: "huanshuyiji2", duanhunshan: "duanhun", duanhunya: "duanhun" };
const alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/",base64Codes=new Uint8Array(128);
for(let i=0;i<alphabet.length;i++)base64Codes[alphabet.charCodeAt(i)]=i;
// Share only authored data and numeric surface queries, never page/save state.
// Active regions are pinned; keep at most four recently visited idle worlds.
const worlds = new Map();
function trimWorlds() {
    let idle = [...worlds.values()].filter(entry => !entry.users).length;
    for (const [world, entry] of worlds) {
        if (idle <= 4) break;
        if (entry.users) continue;
        entry.surfaces?.dispose();
        worlds.delete(world); idle--;
    }
}
function acquire(world) {
    let entry = worlds.get(world);
    if (!entry) {
        const raw = require("./offline/" + world);
        const layers = raw.layers.map(layer => {
            // Decode only the LODs actually requested by the viewport.
            const tiles = layer.tiles.map(() => undefined);
            layer.tiles.forEach((encoded, lod) => Object.defineProperty(tiles, lod, {
                configurable: true,
                get() {
                    const value = ints(encoded);
                    Object.defineProperty(tiles, lod, { value, enumerable: true });
                    return value;
                }
            }));
            return { ...layer, tiles };
        });
        const groups = new Map();
        layers.forEach((layer, i) => {
            const id = layer.group || "legacy";
            let group = groups.get(id);
            if (!group) { group = { layers: [], base: i }; groups.set(id, group); }
            group.layers.push(i);
            if (layer.kind === "ground" || layer.depth < layers[group.base].depth) group.base = i;
        });
        const areas = layers.map(layer => layer.bounds).filter(Boolean);
        const bounds = areas.length ? [Math.min(...areas.map(b => b[0])), Math.min(...areas.map(b => b[1])),
            Math.max(...areas.map(b => b[2])), Math.max(...areas.map(b => b[3]))] : undefined;
        const terrain = raw.terrain ? { tiles: raw.terrain.tiles.map(ints) } : undefined;
        entry = { raw, layers, groups, bounds, terrain, users: 0 };
    }
    worlds.delete(world); worlds.set(world, entry);
    entry.users++;
    trimWorlds();
    return entry;
}
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
    const entry = acquire(world), retained = [entry];
    const surfaces = entry.surfaces ||= new FloorSurfaces(world,raw.navigationRevision,ints);
    entry.surfaceUsers = (entry.surfaceUsers || 0) + 1;
    const seenWorlds = new Set([world]), siblings = [];
    // Only the native page's map membership authorizes neighboring terrain.
    // Name similarity does not make before/after story worlds simultaneous.
    const extraIDs = Array.isArray(regionMapIDs) ? regionMapIDs.slice() : [];
    for (const otherID of extraIDs) {
        const otherKey = String(otherID || "").toLowerCase(), otherWorld = aliases[otherKey] || otherKey;
        if (!otherWorld || seenWorlds.has(otherWorld) || !index.worlds.some(w => w.world === otherWorld)) continue;
        seenWorlds.add(otherWorld);
        const sibling = acquire(otherWorld);
        retained.push(sibling);
        siblings.push({
            world: otherWorld,
            mapID: otherID,
            layers: sibling.layers,
            groups: sibling.groups,
            terrain: sibling.terrain,
            bounds: sibling.bounds
        });
    }
    let disposed = false, surfaceRevision = surfaces.revision;
    const atlas = { world, mapID, navigationRevision:raw.navigationRevision, items: raw.items,
        floorLinks: raw.floorLinks || [],
        step(){
            if (disposed) return false;
            surfaces.step();
            const changed = surfaceRevision !== surfaces.revision;
            surfaceRevision = surfaces.revision;
            return changed;
        },
        get pending(){return surfaces.pending.size>0;},
        get surfaceFailed(){return !surfaces.valid || surfaces.failed.size>0;},
        get surfaceRevision(){return surfaces.revision;},
        levels: index.levels, tilePixels: index.tilePixels,
        siblings, regionWorlds: seenWorlds,
        layers: entry.layers,
        terrain: entry.terrain,
        groups: entry.groups,
        bounds() {
            // A local floor selection must not confine panning to one building.
            const bounds=[entry.bounds,...siblings.map(s=>s.bounds)].filter(Boolean);
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
        dispose() {
            if (disposed) return;
            disposed = true;
            if (--entry.surfaceUsers === 0) surfaces.pause();
            for (const data of retained) data.users--;
            trimWorlds();
        }
    };
    return atlas;
}
module.exports = { open, aliases };
