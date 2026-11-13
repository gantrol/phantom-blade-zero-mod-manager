"use strict";
const { createJournal } = require("./provider/Journal");
const { createEasyCsvIO } = require("./provider/EasyCsvIO");
const { resolveUserRoot } = require("./provider/StorageRoot");
const Diagnostics = require("./Diagnostics");
function profile(UE,page) {
    const get=type=>UE.SubsystemBlueprintLibrary.GetWorldSubsystem(page,type.StaticClass());
    const game=get(UE.PBZGameSystem);
    if(!game)return "";
    const cycle=game.GetCycleCount(),lineage=String(game.GetNightmareLineageId() || "");
    if(!Number.isInteger(cycle) || cycle<0)return "";
    if(lineage && lineage!=="None")return "lineage-"+lineage+":"+cycle;
    // Normal runs have no lineage ID. This initial document's immutable grant
    // time comes from the loaded inventory and survives travel/manual saves.
    // Never adopt old slot-name journals: an autosave slot can hold any run.
    const initial=get(UE.PBZBackPackSubsystem)?.Inventories.GetRef("Page_XC_01");
    if(!initial)return "";
    const started=Number(UE.KismetMathLibrary.ToUnixTimestamp(initial.FirstAddTime));
    return Number.isSafeInteger(started) && started>0 ? "run-"+started+":"+cycle : "";
}
function open(UE,puerts,page) {
    const profileID=profile(UE,page);
    if(!profileID)return;
    const identityPrefix=profileID.split(":")[0]+":";
    // Allow loading legacy files that accumulated footstep visits, then compact on save.
    const maxChars=4*1024*1024;
    const io=createEasyCsvIO(UE,puerts,resolveUserRoot(UE),"PhantomBladeMapMarks",path=>UE.BlueprintPathsLibrary.FileExists(path),maxChars);
    const journal=createJournal(io,"PhantomBladeMapMarks",undefined,maxChars);
    const loaded = journal.load();
    let values = loaded.record?.values || { marks: [], visits: [] };
    if (!Array.isArray(values.marks) || !Array.isArray(values.visits)) throw new Error("MAP_DATA_SCHEMA");
    values = { ...values, discoveries: Array.isArray(values.discoveries) ? values.discoveries : (values.chests || []),
        trackedQuests: Array.isArray(values.trackedQuests) ? values.trackedQuests : [] };
    let revision = loaded.record?.revision || 0;
    let storageError = loaded.blocked ? "MAP_DATA_RECOVERY_REQUIRED" : "";
    if (storageError) Diagnostics.record("storage-error", { source: "marks-load", error: storageError });
    function saveValues(next) {
        // Keep the map usable with temporary edits if persistence is unavailable.
        // A real external change still blocks writes; it is never overwritten.
        // Drop legacy footstep visits so cross-cycle storage stays compact.
        values = { ...next, visits: [] };
        if (storageError) return;
        try {
            journal.save({ schemaVersion: 1, revision: revision + 1, values });
            revision++;
        } catch (error) {
            storageError = String(error);
            Diagnostics.record("storage-error", { source: "marks-save", error: storageError });
        }
    }
    return {
        profile:profileID,
        visits() {return [];},
        rememberVisits() {},
        get storageError() { return storageError; },
        discoveries() { return values.discoveries.filter(p => p.profile === profileID); },
        rememberDiscoveries(records) {
            // Prune discoveries from superseded cycles of the same save identity.
            const kept=values.discoveries.filter(p=>!String(p.profile || "").startsWith(identityPrefix) || p.profile===profileID);
            const recordsByID=new Map(kept.map(p=>[p.profile+":"+p.save,p]));
            let changed=kept.length!==values.discoveries.length || values.visits.length>0;
            for (const record of records) {
                const key=profileID+":"+record.save;
                if (record.evict) {
                    if (recordsByID.delete(key)) changed=true;
                    continue;
                }
                const old=recordsByID.get(key);
                if (old && ["mapID","x","y","z","category","sourceClass","observedRemaining","reward","loot"].every(k=>old[k]===record[k]) &&
                    JSON.stringify(old.rewards)===JSON.stringify(record.rewards)) continue;
                recordsByID.set(key,{...record,profile:profileID});changed=true;
            }
            if (changed) saveValues({...values,discoveries:Array.from(recordsByID.values()).slice(-2048)});
        },
        trackedQuest() { return values.trackedQuests.find(q=>q.profile===profileID); },
        trackQuest(point) {
            const current=values.trackedQuests.find(q=>q.profile===profileID);
            if ((!point && !current) || (point && current?.id===point.id)) return;
            const trackedQuests=values.trackedQuests.filter(q=>q.profile!==profileID);
            if(point)trackedQuests.push({profile:profileID,id:point.id,questKey:point.questKey});
            saveValues({...values,trackedQuests});
        },
        marks(region) { return values.marks.filter(m => m.profile === profileID && m.region === region); },
        visit() {},
        toggle(region, layer, u, v, position) {
            const index = values.marks.findIndex(m => {
                if(m.profile!==profileID || m.region!==region)return false;
                // World coordinates survive atlas floor renumbering. Legacy UV
                // pins belong to the original map and carry no inferred floor.
                if(Number.isFinite(m.x) && Number.isFinite(position?.x))
                    return Math.hypot(m.x-position.x,m.y-position.y)<150 && Math.abs(m.z-position.z)<200;
                return !Number.isFinite(m.x) && !Number.isFinite(position?.x) && Math.hypot(m.u-u,m.v-v)<.015;
            });
            const marks = values.marks.slice();
            if (index >= 0) marks.splice(index, 1);
            else {
                if (values.marks.length >= 512) throw new Error("MARK_LIMIT");
                marks.push({ profile:profileID, region, layer, u, v, ...(position || {}) });
            }
            saveValues({ ...values, marks });
        },
        dispose() { values = { marks: [], visits: [], discoveries: [], trackedQuests: [] }; }
    };
}
module.exports = { open, profile };
