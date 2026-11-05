"use strict";
const { ID } = require("./Protocol");
const TYPES = new Set(["toggle", "number", "choice", "header", "action"]);
const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
const plain = v => v !== null && typeof v === "object" && !Array.isArray(v) &&
    (Object.getPrototypeOf(v) === Object.prototype || Object.getPrototypeOf(v) === null);
const copy = v => JSON.parse(JSON.stringify(v));
function deepFreeze(value) {
    if (value && typeof value === "object") { Object.values(value).forEach(deepFreeze); Object.freeze(value); }
    return value;
}
function text(value, optional = false) {
    if (optional && value === undefined) return true;
    return typeof value === "string" || (plain(value) && Object.keys(value).length > 0 && Object.values(value).every(x => typeof x === "string"));
}
function valueError(entry, value) {
    if (entry.type === "toggle") return typeof value === "boolean" ? null : "EXPECTED_BOOLEAN";
    if (entry.type === "choice") return entry.options.some(o => o.value === value) ? null : "UNKNOWN_CHOICE";
    if (entry.type === "number") {
        if (typeof value !== "number" || !Number.isFinite(value)) return "EXPECTED_FINITE_NUMBER";
        if (value < entry.min || value > entry.max) return "OUT_OF_RANGE";
        const units = (value - entry.min) / entry.step;
        if (Math.abs(units - Math.round(units)) > 1e-7) return "OFF_STEP";
        return null;
    }
    return "NOT_A_VALUE";
}
function compile(descriptor, report = () => {}) {
    if (!descriptor || descriptor.apiVersion !== 1 || !ID.test(descriptor.modId) ||
        !Number.isSafeInteger(descriptor.schemaVersion) || descriptor.schemaVersion < 1 ||
        !text(descriptor.displayName) || !Array.isArray(descriptor.entries) || !Array.isArray(descriptor.groups)) throw new Error("INVALID_DESCRIPTOR");
    const groups = new Map(), entries = new Map(), values = new Map(), defaults = {};
    for (const group of descriptor.groups) {
        if (!plain(group) || !ID.test(group.id) || groups.has(group.id) || !text(group.label) ||
            (group.order !== undefined && !Number.isFinite(group.order))) throw new Error("INVALID_GROUP");
        groups.set(group.id, deepFreeze(copy(group)));
    }
    for (const source of descriptor.entries) {
        if (!plain(source) || !ID.test(source.key) || ["__proto__", "constructor", "prototype"].includes(source.key) || entries.has(source.key)) throw new Error("INVALID_ENTRY_KEY");
        if (!TYPES.has(source.type)) { report({ code: "UNKNOWN_CONTROL", key: source.key, type: source.type }); continue; }
        const entry = copy(source);
        if (!text(entry.label) || !text(entry.description, true) || !text(entry.disabledReason, true) ||
            (entry.group !== undefined && !groups.has(entry.group)) ||
            (entry.applyMode !== undefined && entry.applyMode !== "immediate")) throw new Error("INVALID_ENTRY: " + entry.key);
        if (entry.type === "number") {
            if (![entry.min, entry.max, entry.step].every(Number.isFinite) || entry.min >= entry.max || entry.step <= 0 ||
                (entry.max - entry.min) / entry.step > 1e9 ||
                Math.abs((entry.max - entry.min) / entry.step - Math.round((entry.max - entry.min) / entry.step)) > 1e-7 ||
                (entry.format && !/^[FP][0-6]$/.test(entry.format))) throw new Error("INVALID_NUMBER: " + entry.key);
        }
        if (entry.type === "choice") {
            if (!Array.isArray(entry.options) || entry.options.length < 1 || entry.options.some(o => !plain(o) || typeof o.value !== "string" || !o.value || !text(o.label)) ||
                new Set(entry.options.map(o => o.value)).size !== entry.options.length) throw new Error("INVALID_OPTIONS: " + entry.key);
        }
        // Validate before JSON copying; otherwise a function predicate silently
        // disappears and a control that should be disabled becomes enabled.
        for (const rule of [source.enabledWhen, source.visibleWhen]) {
            if (rule !== undefined && (!plain(rule) || !ID.test(rule.key) || !own(rule, "equals"))) throw new Error("INVALID_CONDITION");
        }
        entries.set(entry.key, deepFreeze(entry));
        if (["toggle", "number", "choice"].includes(entry.type)) {
            const error = valueError(entry, entry.defaultValue);
            if (error) throw new Error("INVALID_DEFAULT: " + entry.key + " " + error);
            values.set(entry.key, entry);
            defaults[entry.key] = entry.defaultValue;
        }
    }
    for (const entry of entries.values()) for (const rule of [entry.enabledWhen, entry.visibleWhen]) {
        if (rule && (!values.has(rule.key) || valueError(values.get(rule.key), rule.equals))) throw new Error("INVALID_CONDITION_TARGET");
    }
    return { groups, entries, values, defaults: deepFreeze(defaults) };
}
function validatePatch(schema, patch) {
    const errors = {};
    if (!plain(patch)) return { _patch: "EXPECTED_OBJECT" };
    for (const [key, value] of Object.entries(patch)) {
        const entry = schema.values.get(key);
        const error = entry ? valueError(entry, value) : "UNKNOWN_KEY";
        if (error) errors[key] = error;
    }
    return errors;
}
function enabled(entry, values) { return !entry.enabledWhen || values[entry.enabledWhen.key] === entry.enabledWhen.equals; }
function visible(entry, values) { return !entry.visibleWhen || values[entry.visibleWhen.key] === entry.visibleWhen.equals; }
module.exports = { compile, validatePatch, valueError, enabled, visible, copy, deepFreeze, own, plain };
