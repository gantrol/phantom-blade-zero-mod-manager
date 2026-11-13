"use strict";

// Controller values are applied only to the player's ordinary combat changer.
// Switching keeps the game's input path, with activation/release hysteresis.
module.exports = Object.freeze({
    stickActive: 0.85,
    stickRelease: 0.25,
    repeatDelay: 0.9,
    repeatCooldown: 0.6,
    occlusionGraceSeconds: 2.2,
    maxCandidates: 128,
    tickMilliseconds: 50
});
