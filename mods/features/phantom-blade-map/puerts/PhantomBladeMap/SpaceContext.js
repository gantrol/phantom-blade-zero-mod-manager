"use strict";

// Stabilize doorway/floor boundaries without timers or persisted view changes.
class SpaceContext {
    reset() { this.key = this.world = undefined; this.since = 0; this.current = undefined; }
    update(atlas, player, now = Date.now()) {
        if (this.world !== atlas?.world) { this.reset(); this.world = atlas?.world; }
        const location = player && atlas?.locate(player);
        if (!location && atlas?.pending) return this.current;
        const key = location ? location.layer + ":" + location.space : "unknown";
        if (key !== this.key) { this.key = key; this.since = now; }
        if (now - this.since >= 600) {
            this.current = location ? { ...location,
                detailed: location.space === "caveOrRoom" || atlas.layers[location.layer]?.kind === "under" } : undefined;
        }
        return this.current;
    }
    minimapScale(enabled) { return enabled && this.current?.detailed ? 30 : 50; }
}
module.exports = { SpaceContext };
