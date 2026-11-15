"use strict";
const data = require("./StoryTransitionData");

// Resolve authored coordinates through native map membership once per map page.
// Only plain numbers/strings survive this step; opening a marker just browses.
class StoryLinks {
    constructor(mapIDs) {
        this.allowed = new Set(mapIDs.map(id => String(id).toLowerCase()));
        this.cursor = 0;
        this.points = [];
        this.ready = false;
    }
    step(map, UE, text, explored) {
        const resolve = endpoint => {
            const [x, y, z] = endpoint.position;
            const mapID = String(map.GetMapIDByLevelAndLocation(endpoint.level, new UE.Vector(x, y, z)) || "");
            return mapID && mapID !== "None" ? { mapID, x, y, z, layer: -1 } : undefined;
        };
        for (let budget = 0; budget < 4 && this.cursor < data.links.length; budget++, this.cursor++) {
            const record = data.links[this.cursor], from = resolve(record.from), to = resolve(record.to);
            if (!from || !to) continue;
            const pair = String(this.cursor + 1).padStart(2, "0");
            for (const [role, point, destination] of [["entry", from, to], ["exit", to, from]]) {
                if (!this.allowed.has(point.mapID.toLowerCase())) continue;
                const marker = { ...point, id: "storyLink:" + record.id + ":" + role,
                    pointKind: "storyLink", facility: true, facilityKind: "storyTeleport", category: "storyTeleport",
                    icon: role === "entry" ? "storyEntry" : "storyExit", state: "remaining", conditionKnown: false,
                    pair, endpointRole: role, destination: { ...destination },
                    label: text[record.labelKey] + " " + pair + " · " + text[role === "entry" ? "storyEntry" : "storyExit"] };
                marker.explored = explored(marker);
                this.points.push(marker);
            }
        }
        this.ready = this.cursor === data.links.length;
    }
    dispose() { this.points = []; this.ready = true; }
}
module.exports = { StoryLinks };
