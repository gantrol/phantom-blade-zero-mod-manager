"use strict";
const Diagnostics = require("./Diagnostics");
const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
const vector = (UE, p) => new UE.Vector(p.x, p.y, p.z);

// A request belongs to one open map page. Only numeric copies leave this call;
// no path notifier, observed actor, automatic repath or timer survives it.
function find(page, UE, puerts, player, target, currentMapID, isExplored) {
    const result = { targetID: target.id, source: target.mapID, points: [], transitions: [], distance: 0, state: "routeUnavailable" };
    if (!player || target.mapID.toLowerCase() !== currentMapID.toLowerCase()) { result.state = "routeOtherArea"; return result; }
    let path;
    try {
        const endRef = puerts.$ref();
        const projected = UE.NavigationSystemV1.K2_ProjectPointToNavigation(page, vector(UE, target), endRef, undefined, undefined, new UE.Vector(200, 200, 300));
        if (!projected) return result;
        const endpoint = puerts.$unref(endRef), end = { x: endpoint.X, y: endpoint.Y, z: endpoint.Z };
        const pawn = UE.GameplayStatics.GetPlayerCharacter(page, 0);
        path = UE.NavigationSystemV1.FindPathToLocationSynchronously(page, vector(UE, player), vector(UE, end), pawn, undefined);
        if (!path) return result;
        path.EnableRecalculationOnInvalidation(UE.ENavigationOptionFlag.Disable);
        if (!path.IsValid()) return result;
        const partial = path.IsPartial(), raw = path.PathPoints, copied = [];
        for (let i = 0, n = raw.Num(); i < n; i++) {
            const p = raw.Get(i); copied.push({ x: p.X, y: p.Y, z: p.Z, mapID: currentMapID });
        }
        if (copied.length === 1 && distance(player, target) < 300 && isExplored(copied[0])) {
            result.state = "routeReady"; result.points = copied; result.endpoint = copied[0]; result.approach = distance(copied[0], target); return result;
        }
        if (copied.length < 2) return result;
        let hidden = false;
        // Check the interiors as well as corners. A path crossing unexplored
        // space stops at the known frontier instead of revealing its far end.
        for (let i = 1; i < copied.length && !hidden; i++) {
            const a = copied[i - 1], b = copied[i], length = distance(a, b), steps = Math.max(1, Math.ceil(length / 200));
            if (!result.points.length && isExplored(a)) result.points.push(a);
            for (let j = 1; j <= steps; j++) {
                const t = j / steps, p = { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t, mapID: currentMapID };
                if (!isExplored(p)) { hidden = true; break; }
                result.points.push(p);
            }
        }
        if (result.points.length < 2) { result.state = hidden ? "routeUnexplored" : "routeUnavailable"; return result; }
        const last = result.points[result.points.length - 1];
        result.state = hidden ? "routeUnexplored" : partial || distance(last, end) > 200 ? "routePartial" : "routeReady";
        for (let i = 1; i < result.points.length; i++) result.distance += distance(result.points[i - 1], result.points[i]);
        // Vertical changes are geometry facts, not guesses about stairs/ladders.
        let anchor = result.points[0];
        for (const p of result.points.slice(1)) if (Math.abs(p.z - anchor.z) >= 200) {
            result.transitions.push({ ...p, dz: p.z - anchor.z }); anchor = p;
        }
        result.endpoint = last;
        result.approach = distance(last, target);
        Diagnostics.record("route", { target: target.id, state: result.state, points: result.points.length, length: Math.round(result.distance), approach: Math.round(result.approach) });
    } catch (error) { Diagnostics.record("route-error", { target: target.id, error: String(error) }); }
    finally { path = undefined; }
    return result;
}
module.exports = { find };
