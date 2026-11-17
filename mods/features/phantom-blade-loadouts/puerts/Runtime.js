"use strict";
const I = require("./Inventory");
const Planner = require("./Planner");
const Search = require("./Search");
const View = require("./View");
const presets = require("./Presets.generated").presets;
const { UE } = I;
const states = new WeakMap();

function report(error) { console.error("[PhantomBladeLoadouts] " + String(error?.stack || error)); }
function selected(page) {
    const button = page.W_SystemPanel_V1.ButtonGroup.GetSelectedButtonBase();
    return button ? I.id(page.getItemFromButton(button)) : "";
}
function refreshPresets(state) {
    const snapshot = state.plan?.snapshot;
    const visible = presets.filter(preset => state.tab !== "progress" || snapshot?.cycle !== null && snapshot?.cycle !== undefined
        && snapshot.cycle >= preset.recommendation.cycleCountMin
        && (preset.recommendation.cycleCountMax === undefined || snapshot.cycle <= preset.recommendation.cycleCountMax));
    const entries = visible.map(preset => Search.index({ id: preset.id, name: I.t(preset.labelKey),
        description: [I.t(preset.summaryKey), ...preset.useCaseIds.map(id => I.t("use." + id)),
            ...Object.entries(preset.equipment).flatMap(([kind, values]) => (kind === "accessories" ? values.itemIds : values)
                .filter(Boolean).map(id => I.label(I.bag(state.page), kind, id)))].join(" "), preset }));
    const found = Search.query(entries, state.filter).map(entry => entry.preset);
    if (!Search.normalize(state.filter)) found.sort((a, b) => b.recommendationPriority - a.recommendationPriority || a.id.localeCompare(b.id));
    state.matches = found.map(preset => preset.id);
    if (found.length && !state.matches.includes(state.preset.id)) { state.preset = found[0]; preview(state); }
    state.view.presets(found, state.preset.id, id => { if (state.job) return; state.preset = presets.find(preset => preset.id === id); preview(state); refreshPresets(state); });
    if (!found.length) {
        state.view.description.SetText(state.view.text(I.t("search.emptyPresets")));
        state.view.apply.SetIsEnabled(false); state.view.message("search.emptyPresets");
    }
}
function preview(state, message) {
    state.plan = Planner.plan(state.preset, I.capture(state.page), state.includeMedicines);
    if (state.undo && (!I.sameSession(state.undo.after, state.plan.snapshot) || state.undo.after.signature !== state.plan.snapshot.signature)) state.undo = null;
    state.view.preview(state.plan, !!state.undo);
    if (message) state.view.message(message);
}
function open(state) {
    if (state.job || state.page.IsSecondStep()) return;
    preview(state); refreshPresets(state); state.view.open(true);
}
function close(state) { if (!state.job && !state.confirmation && !state.confirmedPlan) state.view.open(false); }
function fresh(state, plan) {
    const now = I.capture(state.page);
    return !now.locked && I.sameSession(now, plan.snapshot) && now.signature === plan.snapshot.signature;
}
function start(state, plan, undoing = false) {
    if (state.job || !fresh(state, plan)) { preview(state, "state.changed"); return; }
    state.undo = null;
    state.job = { plan, before: plan.snapshot, steps: plan.steps, index: 0, expected: plan.snapshot.equipment, undoing, rollback: false };
    state.view.busy(true); state.view.message("state.applying");
}
function apply(state) {
    const plan = state.plan;
    if (!plan || state.job || state.confirmation || state.confirmedPlan || !state.matches?.includes(state.preset.id) || plan.errors.length || !plan.steps.length) return;
    if (!fresh(state, plan)) { preview(state, "state.changed"); return; }
    const changingMedicine = plan.steps.some(step => step.kind === "medicines");
    if (!changingMedicine) { start(state, plan); return; }
    const action = UE.AsyncAction_ShowConfirmation.ShowConfirmationYesNo(state.page, undefined,
        UE.KismetTextLibrary.Conv_StringToText(I.t("medicine.confirm")));
    state.confirmation = action;
    const result = value => {
        action.OnResult.Remove(result); state.confirmation = null;
        if (states.get(state.page) !== state) return;
        if (value === UE.ECommonMessagingResult.Confirmed) state.confirmedPlan = plan;
    };
    action.OnResult.Add(result); action.Activate();
}
function undo(state) {
    if (!state.undo || state.job || state.confirmation) return;
    const now = I.capture(state.page), record = state.undo;
    if (now.locked || !I.sameSession(record.after, now) || record.after.signature !== now.signature) { state.undo = null; preview(state, "state.changed"); return; }
    const target = Planner.copy(record.before.equipment); target.medicines = [...now.equipment.medicines];
    try {
        const steps = Planner.stepsFor(now.equipment, target, now.inventory, now.unlocked);
        start(state, { snapshot: now, target, steps, includeMedicines: false }, true);
    } catch (error) { state.view.message(error.message); }
}
function finish(state, message, snapshot) {
    const job = state.job; state.job = null; state.view.busy(false);
    if (message === "state.applied" && !job.undoing && !job.rollback) state.undo = { before: job.before, after: snapshot };
    preview(state, message);
}
function fail(state, now, canRestore) {
    const job = state.job;
    if (!job.rollback && canRestore && I.sameSession(job.before, now) && !now.locked) {
        const target = Planner.copy(job.before.equipment); target.medicines = [...now.equipment.medicines];
        try {
            job.steps = Planner.stepsFor(now.equipment, target, now.inventory, now.unlocked);
            job.index = 0; job.expected = now.equipment; job.pending = null; job.rollback = true;
            state.view.message("state.restoring"); return;
        } catch (error) { report(error); }
    }
    finish(state, "state.partial", now);
}
function step(state) {
    const job = state.job;
    if (!job) return;
    const backpack = I.bag(state.page);
    if (backpack !== job.before.backpack || UE.GameplayStatics.GetPlayerCharacter(state.page, 0) !== job.before.player || state.page.GetWorld() !== job.before.world) {
        finish(state, "state.partial", I.capture(state.page)); return;
    }
    const actual = I.equipment(backpack);
    if (job.pending) {
        if (Planner.same(actual, job.pending.expected)) {
            job.expected = actual; job.pending = null; job.index++;
        } else if (!Planner.same(actual, job.expected) || Date.now() > job.deadline) {
            fail(state, I.capture(state.page), Planner.same(actual, job.expected)); return;
        } else return;
    }
    const now = I.capture(state.page);
    if (now.locked || !I.sameSession(job.before, now) || !Planner.same(actual, job.expected)) { fail(state, now, false); return; }
    if (job.index >= job.steps.length) {
        finish(state, job.rollback ? (job.plan.includeMedicines ? "state.failedMedicines" : "state.failed") : job.undoing ? "state.restored" : "state.applied", now);
        return;
    }
    job.pending = job.steps[job.index]; job.deadline = Date.now() + 1200;
    try { I.execute(now, job.pending); }
    catch (error) { report(error); fail(state, I.capture(state.page), Planner.same(I.equipment(backpack), job.expected)); }
}
function rebuildSearch(state) {
    if (!state.searchContext || state.job || !state.page.IsSecondStep() || state.page.CurrentItemType !== UE.EDisplayItemType.Accessory) return;
    const [tab, backpack] = state.searchContext;
    state.page.initAccessoryByTabID(tab, backpack, selected(state.page));
}
function searchSlots(state, original, panel, args) {
    const ids = args[0];
    state.nativeIds = [...ids];
    const signature = JSON.stringify([I.language(), ids.map(id => [String(id), Number(I.bag(state.page).Accessories.Get(id)?.CurrentLevel || 0)])]);
    if (state.indexSignature !== signature) {
        state.indexSignature = signature; state.index = []; state.indexCursor = 0;
        state.view.search.SetIsEnabled(false);
    }
    if (state.indexCursor === ids.length) {
        state.view.search.SetIsEnabled(true);
        const result = Search.query(state.index, state.query).map(item => item.id);
        ids.splice(0, ids.length, ...result);
    }
    state.page.ChangeInputBar.Broadcast(ids.length > 0, "WeaponConfirm");
    const previous = state.restoreSelection || selected(state.page), result = original.apply(panel, args);
    state.restoreSelection = "";
    const group = panel.ButtonGroup;
    if (ids.length) {
        const keep = ids.indexOf(previous);
        group.SelectButtonAtIndex(keep < 0 ? 0 : keep);
    } else group.DeselectAll();
    return result;
}
function attach(page) {
    detach(page);
    const state = { page, preset: presets.find(p => p.id === "chiren_skill") || presets[0], query: "", filter: "", tab: "all", includeMedicines: false };
    states.set(page, state);
    try {
        state.view = new View(page, {
            open: () => open(state), close: () => close(state), apply: () => apply(state), undo: () => undo(state),
            search: query => {
                if (!state.query && query) state.beforeSearch = selected(page);
                if (!query) state.restoreSelection = state.beforeSearch;
                state.query = query; state.searchDue = Date.now() + 150;
            },
            filter: query => { state.filter = query; state.filterDue = Date.now() + 150; },
            tab: tab => { if (!state.job) { state.tab = tab; refreshPresets(state); } },
            medicines: () => { if (!state.job && !state.confirmation && !state.confirmedPlan && state.matches?.includes(state.preset.id)) { state.includeMedicines = !state.includeMedicines; preview(state); } }
        });
        const panel = page.W_SystemPanel_V1;
        state.originalSlots = panel.createSlotsByClass;
        state.slotsWrapper = function (...args) {
            return state.inAccessoryInit ? searchSlots(state, state.originalSlots, this, args) : state.originalSlots.apply(this, args);
        };
        panel.createSlotsByClass = state.slotsWrapper;
    } catch (error) { detach(page); report(error); }
}
function detach(page) {
    const state = states.get(page);
    if (!state) return;
    states.delete(page);
    if (state.job) report("Page closed during loadout; remaining operations cancelled. Current equipment must be reviewed.");
    if (page.W_SystemPanel_V1.createSlotsByClass === state.slotsWrapper) page.W_SystemPanel_V1.createSlotsByClass = state.originalSlots;
    state.view?.dispose();
}
function tick(page) {
    const state = states.get(page);
    if (!state?.view) return;
    try {
        state.view.layout(page.IsSecondStep() && page.CurrentItemType === UE.EDisplayItemType.Accessory);
        if (state.job) { step(state); return; }
        if (state.confirmedPlan && !UE.PBZBlueprintFunctionLibrary.GetUILayOut(page)?.HasModal()) {
            const plan = state.confirmedPlan; state.confirmedPlan = null; start(state, plan); return;
        }
        if (state.nativeIds && state.indexCursor < state.nativeIds.length) {
            const backpack = I.bag(page), end = Math.min(state.nativeIds.length, state.indexCursor + 8);
            while (state.indexCursor < end) state.index.push(Search.index(I.item(backpack, "accessories", state.nativeIds[state.indexCursor++])));
            if (state.indexCursor === state.nativeIds.length) { state.view.search.SetIsEnabled(true); rebuildSearch(state); }
        }
        if (state.searchDue && Date.now() >= state.searchDue) { state.searchDue = 0; rebuildSearch(state); }
        if (state.filterDue && Date.now() >= state.filterDue) { state.filterDue = 0; refreshPresets(state); }
    } catch (error) { report(error); if (state.job) { state.job = null; state.view.busy(false); } state.view.message("state.partial"); }
}
function install() {
    const proto = require("UI/Widget/Character_V2").CharacterUI_V2MixinInterface.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__loadouts")) return;
    const required = ["BP_InitWidget", "FinishCurrentWidget", "Destruct", "Tick", "initAccessoryByTabID", "ReplaceAccessory", "ReplaceMainWeapon", "ReplaceShaChiWeapon", "replaceMedicine", "RemoveItem", "navigateBack", "isLoadoutLockedByStory"];
    for (const name of required) if (typeof proto[name] !== "function") throw new Error("LOADOUTS_INTERFACE_MISMATCH: " + name);
    function after(name, callback) {
        const original = proto[name];
        proto[name] = function (...args) { const result = original.apply(this, args); callback(this); return result; };
    }
    after("BP_InitWidget", attach); after("Tick", tick);
    for (const name of ["FinishCurrentWidget", "Destruct"]) {
        const original = proto[name]; proto[name] = function (...args) { detach(this); return original.apply(this, args); };
    }
    const init = proto.initAccessoryByTabID;
    proto.initAccessoryByTabID = function (...args) {
        const state = states.get(this);
        if (!state) return init.apply(this, args);
        state.searchContext = args; state.inAccessoryInit = true;
        try { return init.apply(this, args); } finally { state.inAccessoryInit = false; }
    };
    for (const name of ["ReplaceAccessory", "ReplaceMainWeapon", "ReplaceShaChiWeapon", "replaceMedicine", "RemoveItem"]) {
        const original = proto[name]; proto[name] = function (...args) {
            const state = states.get(this);
            if (state && (state.job || state.view.opened || state.view.focused())) return;
            return original.apply(this, args);
        };
    }
    const back = proto.navigateBack;
    proto.navigateBack = function (...args) {
        const state = states.get(this);
        if (state?.view.opened) { close(state); return; }
        if (state?.query) { state.view.search.SetText(state.view.text("")); return; }
        return back.apply(this, args);
    };
    const key = proto.OnPreviewKeyDown;
    proto.OnPreviewKeyDown = function (geometry, event) {
        const state = states.get(this);
        if (state?.view.opened) {
            const name = String(UE.KismetInputLibrary.GetKey(event).KeyName);
            if (name === "Escape" || name === "Gamepad_FaceButton_Right") {
                if (state.filter) state.view.presetSearch.SetText(state.view.text("")); else close(state);
                return UE.WidgetBlueprintLibrary.Handled();
            }
            // Let Slate route text/IME and ordinary button navigation to the
            // focused child without invoking the underlying equipment page.
            return UE.WidgetBlueprintLibrary.Unhandled();
        }
        return key ? key.call(this, geometry, event) : UE.WidgetBlueprintLibrary.Unhandled();
    };
    Object.defineProperty(proto, "__loadouts", { value: true });
}
module.exports = { install };
