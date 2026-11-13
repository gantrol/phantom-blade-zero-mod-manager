"use strict";
const { label } = require("./Locales");
const Schema = require("./provider/Schema");
const { publishOptional } = require("./provider/Protocol");
const { createJournal } = require("./provider/Journal");
const { createEasyCsvIO } = require("./provider/EasyCsvIO");
const { resolveUserRoot } = require("./provider/StorageRoot");
const {groups:filterGroups,traversalGroups}=require("./Filters");
const categories = ["npc", "treeHeart", "marker"];
const legacyGroups={
    filterCollection:["bell","weapon","shadowWeapon","accessory","medicine","challenge","amber","blackstone","flower"],
    filterDocuments:["book"],filterMaterials:["soulMaterial","upgradeMaterial","growthItem"],
    filterMoney:["dust"],filterKeyItems:["key","treasure"],filterSupplies:["supply","chest"]
};
const descriptor = {
    apiVersion: 1, schemaVersion: 7, modId: "PhantomBladeMap", displayName: label("title"),
    groups: [{ id: "display", label: label("settings") }, { id: "categories", label: label("categories") }, {id:"traversal",label:label("filterTraversal")}],
    entries: [
        { key: "visibility", type: "toggle", label: label("visibility"), defaultValue: true, group: "display" },
        { key: "exploredOnly", type: "toggle", label: label("exploredOnly"), defaultValue: false, group: "display" },
        { key: "showFog", type: "toggle", label: label("showFog"), defaultValue: true, group: "display" },
        { key: "remainingOnly", type: "toggle", label: label("remainingOnly"), defaultValue: true, group: "display" },
        { key: "showList", type: "toggle", label: label("showList"), defaultValue: true, group: "display" },
        { key: "view", type: "choice", label: label("view"), defaultValue: "native", group: "display", options: [{value:"native",label:label("native")},{value:"survey",label:label("survey")}] },
        { key: "questGuidance", type: "choice", label: label("questGuidance"), defaultValue: "hints", group: "display",
            options: require("./QuestChains").guidanceModes.map(value=>({value,label:label("questGuidance_"+value)})) },
        { key: "minimap", type: "toggle", label: label("minimap"), defaultValue: true, group: "display" },
        { key: "showFloors", type: "toggle", label: label("showFloors"), defaultValue: true, group: "display" },
        { key: "showTraversal", type: "toggle", label: label("showTraversal"), defaultValue: true, group: "traversal" },
        { key: "autoCollect", type: "toggle", label: label("autoCollect"), defaultValue: false, group: "display" },
        ...filterGroups.map(g=>({key:g.setting,type:"toggle",label:label(g.label),defaultValue:true,group:"categories"})),
        ...traversalGroups.map(g=>({key:g.setting,type:"toggle",label:label(g.label),defaultValue:true,group:"traversal"})),
        ...categories.map(key => ({ key, type: "toggle", label: label(key), defaultValue: !["npc", "treeHeart"].includes(key), group: "categories" }))
    ]
};
let shared;
function open(UE, puerts) {
    if (shared) return shared;
    const schema = Schema.compile(descriptor);
    let values = { ...schema.defaults }, revision = 0, persistence = { state: "saved" }, journal;
    const subscribers = new Set();
    try {
        const root = resolveUserRoot(UE);
        journal = createJournal(createEasyCsvIO(UE, puerts, root, descriptor.modId, path => UE.BlueprintPathsLibrary.FileExists(path)), descriptor.modId);
        const loaded = journal.load();
        if (loaded.record && [1, 2, 3, 4, 5, 6, 7].includes(loaded.record.schemaVersion)) {
            const migrated = loaded.record.schemaVersion < 3
                ? { ...loaded.record.values, remainingOnly: true, exploredOnly: true }
                : { ...loaded.record.values };
            if(loaded.record.schemaVersion<4)migrated.view="native";
            if(loaded.record.schemaVersion<6)migrated.exploredOnly=false;
            delete migrated.showRoute;
            for(const [group,keys] of Object.entries(legacyGroups)) {
                if(migrated[group]===undefined && keys.every(key=>migrated[key]===false))migrated[group]=false;
                for(const key of keys)delete migrated[key];
            }
            if (!Object.keys(Schema.validatePatch(schema, migrated)).length) {
                values = { ...values, ...migrated }; revision = loaded.record.revision;
                if (loaded.record.schemaVersion < descriptor.schemaVersion && !loaded.blocked) {
                    journal.save({ schemaVersion: descriptor.schemaVersion, revision: revision + 1, values }); revision++;
                }
            }
        }
        if (loaded.blocked) journal = undefined;
    } catch (error) { persistence = { state: "failed", error: String(error) }; journal = undefined; }
    const read = () => ({ values: { ...values }, revision, persistence: { ...persistence } });
    const flush = () => ({ ok: persistence.state !== "failed", ...read() });
    function apply(patch, context = {}) {
        if (context.baseRevision !== undefined && context.baseRevision !== revision) return { ok: false, error: "REVISION_CONFLICT", ...read() };
        const errors = Schema.validatePatch(schema, patch);
        if (Object.keys(errors).length) return { ok: false, error: "VALIDATION_FAILED", fields: errors, ...read() };
        values = { ...values, ...patch }; revision++;
        if (journal) {
            try { journal.save({ schemaVersion: descriptor.schemaVersion, revision, values }); persistence = { state: "saved" }; }
            catch (error) { persistence = { state: "failed", error: String(error) }; }
        }
        for (const subscriber of subscribers) subscriber({ ...read(), origin: context.origin || "map" });
        return { ok: true, ...read() };
    }
    const provider = {
        ...descriptor, read, apply, flush,
        reset(group) { return apply(Object.fromEntries(descriptor.entries.filter(e => !group || e.group === group).map(e => [e.key, e.defaultValue]))); },
        subscribe(fn) { subscribers.add(fn); return () => subscribers.delete(fn); }
    };
    publishOptional(provider);
    shared = provider;
    return provider;
}
module.exports = { open, categories };
