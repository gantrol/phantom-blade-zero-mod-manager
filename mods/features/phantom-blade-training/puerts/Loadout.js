"use strict";
const E = require("./Engine"), equipment = require("./Equipment.generated").weapons;
const builtin = require("./Template.generated");
let template = builtin, overridden = false;
const table = main => main ? "MainWeaponInfos" : "ShaChiWeaponInfo";
const levelField = main => main ? "WeaponLevel" : "ShaChiWeaponLevel";
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
function validate(save) {
    if (!save?.PBZGameSystem || !save.Inventory?.MainWeaponInfos || !save.Inventory?.ShaChiWeaponInfo) throw new Error("arena_profile_invalid");
    return save;
}
function profile(save, source) {
    validate(save);
    const inv = save.Inventory, weapons = [];
    for (const main of [true, false]) {
        const equipped = inv[main ? "MainWeapons" : "ShaqiWeapons"] || [];
        for (let slot = 0; slot < 2; slot++) {
            const id = equipped[slot] && equipped[slot] !== "None" ? equipped[slot] : "";
            weapons.push({ id, level: id ? Number(inv[table(main)][id][levelField(main)] || 0) : 1 });
        }
    }
    return { source, weapons, activeMain: inv.CurrentWeaponIndex || 0, activePhantom: inv.CurrentShaqiWeaponIndex || 0,
        maxHealth: 0, difficulty: save.PBZGameSystem.DifficultModeByte, cycle: save.PBZGameSystem.CycleCount || 0, bossHealth: 100, bossAttack: 100 };
}
function owned(save, live = false) {
    validate(save);
    return equipment.flatMap(meta => {
        const main = meta.kind === "main", record = save.Inventory[table(main)][meta.id];
        if (!record) return [];
        const level = Number(record[levelField(main)] || 0), max = live ? E.bag().GetWeaponMaxLevel(meta.id, main)
            : Math.max(level, Math.min(meta.upgradeCount + 1, 30 + 5 * clamp(record.ReincarnationCount || 0, 0, 6)));
        return [{ ...meta, level, min: level === 0 ? 0 : 1, max, snapSoul: !!record.AlreadySnapSoul }];
    });
}
function liveWeapons(config) {
    const bag = E.bag();
    [Array.from(bag.GetEquipmentNormalWeapons()), Array.from(bag.GetShaqiWeapons())].forEach((ids, group) => {
        for (let slot = 0; slot < 2; slot++) {
            const id = ids[slot] && String(ids[slot]) !== "None" ? String(ids[slot]) : "";
            config.weapons[group * 2 + slot] = { id, level: id ? bag.GetWeaponLevel(id, group === 0) : 1 };
        }
    });
    config.activeMain = bag.GetCurrentMainWeaponIndex(); config.activePhantom = bag.GetCurrentShaqiWeaponIndex();
}
function apply(save, baseline, config, pool) {
    validate(save); validate(baseline);
    if (!Number.isInteger(config.difficulty) || config.difficulty < 0 || config.difficulty > 4
        || !Number.isInteger(config.cycle) || config.cycle < 0 || config.cycle > 7) throw new Error("arena_difficulty_invalid");
    save.PBZGameSystem.DifficultModeByte = config.difficulty; save.PBZGameSystem.CycleCount = config.cycle;
    for (const main of [true, false]) {
        const offset = main ? 0 : 2, ids = [], records = save.Inventory[table(main)];
        for (let slot = 0; slot < 2; slot++) {
            const weapon = config.weapons[offset + slot];
            if (!weapon.id) {
                if (!slot && (main || config.weapons[offset + 1].id)) throw new Error("arena_empty_primary_slot");
                continue;
            }
            const item = pool.find(entry => entry.id === weapon.id && (entry.kind === "main") === main);
            if (!item || !baseline.Inventory[table(main)][weapon.id] || !records[weapon.id] || ids.includes(weapon.id)
                || !Number.isInteger(weapon.level) || weapon.level < item.min || weapon.level > item.max) throw new Error("arena_weapon_invalid");
            records[weapon.id][levelField(main)] = weapon.level; ids.push(weapon.id);
        }
        save.Inventory[main ? "MainWeapons" : "ShaqiWeapons"] = ids;
        const active = ids.length ? clamp(main ? config.activeMain : config.activePhantom, 0, ids.length - 1) : 0;
        save.Inventory[main ? "CurrentWeaponIndex" : "CurrentShaqiWeaponIndex"] = active;
        if (main) save.Inventory.CurrentMainWeaponID = ids[active];
    }
}
function verify(config) {
    const actual = E.copy(config); liveWeapons(actual);
    if (E.canonical(actual.weapons) !== E.canonical(config.weapons)) throw new Error("arena_equipment_apply_mismatch");
}
module.exports = { profile, owned, apply, verify, liveWeapons, clamp, equipment,
    template: () => template, overridden: () => overridden,
    setTemplate(value) { template = validate(value); overridden = true; } };
