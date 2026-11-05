"use strict";
// Page events only. Each run owns separate 32-event chunks, so restarting the
// game cannot erase the last page/cleanup sequence from a crashed run.
const build = require("./BuildInfo");
const started = new Date().toISOString();
const runId = started.replace(/[^0-9TZ]/g, "") + "-" + Math.random().toString(36).slice(2, 10);
let events = [], sequence = 0, chunk = 0, chunkChars = 0, directory, indexWritten = false;
function record(event, detail = {}) {
    const seq = ++sequence;
    try {
        const item = { ...detail, time: new Date().toISOString(), event, seq, runId, revision: build.revision };
        // Serialize first: the journal never retains native object wrappers.
        const raw = JSON.stringify(item);
        const value = raw.length <= 8192 ? JSON.parse(raw) : { time: item.time, event, seq, runId, revision: build.revision, detailTruncated: true, summary: raw.slice(0, 4000) };
        const valueChars = JSON.stringify(value).length;
        if (events.length && (events.length === 32 || chunkChars + valueChars > 12000)) { events = []; chunkChars = 0; chunk++; }
        events.push(value);
        chunkChars += valueChars;
        console.log("[PhantomBladeMap] " + JSON.stringify(value));
        const UE = require("ue");
        if (!directory) directory = String(UE.KismetSystemLibrary.GetPlatformUserDir()).replace(/\\/g,"/").replace(/\/+$/, "") + "/PbzMods/Diagnostics/PhantomBladeMap";
        if (!indexWritten) {
            const index = { schema: 2, runId, started, ...build, sessionDirectory: directory + "/sessions/" + runId };
            indexWritten = UE.EasyCsv.SaveStringToFile("Name,Payload\nsession," + encodeURIComponent(JSON.stringify(index)) + "\n", directory) === true;
        }
        if (UE.EasyCsv.SaveStringToFile("Name,Payload\nstatus," + encodeURIComponent(JSON.stringify(events)) + "\n",
            directory + "/sessions/" + runId + "/" + String(chunk).padStart(6, "0")) !== true) throw new Error("DIAGNOSTIC_WRITE_FAILED");
    } catch (error) { console.warn("[PhantomBladeMap] diagnostic-write: " + String(error)); }
    return seq;
}
module.exports = { record };
