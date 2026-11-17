"use strict";
const E = require("./Engine");
function bridge(action) {
    const armed = JSON.parse(E.cpp.loadModule("pbz-arena-v1/arm/" + action));
    if (armed.error) throw new Error(armed.error);
    // The native bridge consumes this exact call synchronously. It never
    // retains a raw UObject pointer across frames or accepts an address from JS.
    E.saves().GetManagerState();
    const result = JSON.parse(E.cpp.loadModule("pbz-arena-v1/result"));
    if (result.error) throw new Error(result.error);
    return result;
}
function slots() {
    const save = E.saves(), result = {};
    for (const name of save.GetAllSaveSlots()) {
        const out = E.cpp.$ref(new E.UE.JsonObjectWrapper());
        if (!save.LoadGameFromSlot(name, out)) throw new Error("arena_slot_read_failed");
        result[String(name)] = E.json(E.cpp.$unref(out));
    }
    return result;
}
class Isolation {
    status() { return bridge("status"); }
    prepare() {
        this.original = { slots: slots(), account: E.json(E.saves().GetAdvancedSaveJsonObject()) };
        bridge("prepare");
    }
    activate() {
        const state = bridge("activate");
        if (!state.hadOriginal) { E.saves().MarkAdvancedSaveDirty(); E.saves().SaveAdvancedSaveGameNow(); }
    }
    baseline() { return bridge("baseline"); }
    verify() { bridge("verify"); }
    restore() { bridge("restore"); E.saves().LoadAdvancedSaveGame(); }
    verifyCache(continueSlot) {
        if (continueSlot && Object.prototype.hasOwnProperty.call(this.original.slots, continueSlot)) E.saves().MarkLoadedSlotAsContinue(continueSlot);
        if (E.canonical(slots()) !== E.canonical(this.original.slots)
            || E.canonical(E.json(E.saves().GetAdvancedSaveJsonObject())) !== E.canonical(this.original.account))
            throw new Error("arena_original_cache_mismatch");
    }
    release(continueSlot) {
        this.verifyCache(continueSlot);
        bridge("release"); this.original = null;
    }
    cancel() { bridge("cancel"); }
}
module.exports = Isolation;
