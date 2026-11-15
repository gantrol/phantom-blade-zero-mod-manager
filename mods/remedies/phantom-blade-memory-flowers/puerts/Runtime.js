"use strict";
const deathTags = require("./DeathTags.generated");

function install() {
    if (!require("../PhantomBladeRuntime/Enabled").includes("map")) return;
    const { flows } = require("../PhantomBladeMap/CollectionRequirements");
    // Change only the rescue prerequisite. The live native flow still decides
    // availability, and the quest-completion and collection conditions remain.
    const changes = Object.entries(deathTags).map(([stage, deathTag]) => {
        const checks = flows["BPF_BuMieXiaDan_Memory_" + stage];
        const check = checks?.find(item => item.tag === "pz.不灭侠胆.帮助正道解脱" + stage || item.tag === deathTag);
        if (!check) throw new Error("MEMORY_FLOWERS_MAP_INTERFACE_MISMATCH: " + stage);
        return { check, deathTag };
    });
    for (const { check, deathTag } of changes) {
        check.tag = deathTag;
        check.reason = "collectionCombatRequired";
    }
}

module.exports = { install };
