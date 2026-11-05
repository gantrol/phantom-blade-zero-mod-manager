"use strict";
const UE = require("ue");
const helper = require("../Library/TsHelper").default;
const rule = require("./Rules");
let repairing = false;

function inventoryState(bag) {
    const item = bag.Inventories.GetRef(rule.item);
    // Copy primitives before any native grant can rearrange the inventory map.
    return item ? { amount: item.CurrentAmount, max: item.MaxAmount, cycle: item.AcquiredCycle } : undefined;
}

function restore(page) {
    const bag = helper.getWorldSubsystem(page, UE.PBZBackPackSubsystem.StaticClass());
    const game = helper.getWorldSubsystem(page, UE.PBZGameSystem.StaticClass());
    const events = helper.getWorldSubsystem(page, UE.EventPipelineManager.StaticClass());
    if (!bag || !game || !events) return;
    const item = inventoryState(bag);
    if (!item || item.amount !== 0 || item.max < 1 || item.cycle !== game.GetCycleCount()) return;
    const tags = new Set(), native = events.CacheTags.Tags.GameplayTags;
    for (let i = 0, n = native.Num(); i < n; i++) tags.add(String(native.GetRef(i).TagName));
    if (!rule.requiredTags.every(tag => tags.has(tag))) return;

    // Native grant updates the book's page state and normal inventory events.
    // Quantity is already known to be zero; the game persists it at its next save.
    bag.AddPropsToBackPack(1, rule.item, false);
    const after = inventoryState(bag);
    console.log("[PhantomBladeDocumentRecovery] " + JSON.stringify({
        event: after?.amount === 1 ? "restored" : "grant-unconfirmed",
        item: rule.item, book: rule.book, page: rule.page, quantity: after?.amount
    }));
    return after?.amount === 1;
}

function install(Type) {
    const proto = Type.prototype, name = "BP_InitWidget", original = proto[name];
    if (Object.prototype.hasOwnProperty.call(proto, "__documentRecoveryInstalled")) return;
    if (typeof original !== "function") throw Error("DOCUMENT_RECOVERY_INTERFACE_MISMATCH");
    Object.defineProperty(proto, "__documentRecoveryInstalled", { value: true });
    proto[name] = function (...args) {
        if (!repairing) {
            repairing = true;
            try {
                // BP_FirstInitWidget has already cached the owned pages. Rebuild
                // that cache before BP_InitWidget creates the visible book list.
                if (restore(this)) this.cacheOwnBookIDs();
            }
            catch (error) { console.warn("[PhantomBladeDocumentRecovery] " + String(error)); }
            finally { repairing = false; }
        }
        return original.apply(this, args);
    };
    console.log("[PhantomBladeDocumentRecovery] ready");
}

module.exports = { install };
