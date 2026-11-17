"use strict";

// Ordinary lock-on and bow aiming keep the game's mapped input actions.
module.exports = Object.freeze({
    stickActive: 0.85,
    stickRelease: 0.25,
    acquisitionAngleDegrees: 15,
    occlusionGraceSeconds: 2.2,
    maxCandidates: 128,
    maxTraceComponents: 256,
    tickMilliseconds: 50
});
