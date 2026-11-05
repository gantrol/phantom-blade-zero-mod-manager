"use strict";
const messages = {
    en: {
        hint: "Load game · F9: reset Three-Faced Chaos",
        saving: "Creating reset save…",
        ready: "Three-Faced Chaos reset · Manual save {slot}",
        mainMenu: "Open Load Game from the title screen.",
        schema: "Save format does not match this mod.",
        nightmare: "Select a standard save.",
        questMissing: "Three-Faced Chaos is absent from this save.",
        prerequisites: "Complete the quest's forest prerequisites first.",
        notFailedEncounter: "Select a save with the failed forest encounter.",
        spawnerMissing: "The forest encounter record is missing.",
        noSelection: "Select a save first.",
        full: "All 20 manual save slots are occupied.",
        loadFailed: "Unable to read the selected save.",
        failed: "Unable to create the reset save."
    },
    "zh-CN": {
        hint: "读取存档 · F9：重置三面混沌",
        saving: "正在创建重置存档…",
        ready: "三面混沌已重置 · 手动存档 {slot}",
        mainMenu: "请从标题画面进入读取存档。",
        schema: "存档格式与此 Mod 不匹配。",
        nightmare: "请选择普通模式存档。",
        questMissing: "此存档尚未接取三面混沌。",
        prerequisites: "请先完成血色森林战斗的前置任务。",
        notFailedEncounter: "请选择血色森林三面混沌已失败的存档。",
        spawnerMissing: "未找到血色森林战斗记录。",
        noSelection: "请先选择存档。",
        full: "20 个手动存档位已满。",
        loadFailed: "无法读取所选存档。",
        failed: "重置存档创建失败。"
    }
};
const aliases = { zh: "zh-CN", "zh-Hans": "zh-CN", "zh-Hans-CN": "zh-CN", "zh-CN": "zh-CN" };
function translate(language, key, parameters = {}) {
    const resource = messages[aliases[language] || language] || messages.en;
    return (resource[key] || resource.failed).replace(/\{(\w+)\}/g, (_, name) => String(parameters[name] ?? ""));
}
module.exports = { translate };
