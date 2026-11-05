"use strict";
const { ID } = require("./Protocol");
const { plain } = require("./Schema");
// Two independent slots. A failed/torn write never overwrites the current slot.
// ASCII CSV payload is deliberately independent of UE's UTF-8/UTF-16 output choice.
function checksum(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619);
    return (hash >>> 0).toString(16).padStart(8, "0");
}
function encode(record) {
    const payload = encodeURIComponent(JSON.stringify(record));
    return "Name,Payload,Checksum\nconfig," + payload + "," + checksum(payload) + "\n";
}
function decode(raw, maxChars = 1024 * 1024) {
    if (typeof raw !== "string" || raw.length > maxChars) throw new Error("INVALID_SIZE");
    const rows = raw.replace(/^\uFEFF/, "").trim().split(/\r?\n/);
    if (rows.length !== 2 || rows[0] !== "Name,Payload,Checksum") throw new Error("INVALID_HEADER");
    const cells = rows[1].split(",");
    if (cells.length !== 3 || cells[0] !== "config" || checksum(cells[1]) !== cells[2]) throw new Error("CHECKSUM_MISMATCH");
    const result = JSON.parse(decodeURIComponent(cells[1]));
    if (!plain(result) || result.format !== 1 || !ID.test(result.modId) || !Number.isSafeInteger(result.generation) || result.generation < 1 ||
        !Number.isSafeInteger(result.revision) || result.revision < 0 || !Number.isSafeInteger(result.schemaVersion) || result.schemaVersion < 1 || !plain(result.values)) throw new Error("INVALID_RECORD");
    return result;
}
function createJournal(io, modId, report = () => {}, maxChars = 1024 * 1024) {
    if (!ID.test(modId)) throw new Error("INVALID_MOD_ID");
    let current = null, initialized = false, blocked = false;
    function readSlots() {
        const valid = [], errors = [];
        for (let slot = 0; slot < 2; slot++) {
            try {
                const text = io.read(slot); // null means known absent; I/O errors must throw.
                if (text === null) continue;
                const record = decode(text, maxChars);
                if (record.modId !== modId) throw new Error("NAMESPACE_MISMATCH");
                valid.push({ slot, record });
            } catch (e) { errors.push({ slot, code: "READ_FAILED", detail: String(e) }); }
        }
        valid.sort((a, b) => b.record.generation - a.record.generation);
        if (valid.length === 2 && valid[0].record.generation === valid[1].record.generation &&
            JSON.stringify(valid[0].record) !== JSON.stringify(valid[1].record)) throw new Error("GENERATION_CONFLICT");
        return { valid, errors };
    }
    return {
        load() {
            if (initialized) throw new Error("ALREADY_LOADED");
            initialized = true;
            try {
                const { valid, errors } = readSlots();
                errors.forEach(report);
                current = valid[0] || null;
                // Preserve damaged originals, even with a valid recovery slot. Explicit repair required.
                blocked = errors.length > 0;
                // Callers own their working values. Keep the disk comparison
                // snapshot private when they append visits or edit markers.
                return { record: current ? JSON.parse(JSON.stringify(current.record)) : null, recovered: !!current && blocked, blocked, errors };
            } catch (e) { blocked = true; return { record: null, blocked, errors: [{ code: "LOAD_FAILED", detail: String(e) }] }; }
        },
        save(data) {
            if (!initialized) throw new Error("LOAD_REQUIRED");
            if (blocked) throw new Error("RECOVERY_REQUIRED");
            const observed = readSlots();
            if (observed.errors.length) { blocked = true; throw new Error("STORAGE_CHANGED_OR_DAMAGED"); }
            const latest = observed.valid[0] || null;
            if (JSON.stringify(latest) !== JSON.stringify(current)) { blocked = true; throw new Error("EXTERNAL_DISK_CHANGE"); }
            const generation = (current?.record.generation || 0) + 1;
            if (!Number.isSafeInteger(generation)) throw new Error("GENERATION_OVERFLOW");
            const slot = current ? 1 - current.slot : 0;
            const record = { format: 1, modId, generation, schemaVersion: data.schemaVersion, revision: data.revision, values: data.values };
            const serialized = encode(record);
            if(serialized.length>maxChars)throw new Error("INVALID_SIZE");
            if (io.write(slot, serialized) !== true) throw new Error("WRITE_FAILED");
            const actual = io.read(slot);
            if (actual === null) throw new Error("READBACK_FAILED");
            const verified = decode(actual, maxChars);
            if (JSON.stringify(verified) !== JSON.stringify(record)) throw new Error("READBACK_FAILED");
            current = { slot, record: verified };
            return { generation };
        }
    };
}
module.exports = { createJournal, encode, decode, checksum };
