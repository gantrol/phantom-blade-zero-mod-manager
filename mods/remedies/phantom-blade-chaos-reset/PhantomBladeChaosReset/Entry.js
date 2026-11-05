"use strict";
const UE = require("ue");
const puerts = require("puerts");
const { FText } = require("cpp");
const { prepare } = require("./Patch");
const { translate } = require("./Locales");
const BUSY = "__chaosResetSaving";

function status(page, key, parameters) {
    const language = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage());
    page.LoadGameText.SetText(FText.FromString(translate(language, key, parameters)));
}

function createResetSave(page) {
    if (page[BUSY] || page.IsLoadGameOnDeactivate) return;
    const menuName = puerts.$ref("");
    UE.PBZBlueprintFunctionLibrary.GetGameDataPolicy().StartMenuPolicy.GetCorrectStartMenuMapName(menuName);
    const expectedMap = String(puerts.$unref(menuName)).split("/").pop().split(".").pop();
    if (String(UE.GameplayStatics.GetCurrentLevelName(page, true)) !== expectedMap) {
        status(page, "mainMenu");
        return;
    }
    const item = page.CommonListView_89.BP_GetSelectedItem();
    if (!item) { status(page, "noSelection"); return; }
    const sourceSlot = String(item.SlotName);
    if (!/^(?:AutoSave\d+|ManualSave[1-9]\d*)$/.test(sourceSlot)) {
        status(page, "nightmare");
        return;
    }
    const subsystem = UE.SubsystemBlueprintLibrary.GetGameInstanceSubsystem(page, UE.PBZSaveLoadSubsystem.StaticClass());
    if (subsystem.GetManualSaveCount() >= 20) { status(page, "full"); return; }
    const slots = subsystem.GetAllSaveSlots();
    let maximum = 0;
    for (let i = 0; i < slots.Num(); i++) {
        const match = /^ManualSave([1-9]\d*)$/.exec(String(slots.Get(i)));
        if (match) maximum = Math.max(maximum, Number(match[1]));
    }
    const destination = "ManualSave" + (maximum + 1);
    const wrapper = puerts.$ref(new UE.JsonObjectWrapper());
    if (!subsystem.LoadGameFromSlot(sourceSlot, wrapper)) { status(page, "loadFailed"); return; }
    const text = puerts.$ref("");
    if (!UE.JsonBlueprintFunctionLibrary.ToString(puerts.$unref(wrapper), text)) {
        status(page, "loadFailed");
        return;
    }
    const plan = prepare(JSON.parse(puerts.$unref(text)));
    const payload = JSON.stringify(plan.save);
    const param = new UE.SaveGameParam();
    param.SaveSlotName = destination;
    param.bShowSaveTip = false;
    page[BUSY] = true;
    page.CommonListView_89.SetIsEnabled(false);
    status(page, "saving");

    function finish(success) {
        page[BUSY] = false;
        page.CommonListView_89.SetIsEnabled(true);
        if (!success) { status(page, "failed"); return; }
        if (!page.IsActivated()) return;
        page.initListView();
        for (let i = 0; i < page.CommonListView_89.GetNumItems(); i++) {
            const entry = page.CommonListView_89.GetItemAt(i);
            if (String(entry.SlotName) !== destination) continue;
            page.CommonListView_89.SetSelectedIndex(i);
            page.CommonListView_89.BP_NavigateToItem(entry);
            break;
        }
        status(page, "ready", { slot: maximum + 1 });
        console.warn("[PhantomBladeChaosReset] " + JSON.stringify({ sourceSlot, destination, changedFields: plan.changes.length }));
    }
    try {
        // Native serialization and asynchronous save; no mod filesystem I/O.
        const result = subsystem.ReplaceSaveString(param, puerts.toDelegate(page,
            (_slot, _json, _index, success) => finish(success)), payload);
        if (result.ResultType !== UE.ESaveLoadResultType.Success) finish(false);
    } catch (error) {
        finish(false);
        throw error;
    }
}

function install(LoadGameInterface) {
    const proto = LoadGameInterface.prototype;
    if (proto.__chaosResetInstalled) return;
    for (const name of ["OnPreviewKeyDown", "BP_OnActivated", "confirmSelectedSlot", "initListView"]) {
        if (typeof proto[name] !== "function") throw new Error("load_menu_changed");
    }
    Object.defineProperty(proto, "__chaosResetInstalled", { value: true });
    const activate = proto.BP_OnActivated;
    proto.BP_OnActivated = function (...args) {
        const result = activate.apply(this, args);
        this[BUSY] = false;
        status(this, "hint");
        return result;
    };
    const confirm = proto.confirmSelectedSlot;
    proto.confirmSelectedSlot = function (...args) {
        if (!this[BUSY]) return confirm.apply(this, args);
    };
    const keyDown = proto.OnPreviewKeyDown;
    proto.OnPreviewKeyDown = function (geometry, event) {
        if (this[BUSY]) return UE.WidgetBlueprintLibrary.Handled();
        const input = UE.KismetInputLibrary;
        if (String(input.GetKey(event).KeyName) !== "F9") {
            return keyDown.call(this, geometry, event);
        }
        if (!input.InputEvent_IsRepeat(event)) {
            try { createResetSave(this); }
            catch (error) {
                status(this, String(error.message));
                console.warn("[PhantomBladeChaosReset] " + String(error));
            }
        }
        return UE.WidgetBlueprintLibrary.Handled();
    };
    console.warn("[PhantomBladeChaosReset] 0.1.0 loaded");
}

module.exports = { install };
