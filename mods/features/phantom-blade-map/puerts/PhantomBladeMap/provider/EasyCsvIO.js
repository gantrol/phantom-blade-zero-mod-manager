"use strict";
const { ID } = require("./Protocol");
const { decode } = require("./Journal");
// Experimental UE adapter. Enable only after the isolated storage probe passes.
// UE and puerts are injected so tests never impersonate the actual game runtime.
function createEasyCsvIO(UE, puerts, verifiedRoot, modId, fileExists, maxChars = 1024 * 1024) {
    if (!ID.test(modId) || typeof verifiedRoot !== "string" ||
        !/^[A-Za-z]:[\\/]/.test(verifiedRoot) || /(^|[\\/])\.\.([\\/]|$)/.test(verifiedRoot) ||
        /[\\/]SaveGames([\\/]|$)/i.test(verifiedRoot) || typeof fileExists !== "function") throw new Error("UNVERIFIED_STORAGE_ROOT");
    const root = verifiedRoot.replace(/\\/g, "/").replace(/\/+$/, "") + "/" + modId;
    function dir(slot) { if (slot !== 0 && slot !== 1) throw new Error("INVALID_SLOT"); return root + "/" + slot; }
    function read(slot) {
        const path = dir(slot) + "/New_CSV.csv";
        const exists = fileExists(path);
        if (exists === false) return null;
        if (exists !== true) throw new Error("EXISTENCE_UNKNOWN");
        const out = puerts.$ref();
        // Proven signature: string path + $ref(), NOT a plain object posing as FFilePath.
        if (UE.EasyCsv.MakeCsvInfoStructFromFile(path, out) !== true) throw new Error("CSV_READ_FAILED");
        const info = puerts.$unref(out);
        if (!info || info.CSV_Keys.Num() !== 1) throw new Error("CSV_ROW_MISMATCH");
        // CSV keys are FName values: the engine may reuse an earlier display
        // spelling (observed "Config" for the literal CSV row "config").
        const rowKey = info.CSV_Keys.Get(0);
        if (String(rowKey).toLowerCase() !== "config") throw new Error("CSV_ROW_MISMATCH");
        const row = info.CSV_Map.Get(rowKey);
        if (!row || row.StringValues.Num() !== 2) throw new Error("CSV_CELL_MISMATCH");
        // Copy primitive strings immediately; do not hold a TMap child across calls.
        const payload = String(row.StringValues.Get(0)), checksum = String(row.StringValues.Get(1));
        const raw = "Name,Payload,Checksum\nconfig," + payload + "," + checksum + "\n";
        decode(raw, maxChars);
        return raw;
    }
    return {
        read,
        write(slot, raw) {
            decode(raw, maxChars);
            return UE.EasyCsv.SaveStringToFile(raw, dir(slot)) === true;
        }
    };
}
module.exports = { createEasyCsvIO };
