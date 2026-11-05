"use strict";
const NavSurvey = require("./NavSurvey");
const Diagnostics = require("./Diagnostics");
const atlasMapBySubmap = {
    xigu: "xigu",
    fujizhilin: "fujizhilin",
    xuesesenlin: "fujizhilin",
    pangzhen: "pangzhen",
    houjie: "houjie",
    meigu_yiji: "huanshuyiji2",
    huanshushi: "huanshuyiji2",
    zhengdaodaying: "zhengdaodaying",
    lujiacun: "zhengdaodaying",
    dengdingshandao: "dengdingshandao"
};
function uniqueLines(lines) {
    const result = new Map();
    for (const line of lines) {
        const a = line.slice(1, 4).map(Math.round).join(":"), b = line.slice(4, 7).map(Math.round).join(":");
        const key = String(line[8]).toLowerCase() + ":" + (a < b ? a + ":" + b : b + ":" + a);
        const previous = result.get(key);
        if (!previous || (line[7] || 0) > (previous[7] || 0)) result.set(key, line);
    }
    return [...result.values()];
}
function loadRegionStaticLines(region, mapIDs) {
    const index = require("./atlas/index");
    const submaps = [...new Set([...(mapIDs || []), region].filter(Boolean))];
    const lines = [];
    const loadedAtlases = new Set();
    for (const submap of submaps) {
        const key = String(submap).toLowerCase();
        const atlasName = atlasMapBySubmap[key];
        if (!atlasName || !index[atlasName]?.lines) continue;
        if (loadedAtlases.has(atlasName)) continue;
        loadedAtlases.add(atlasName);
        const sourceMap = submaps.find(id => String(id).toLowerCase() === atlasName) || submap;
        const raw = require("./atlas/" + atlasName).lines || [];
        for (const seg of raw) {
            lines.push([seg[0], seg[1], seg[2], seg[3], seg[4], seg[5], seg[6], seg[7] || 0, sourceMap]);
        }
    }
    return lines;
}
function open(page, UE, puerts, region, mapIDs, player, store, exploredOnly, isExplored, mapID, canSample, initialFocus) {
    const staticLines = loadRegionStaticLines(region, mapIDs);
    let sampler, focus = initialFocus, version = 0, disposed = false, candidates = [], cells = [], routeID = "";
    try { sampler = NavSurvey.open(page, UE, puerts, region, mapID, player, store?.profile, isExplored, canSample); }
    catch (error) { Diagnostics.record("survey-error", { region, error: String(error) }); }
    function rebuild() {
        cells = (sampler?.values() || []).filter(isExplored);
        candidates = uniqueLines(staticLines.concat(NavSurvey.outline(cells))).filter(line => {
            const z = (line[3] + line[6]) / 2;
            const point = { x: (line[1] + line[4]) / 2, y: (line[2] + line[5]) / 2, z, mapID: line[8] || region };
            return isExplored(point) && isExplored({ x: line[1], y: line[2], z: line[3], mapID: point.mapID }) &&
                isExplored({ x: line[4], y: line[5], z: line[6], mapID: point.mapID });
        });
        version++;
    }
    rebuild();
    function onFloor(z) { return !focus?.strictSlice || z >= focus.minZ && z <= focus.maxZ; }
    return {
        exploredOnly,
        get available() { return candidates.length > 0 || cells.length > 0; },
        resources() { return sampler?.resources() || { queuedQueries: 0, calls: 0, attempts: 0 }; },
        get status() { return sampler?.state || (candidates.length ? "surveyReady" : "surveyUnavailable"); },
        setFocus(next) { focus = next; },
        follow(route) {
            if (route?.targetID && routeID !== route.targetID && route.source.toLowerCase() === mapID.toLowerCase()) {
                routeID = route.targetID; sampler?.follow(route.points);
            }
        },
        snapshot(source) {
            const id = source.toLowerCase();
            return { version, lines: candidates.filter(s => String(s[8]).toLowerCase() === id && onFloor((s[3] + s[6]) / 2)),
                cells: cells.filter(p => p.mapID.toLowerCase() === id && onFloor(p.z)) };
        },
        step(active = true, allowSampling = true) {
            if (disposed || !active) return false;
            if (sampler?.step(allowSampling)) { rebuild(); return true; }
            return false;
        },
        dispose() { if (disposed) return; disposed = true; sampler?.dispose(); sampler = undefined; candidates = []; cells = []; page = undefined; }
    };
}
module.exports = { open };
