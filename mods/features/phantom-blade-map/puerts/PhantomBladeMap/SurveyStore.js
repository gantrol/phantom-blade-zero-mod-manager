"use strict";
const { createJournal } = require("./provider/Journal");
const { createEasyCsvIO } = require("./provider/EasyCsvIO");
const { resolveUserRoot } = require("./provider/StorageRoot");
const Diagnostics = require("./Diagnostics");
const spacing = 100, tileCells = 32;
function hash(value) {
    let result = 2166136261;
    for (let i = 0; i < value.length; i++) result = Math.imul(result ^ value.charCodeAt(i), 16777619);
    return (result >>> 0).toString(16);
}
function open(UE, puerts, profile, region, player) {
    const tiles = new Map(), pending = [], dirty = new Set();
    let revision = 0, generation = 0, indexDirty = false, storageError = "", index, root, legacyComplete = false;
    const namespace = profile + "|" + region;
    function journal(id) { return createJournal(createEasyCsvIO(UE, puerts, root, id, path => UE.BlueprintPathsLibrary.FileExists(path)), id); }
    function fail(error) {
        storageError = String(error); dirty.clear(); indexDirty = false;
        Diagnostics.record("survey-storage-error", { region, error: storageError });
    }
    function tileFor(mapID, gx, gy) {
        const tx = Math.floor(gx / tileCells), ty = Math.floor(gy / tileCells), key = mapID.toLowerCase() + ":" + tx + ":" + ty;
        if (!tiles.has(key)) tiles.set(key, { key, mapID, tx, ty, cells: new Map(), loaded: true, revision: 0 });
        return tiles.get(key);
    }
    function put(mapID, gx, gy, z, layer = -1, closed = 0, links = 0, persist = true, px, py) {
        const tile = tileFor(mapID, gx, gy);
        if (!tile.loaded) loadTile(tile);
        const gz = Math.round(z / 180);
        let key = gx + ":" + gy + ":" + gz, cell;
        for (const candidate of [gz, gz - 1, gz + 1]) {
            const candidateKey = gx + ":" + gy + ":" + candidate, found = tile.cells.get(candidateKey);
            if (found && Math.abs(found.z - z) < 90) { key = candidateKey; cell = found; break; }
        }
        const changed = !cell || cell.z !== z || layer >= 0 && cell.layer !== layer || (cell.closed | closed) !== cell.closed || (cell.links | links) !== cell.links || px !== undefined && (cell.px !== px || cell.py !== py);
        if (!cell) { cell = { gx, gy, z, x: gx * spacing, y: gy * spacing, mapID, layer, closed, links, tile: tile.key }; tile.cells.set(key, cell); }
        else { cell.z = z; if (layer >= 0) cell.layer = layer; cell.closed |= closed; cell.links |= links; }
        if (px !== undefined) { cell.px = px; cell.py = py; }
        if (persist && changed) { dirty.add(tile.key); generation++; }
        return cell;
    }
    function loadTile(tile) {
        if (tile.loaded) return;
        const io = journal("PhantomBladeSurveyTile_" + hash(namespace + "|" + tile.key)), loaded = io.load(), values = loaded.record?.values;
        tile.loaded = true; tile.journal = loaded.blocked ? undefined : io; tile.revision = loaded.record?.revision || 0;
        if (values) {
            if (values.profile !== profile || values.region !== region || values.key !== tile.key || values.spacing !== spacing) throw new Error("SURVEY_TILE_IDENTITY");
            for (const row of values.cells || []) put(tile.mapID, row[0], row[1], row[2], row[3], row[4], row[5], false, row[6], row[7]);
        }
        generation++;
        if (loaded.blocked) fail("SURVEY_TILE_RECOVERY_REQUIRED");
    }
    try {
        if (profile) {
            root = resolveUserRoot(UE); index = journal("PhantomBladeSurveyIndex_" + hash(namespace));
            const loaded = index.load(), values = loaded.record?.values;
            revision = loaded.record?.revision || 0;
            if (loaded.blocked) { index = undefined; fail("SURVEY_INDEX_RECOVERY_REQUIRED"); }
            if (values) {
                if (values.profile !== profile || values.region !== region) throw new Error("SURVEY_INDEX_IDENTITY");
                for (const [mapID, tx, ty] of values.tiles || []) {
                    const tile = tileFor(mapID, tx * tileCells, ty * tileCells); tile.loaded = false; tile.persisted = true; pending.push(tile);
                }
                const distance = t => player ? Math.hypot((t.tx + .5) * tileCells * spacing - player.x, (t.ty + .5) * tileCells * spacing - player.y) : 0;
                pending.sort((a, b) => distance(a) - distance(b));
            }
            legacyComplete = !!values?.legacyComplete;
            if (!legacyComplete) {
                // Keep legacy files intact. Old samples prove coverage, not walls.
                const legacy = journal("PhantomBladeSurvey_" + hash(namespace)).load().record?.values;
                if (legacy?.profile === profile && legacy.region === region && legacy.spacing === spacing)
                    for (const [x, y, z, mapID] of legacy.cells || []) {
                        // An interrupted migration resumes only missing tiles; saved
                        // tiles already contain the complete legacy tile plus new work.
                        const tile = tileFor(mapID, Math.round(x / spacing), Math.round(y / spacing));
                        if (!tile.persisted) put(mapID, Math.round(x / spacing), Math.round(y / spacing), z);
                    }
                if (!dirty.size) { legacyComplete = true; indexDirty = true; }
            }
        }
    } catch (error) { fail(error); }
    return {
        get generation() { return generation; },
        get count() { let count = 0; for (const tile of tiles.values()) count += tile.cells.size; return count; },
        get storageError() { return storageError; },
        get loading() { return pending.length > 0; },
        put,
        find(mapID, gx, gy, z) {
            const tile = tiles.get(mapID.toLowerCase() + ":" + Math.floor(gx / tileCells) + ":" + Math.floor(gy / tileCells));
            if (!tile?.loaded) return;
            const gz = Math.round(z / 180);
            for (const bin of [gz, gz - 1, gz + 1]) { const cell = tile.cells.get(gx + ":" + gy + ":" + bin); if (cell && Math.abs(cell.z - z) < 90) return cell; }
        },
        edge(cell, direction, blocked) {
            const bit = 1 << direction, before = cell.closed + ":" + cell.links;
            if (blocked) { cell.closed |= bit; cell.links &= ~bit; }
            else { cell.links |= bit; cell.closed &= ~bit; }
            if (before !== cell.closed + ":" + cell.links) { dirty.add(cell.tile); generation++; }
        },
        values() { return [...tiles.values()].flatMap(tile => [...tile.cells.values()]); },
        stepIO(flush = true) {
            // UE's CSV API is synchronous: one small transaction per open-map
            // frame. Disposal never writes to disk or retains an Unreal object.
            if (storageError || !index) return false;
            try {
                if (indexDirty && flush) {
                    index.save({ schemaVersion: 2, revision: ++revision, values: { profile, region, legacyComplete, tiles: [...tiles.values()].filter(t => t.persisted).map(t => [t.mapID, t.tx, t.ty]) } });
                    indexDirty = false; return false;
                }
                if (pending.length) { loadTile(pending.shift()); return true; }
                if (dirty.size && flush) {
                    const key = dirty.values().next().value, tile = tiles.get(key);
                    if (!tile.journal) {
                        tile.loaded = false; loadTile(tile);
                        if (storageError) return false;
                    }
                    tile.journal.save({ schemaVersion: 2, revision: ++tile.revision, values: { profile, region, key, spacing,
                        cells: [...tile.cells.values()].map(p => [p.gx, p.gy, p.z, p.layer, p.closed, p.links, p.px, p.py]) } });
                    dirty.delete(key);
                    if (!tile.persisted) { tile.persisted = true; indexDirty = true; }
                    if (!dirty.size && !legacyComplete) { legacyComplete = true; indexDirty = true; }
                }
            } catch (error) { fail(error); }
            return false;
        },
        dispose() { tiles.clear(); pending.length = 0; dirty.clear(); index = undefined; }
    };
}
module.exports = { open, spacing };
