"use strict";

module.exports = {
    quest: "/Game/PBZstory/PBZQuest/Branch/SanMianHunDun.SanMianHunDun_C",
    world: "PBZ_Fujizhilin_WP",
    spawner: "PBZ_Fujizhilin_WPBP_AISpawner_C_UAID_E89C25A75434CBCC02_1307783693",
    flowOwner: "PBZ_Fujizhilin_WP-BP_AISpawner_C_UAID_E89C25A75434CBCC02_1307783693",
    prerequisites: ["S1", "S2", "S3", "S4", "S5"],
    battleBranches: ["S6", "1"],
    tags: [
        "fjzl.三头魔偶.激活",
        "fjzl.三头魔偶.出场",
        "fjzl.三头魔偶.死亡",
        "fjzl.三头魔偶.p2",
        "fjzl.三头魔偶.p3"
    ],
    // SetExcuteType nodes in the shipped ChuChang, Death and RuZhan graphs.
    executionNodes: [
        "F6BBF7EC4111EB864AD607AF1825EEE3",
        "7F2970B84E7596BA64DAA8ACC410A834",
        "D83873FF40E31CEFA0EC22BD5811D61C",
        "1D46270943E7B1894E9DE0B5B086B5EB",
        "1076758D46FCE9248D9B9A9E9E21766B",
        "60AD442E44FDB0EEACCCB1AE6A77328A"
    ]
};
