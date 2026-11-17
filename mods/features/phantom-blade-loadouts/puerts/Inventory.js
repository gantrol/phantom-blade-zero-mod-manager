"use strict";
const UE = require("ue");
const Helper = require("../Library/TsHelper").default;
const UI = require("../Library/UIHelper").default;
const Search = require("./Search");
const locales = require("./Locales.generated");

const methods = { main: "getSortedMainWeaponArray", shadow: "getSortedShaChiWeaponArray",
    accessories: "getSortedAccessoryArray", medicines: "getSortedMedicineArray" };
function id(value) { const text = String(value || ""); return text.toLowerCase() === "none" ? "" : text; }
function array(value) { return value ? Array.from(value) : []; }
function valid(value) { return !!value && UE.KismetSystemLibrary.IsValid(value); }
function bag(page) { return Helper.getWorldSubsystem(page, UE.PBZBackPackSubsystem.StaticClass()); }
function language() { return String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).toLowerCase().startsWith("zh") ? "zh-CN" : "en"; }
function t(key, values = {}) {
    let text = locales[language()][key] || locales.en[key] || key;
    for (const [name, value] of Object.entries(values)) text = text.split("{" + name + "}").join(String(value));
    return text;
}
function localized(value) {
    const key = id(value?.Key);
    if (!key) return "";
    return UE.KismetTextLibrary.Conv_TextToString(UE.PBZBlueprintFunctionLibrary.MakeTextByKey(key));
}
function item(backpack, kind, itemId) {
    let data, info, name, aliases, descriptions = [], type, summon = false, forged = false;
    if (kind === "main" || kind === "shadow") {
        const main = kind === "main";
        info = (main ? backpack.MainWeaponInfos : backpack.ShaqiWeaponInfos).Get(itemId);
        data = main ? backpack.GetMainWeaponTableData(itemId) : backpack.GetShaqiWeaponTableData(itemId);
        const soul = main ? data.WeaponSnapSoulData : data.ShaChiWeaponSnapSoulData;
        forged = !!info?.bAlreadySnapSoul;
        aliases = [localized(data.WeaponName), localized(soul?.WeaponSnapSoulName)];
        name = forged && aliases[1] ? aliases[1] : aliases[0];
        summon = !main && data.ShaChiWeaponType === UE.EShaChiWeaponType.Summon;
    } else if (kind === "accessories") {
        info = backpack.Accessories.Get(itemId);
        data = UE.PBZBackPackSubsystem.GetAccessoryData(itemId);
        const levels = array(data.AccessoryLevelDatas), level = Number(info?.CurrentLevel || 0);
        const current = level > 0 ? levels[level - 1] : data.DefaultAccessoryData;
        aliases = [localized(data.AccessoryName), ...levels.map(entry => localized(entry.AccessoryName))];
        name = localized(current?.AccessoryName) || aliases[0];
        descriptions = [localized(data.AccessoryDesc), ...array(current?.GEData).map(entry => localized(entry.FlowLocText))];
        type = Number(data.AccessoryType);
    } else {
        info = backpack.MedicineInfos.Get(itemId);
        data = UE.PBZBackPackSubsystem.GetMedicineData(itemId);
        name = localized(data.MedicineName); aliases = [name];
    }
    return { id: itemId, kind, name: name || itemId, aliases, description: Search.plain(descriptions.join(" ")),
        category: t(kind === "accessories" ? "category.accessory." + type : "category." + kind),
        type, summon, forged, level: Number(info?.WeaponLevel ?? info?.CurrentLevel ?? 0), uses: Number(info?.CurrentTime || 0) };
}
function owned(backpack, kind) { return array(UI[methods[kind]](backpack)).map(id).filter(Boolean); }
function equipment(backpack) {
    function slots(value, count) { return Array.from({ length: count }, (_, i) => value.IsValidIndex(i) ? id(value.Get(i)) : ""); }
    return { main: slots(backpack.MainWeapons, 2), shadow: slots(backpack.ShaqiWeapons, 2),
        accessories: slots(backpack.AssemblyAccessory, 6), medicines: [id(backpack.BottomMedicineID), id(backpack.TopMedicineID)] };
}
function arena(page) {
    // The PuerTS arena marks its isolated pawn, independent of HUD visibility.
    return !!UE.GameplayStatics.GetPlayerCharacter(page, 0)?.ActorHasTag("PhantomBladeArena.Session");
}
function locked(page) {
    if (!valid(page) || !valid(bag(page)) || !valid(UE.GameplayStatics.GetPlayerCharacter(page, 0))) return "state.locked";
    if (page.isLoadoutLockedByStory() || UI.isInCombat(page) || UE.PBZBlueprintFunctionLibrary.GetUILayOut(page)?.HasModal()) return "state.locked";
    return arena(page) ? "state.arena" : "";
}
function capture(page) {
    const backpack = bag(page), inventory = {};
    if (!valid(backpack)) throw new Error("state.locked");
    for (const kind of Object.keys(methods)) for (const itemId of owned(backpack, kind)) inventory[itemId] = item(backpack, kind, itemId);
    const max = Number(backpack.GetCanAssembleAccessoryNum());
    const unlocked = Array.from({ length: 6 }, (_, i) => i === 0 ? max > 1 : max >= i + 1);
    const active = { main: Number(backpack.GetCurrentMainWeaponIndex()), shadow: Number(backpack.GetCurrentShaqiWeaponIndex()) };
    if (![active.main, active.shadow].every(index => index === 0 || index === 1)) throw new Error("state.invalid");
    const game = Helper.getWorldSubsystem(page, UE.PBZGameSystem.StaticClass());
    const cycle = game ? Number(game.GetCycleCount()) : null;
    const snapshot = { backpack, player: UE.GameplayStatics.GetPlayerCharacter(page, 0), world: page.GetWorld(), game,
        cycle: Number.isInteger(cycle) && cycle >= 0 ? cycle : null, equipment: equipment(backpack), inventory, unlocked, active, locked: locked(page) };
    snapshot.signature = JSON.stringify([snapshot.equipment, active, unlocked, snapshot.cycle,
        Object.values(inventory).map(entry => [entry.id, entry.level, entry.forged, entry.uses])]);
    return snapshot;
}
function sameSession(a, b) { return a.backpack === b.backpack && a.player === b.player && a.world === b.world && a.game === b.game && a.cycle === b.cycle; }
function execute(snapshot, step) {
    const backpack = snapshot.backpack;
    if (step.kind === "accessories") {
        if (step.id) backpack.AssembleAccessory(step.id, step.slot);
        else backpack.TakeOffAccessory(step.slot);
    } else if (step.kind === "main") backpack.ReplaceWeaponByIndex(step.id, step.slot);
    else if (step.kind === "shadow") backpack.ReplaceShaChiWeaponByIndex(step.id, step.slot);
    else backpack.ReplaceMedicineByIndex(step.slot, step.id, false);
}
function label(backpack, kind, itemId) { return itemId ? item(backpack, kind, itemId).name : t("slot.empty"); }
module.exports = { UE, UI, id, valid, bag, language, t, item, owned, equipment, locked, capture, sameSession, execute, label };
