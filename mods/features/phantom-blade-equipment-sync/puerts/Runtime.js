"use strict";
const UE = require("ue");
const TsHelper = require("../Library/TsHelper").default;

let syncing = false;

function syncCategory(entries, upgradeFn) {
    let maxLevel = 0;
    for (const entry of entries) {
        if (Number.isInteger(entry.level) && entry.level > maxLevel) {
            maxLevel = entry.level;
        }
    }
    if (maxLevel <= 1) return 0;
    let upgraded = 0;
    for (const entry of entries) {
        if (Number.isInteger(entry.level) && entry.level > 0 && entry.level < maxLevel) {
            upgradeFn(entry.id, maxLevel - entry.level);
            upgraded++;
        }
    }
    return upgraded;
}

function collectLevels(map, levelField) {
    const entries = [];
    if (!map) return entries;
    const maxIdx = map.GetMaxIndex();
    for (let i = 0; i < maxIdx; i++) {
        if (!map.IsValidIndex(i)) continue;
        const key = map.GetKey(i);
        const id = key ? String(key) : "";
        if (!id || id === "None") continue;
        const info = map.GetRef ? map.GetRef(key) : map.Get(key);
        if (info) {
            entries.push({ id, level: Number(info[levelField]) });
        }
    }
    return entries;
}

function syncAll(backpack) {
    if (!backpack || syncing) return;
    syncing = true;
    try {
        const mainEntries = collectLevels(backpack.MainWeaponInfos, "WeaponLevel");
        const shaqiEntries = collectLevels(backpack.ShaqiWeaponInfos, "ShaChiWeaponLevel");
        const accessoryEntries = collectLevels(backpack.Accessories, "CurrentLevel");

        const mainCount = syncCategory(mainEntries, (id, delta) => backpack.UpgradeWeapon(id, true, delta));
        const shaqiCount = syncCategory(shaqiEntries, (id, delta) => backpack.UpgradeWeapon(id, false, delta));
        const accCount = syncCategory(accessoryEntries, (id, delta) => backpack.UpgradeAccessory(id, delta));
        if (mainCount > 0 || shaqiCount > 0 || accCount > 0) {
            console.log(`[PhantomBladeEquipmentSync] synced main=${mainCount} shaqi=${shaqiCount} accessory=${accCount}`);
        }
    } catch (error) {
        console.warn("[PhantomBladeEquipmentSync] sync failed: " + String(error));
    } finally {
        syncing = false;
    }
}

function install(InterfaceType) {
    const proto = InterfaceType && InterfaceType.prototype;
    if (!proto || proto.__pbzEquipmentSyncInstalled) return;
    Object.defineProperty(proto, "__pbzEquipmentSyncInstalled", { value: true });

    const originalReCheck = proto.ReCheckItemUpgradeState;
    if (typeof originalReCheck === "function") {
        proto.ReCheckItemUpgradeState = function (backPackSubsystem) {
            syncAll(backPackSubsystem);
            return originalReCheck.apply(this, arguments);
        };
    }

    const originalBeginPlay = proto.ReceiveBeginPlay;
    if (typeof originalBeginPlay === "function") {
        proto.ReceiveBeginPlay = function () {
            const result = originalBeginPlay.apply(this, arguments);
            if (this.__pbzEquipSyncTimer) clearTimeout(this.__pbzEquipSyncTimer);
            this.__pbzEquipSyncTimer = setTimeout(() => {
                this.__pbzEquipSyncTimer = 0;
                try {
                    const sub = TsHelper.getWorldSubsystem(this, UE.PBZBackPackSubsystem.StaticClass());
                    syncAll(sub);
                } catch (_) {
                }
            }, 600);
            return result;
        };
    }

    const originalEndPlay = proto.ReceiveEndPlay;
    if (typeof originalEndPlay === "function") {
        proto.ReceiveEndPlay = function () {
            if (this.__pbzEquipSyncTimer) {
                clearTimeout(this.__pbzEquipSyncTimer);
                this.__pbzEquipSyncTimer = 0;
            }
            return originalEndPlay.apply(this, arguments);
        };
    }
}

module.exports = { install, syncAll };
