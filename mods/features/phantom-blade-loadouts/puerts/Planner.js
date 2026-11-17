"use strict";

const groups = ["main", "shadow", "accessories", "medicines"];
function copy(equipment) { return Object.fromEntries(groups.map(kind => [kind, [...equipment[kind]]])); }
function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }
function legal(equipment, inventory, unlocked) {
    for (const kind of groups) {
        const ids = equipment[kind].filter(Boolean);
        if (new Set(ids).size !== ids.length || ids.some(id => inventory[id]?.kind !== kind)) return false;
    }
    if (equipment.accessories.some((id, slot) => id && !unlocked[slot])) return false;
    const counts = new Map();
    for (const id of equipment.accessories.filter(Boolean)) {
        const type = inventory[id].type;
        counts.set(type, (counts.get(type) || 0) + 1);
        if (counts.get(type) > 3) return false;
    }
    return equipment.shadow.filter(id => id && inventory[id].summon).length <= 1;
}
function stepsFor(before, target, inventory, unlocked) {
    const current = copy(before), steps = [];
    function put(kind, slot, id) {
        current[kind][slot] = id;
        if (!legal(current, inventory, unlocked)) throw new Error("state.invalid");
        steps.push({ kind, slot, id, expected: copy(current) });
    }
    // Remove changed accessories first: every intermediate state stays within
    // the category cap, and items moved between slots are never duplicated.
    for (let i = 0; i < 6; i++) if (current.accessories[i] && current.accessories[i] !== target.accessories[i]) put("accessories", i, "");
    for (const kind of ["main", "shadow", "medicines"]) {
        const pending = [0, 1].filter(slot => current[kind][slot] !== target[kind][slot]);
        while (pending.length) {
            const next = pending.findIndex(slot => {
                const id = target[kind][slot];
                if (!id || current[kind].includes(id)) return false;
                const candidate = copy(current); candidate[kind][slot] = id;
                return legal(candidate, inventory, unlocked);
            });
            // Native two-slot swaps and empty-weapon transitions are not yet
            // verified. Reject the whole plan before touching equipment.
            if (next < 0) throw new Error("state.swap");
            const slot = pending.splice(next, 1)[0];
            put(kind, slot, target[kind][slot]);
        }
        if (kind === "shadow") {
            for (let i = 0; i < 6; i++) if (current.accessories[i] !== target.accessories[i]) put("accessories", i, target.accessories[i]);
        }
    }
    return steps;
}
function plan(preset, snapshot, includeMedicines = false) {
    const target = copy(snapshot.equipment), errors = [], missingPriority = [];
    const fail = (key, items = []) => errors.push({ key, items });
    const inventory = snapshot.inventory;
    if (snapshot.locked) fail(snapshot.locked);
    for (const id of preset.requirements.milestoneItemIds) if (!inventory[id]) fail("state.missing", [id]);
    for (const id of preset.requirements.soulForgedItemIds) if (!inventory[id]?.forged) fail("state.forge", [id]);
    if (snapshot.unlocked.filter(Boolean).length < preset.requirements.accessorySlotsMin) fail("state.slots");
    for (const kind of ["main", "shadow", ...(includeMedicines ? ["medicines"] : [])]) {
        preset.equipment[kind].forEach((id, index) => {
            if (id === null) return;
            if (inventory[id]?.kind !== kind) fail("state.missing", [id]);
            const slot = kind === "main" || kind === "shadow" ? (index === 0 ? snapshot.active[kind] : 1 - snapshot.active[kind]) : index;
            target[kind][slot] = id;
        });
    }
    const accessories = preset.equipment.accessories, ids = [], counts = new Map();
    for (const id of accessories.itemIds) {
        const item = inventory[id];
        if (item?.kind !== "accessories") {
            if (accessories.mode !== "priority") fail("state.missing", [id]); else missingPriority.push(id);
            continue;
        }
        if (ids.length >= snapshot.unlocked.filter(Boolean).length || (counts.get(item.type) || 0) >= 3) {
            if (accessories.mode !== "priority") fail("state.invalid");
            continue;
        }
        counts.set(item.type, (counts.get(item.type) || 0) + 1); ids.push(id);
    }
    if (accessories.mode !== "exact") for (const id of target.accessories) {
        const item = inventory[id];
        if (!item || ids.includes(id) || ids.length >= snapshot.unlocked.filter(Boolean).length || (counts.get(item.type) || 0) >= 3) continue;
        counts.set(item.type, (counts.get(item.type) || 0) + 1); ids.push(id);
    }
    // Keep desired accessories in their existing slots wherever possible.
    target.accessories = target.accessories.map((id, slot) => snapshot.unlocked[slot] && ids.includes(id) ? id : "");
    for (const id of ids) if (!target.accessories.includes(id)) {
        const slot = target.accessories.findIndex((value, i) => !value && snapshot.unlocked[i]);
        if (slot >= 0) target.accessories[slot] = id;
    }
    if (!legal(target, inventory, snapshot.unlocked)) fail("state.invalid");
    let steps = [];
    if (!errors.length) try { steps = stepsFor(snapshot.equipment, target, inventory, snapshot.unlocked); }
    catch (error) { fail(error.message); }
    const differences = groups.flatMap(kind => target[kind].map((id, slot) => ({ kind, slot, before: snapshot.equipment[kind][slot], after: id }))
        .filter(change => change.before !== change.after));
    return { preset, snapshot, target, steps, differences, errors, missingPriority, includeMedicines };
}
module.exports = { plan, stepsFor, legal, copy, same };
