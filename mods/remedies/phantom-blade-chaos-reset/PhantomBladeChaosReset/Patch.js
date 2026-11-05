"use strict";
const target = require("./Target");

function requireCondition(condition, code) {
    if (!condition) throw new Error(code);
}

// Operates on a detached slot. The caller writes the result to a NEW manual slot.
// Inventory, achievements, statistics, earlier quest branches and other slots
// are outside this patch's write set.
function prepare(source) {
    const save = JSON.parse(JSON.stringify(source));
    const narrative = save.Narrative;
    const pipeline = save.EventPipeline;
    const spawners = save.AISystem?.SpawnerSaveData;
    requireCondition(Array.isArray(narrative?.NarrativeSavedQuests)
        && Array.isArray(pipeline?.GlobalTags) && pipeline.ComponentInfoJson
        && pipeline.ComponentsOwnerActorInfo && pipeline.ExecuteNodeGuid && spawners, "schema");
    const matches = narrative.NarrativeSavedQuests.filter(q => q.QuestClass === target.quest);
    requireCondition(matches.length === 1, "questMissing");
    const quest = matches[0];
    requireCondition(Array.isArray(quest.QuestBranches) && Array.isArray(quest.CompletedBranches), "schema");
    const branch = id => quest.QuestBranches.find(b => b.BranchID === id);
    requireCondition(target.prerequisites.every(id => branch(id)?.BranchType === "EBranchType::BranchCompleted"), "prerequisites");
    requireCondition(target.battleBranches.every(id => branch(id)), "schema");
    // This release repairs the failed forest encounter observed in build 26266538.
    // The successful third-phase ending has separate spawners and reward state.
    requireCondition(quest.QuestCompletion === "EQuestCompletion::QC_Failed"
        && pipeline.GlobalTags.includes("fjzl.三头魔偶.死亡")
        && !pipeline.GlobalTags.includes("fjzl.三头魔偶.p3"), "notFailedEncounter");
    requireCondition(Object.prototype.hasOwnProperty.call(spawners, target.spawner), "spawnerMissing");

    const changes = [];
    function record(path, before, after) {
        if (JSON.stringify(before) !== JSON.stringify(after)) changes.push({ path, before, after });
    }
    const questPath = "Narrative/NarrativeSavedQuests/" + target.quest;
    record(questPath + "/QuestCompletion", quest.QuestCompletion, "EQuestCompletion::QC_Started");
    quest.QuestCompletion = "EQuestCompletion::QC_Started";
    for (const id of target.battleBranches) {
        const value = branch(id);
        record(questPath + "/" + id + "/BranchType", value.BranchType, "EBranchType::BranchActivating");
        value.BranchType = "EBranchType::BranchActivating";
    }
    const completed = quest.CompletedBranches.filter(id => !target.battleBranches.includes(id));
    record(questPath + "/CompletedBranches", quest.CompletedBranches, completed);
    quest.CompletedBranches = completed;

    const removedTags = new Set(target.tags);
    function clearTags(object, key, path) {
        if (!Array.isArray(object[key])) return;
        const next = object[key].filter(tag => !removedTags.has(tag));
        record(path, object[key], next);
        object[key] = next;
    }
    clearTags(pipeline, "GlobalTags", "EventPipeline/GlobalTags");
    for (const [id, info] of Object.entries(pipeline.ComponentInfoJson)) {
        clearTags(info, "EventTag", "EventPipeline/ComponentInfoJson/" + id + "/EventTag");
    }
    function remove(object, key, path) {
        if (!Object.prototype.hasOwnProperty.call(object, key)) return;
        record(path, object[key], undefined);
        delete object[key];
    }
    for (const id of target.executionNodes) {
        remove(pipeline.ExecuteNodeGuid, id, "EventPipeline/ExecuteNodeGuid/" + id);
    }
    // Absence restores the map's authored spawn and neutral pre-combat defaults.
    remove(spawners, target.spawner, "AISystem/SpawnerSaveData/" + target.spawner);
    remove(pipeline.ComponentsOwnerActorInfo, target.flowOwner,
        "EventPipeline/ComponentsOwnerActorInfo/" + target.flowOwner);
    return { save, changes };
}

module.exports = { prepare };
