"use strict";
const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
// Each request is cancellable and expanded from the page's Tick in small slices.
// Only certified mesh adjacency is included; geometric erosion gaps are excluded.
function start(atlas, player, target, visible) {
    const result = { state: "routeLoading", points: [], transitions: [], distance: 0, targetID: target.id, source: target.mapID };
    let cursor = 0, source = -1, goal = -1, sourceDistance = Infinity, goalDistance = Infinity;
    let phase = "anchors", closed = false;
    const sourceFloor = atlas.floor(player), targetFloor = atlas.floor(target);
    const path = [];
    let trace = -1, segment, sample = 0, samples = 0;
    const heap = [], costs = new Map(), previous = new Map(), visited = new Set();
    function push(id, score) {
        let i = heap.length; heap.push([id, score]);
        while (i > 0) { const p = (i - 1) >> 1; if (heap[p][1] <= score) break; heap[i] = heap[p]; i = p; }
        heap[i] = [id, score];
    }
    function pop() {
        const head = heap[0], tail = heap.pop();
        if (heap.length) { let i = 0;
            while (i * 2 + 1 < heap.length) { let child = i * 2 + 1;
                if (child + 1 < heap.length && heap[child + 1][1] < heap[child][1]) child++;
                if (heap[child][1] >= tail[1]) break; heap[i] = heap[child]; i = child; }
            heap[i] = tail; }
        return head[0];
    }
    function finish(state) { result.state = state; phase = "done"; heap.length = path.length = 0; segment = undefined; costs.clear(); previous.clear(); visited.clear(); }
    return {
        result,
        step() {
            if (closed || phase === "done") return false;
            const begin = Date.now();
            for (let batch = 0; batch < 128 && Date.now() - begin < 2; batch++) {
                if (phase === "anchors") {
                    if (cursor < atlas.nodes.length / 4) {
                        const id = cursor++, p = atlas.point(id);
                        if (p.floor === sourceFloor) { const d = distance(player, p); if (d < sourceDistance) { source = id; sourceDistance = d; } }
                        if (p.floor === targetFloor) { const d = distance(target, p); if (d < goalDistance) { goal = id; goalDistance = d; } }
                        continue;
                    }
                    if (source < 0 || goal < 0 || sourceDistance > 3000 || goalDistance > 3000) { finish("routeUnavailable"); return true; }
                    phase = "search"; costs.set(source, 0); push(source, 0);
                }
                if (phase === "trace") {
                    path.push(trace);
                    if (trace === source) { phase = "emit"; cursor = path.length - 1; }
                    else trace = previous.get(trace);
                    continue;
                }
                // Reconstruction and fog checks share the search's frame
                // budget. A long successful path must not stall its last frame.
                if (phase === "emit") {
                    if (segment && sample < samples) {
                        const {a,p} = segment, t = sample++ / samples;
                        if (!visible({x:a.x+(p.x-a.x)*t,y:a.y+(p.y-a.y)*t,z:a.z+(p.z-a.z)*t,mapID:p.mapID})) {
                            finish("routeUnexplored"); return true;
                        }
                        continue;
                    }
                    if (segment) {
                        const {a,p,length} = segment;
                        result.distance += length;
                        if (a.floor !== p.floor) {
                            const connection=atlas.connection(a,p);
                            if(connection && visible(connection))result.transitions.push(connection);
                        }
                        result.points.push(p); segment = undefined;
                    }
                    if (cursor < 0) {
                        result.endpoint = result.points[result.points.length - 1]; result.approach = goalDistance;
                        finish("routeCoarse"); return true;
                    }
                    const p = atlas.point(path[cursor--]), a = result.points[result.points.length - 1];
                    if (!visible(p)) { finish("routeUnexplored"); return true; }
                    if (a) {
                        const length = distance(a,p); segment = {a,p,length};
                        sample = 1; samples = Math.ceil(length / 200);
                    } else result.points.push(p);
                    continue;
                }
                if (!heap.length) { finish("routeUnavailable"); return true; }
                const id = pop(); if (visited.has(id)) continue; visited.add(id);
                if (id === goal) {
                    phase = "trace"; trace = goal; continue;
                }
                for (const [next, weight] of atlas.adjacency.get(id) || []) {
                    const cost = costs.get(id) + weight;
                    if (cost >= (costs.get(next) ?? Infinity)) continue;
                    costs.set(next, cost); previous.set(next, id); push(next, cost + distance(atlas.point(next), atlas.point(goal)));
                }
            }
            return false;
        },
        dispose() { closed = true; finish("routeUnavailable"); result.points = []; result.transitions = []; }
    };
}
module.exports = { start };
