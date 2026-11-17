"use strict";
const UE = require("ue");
const Helper = require("../Library/TsHelper").default;
const locales = require("./Locales.generated");
const states = new WeakMap();
const capability = "PhantomBladeArena.SkillEditing";
const text = value => UE.KismetTextLibrary.Conv_StringToText(value);
const id = value => String(value || "");
function t(key) {
    const lang = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).startsWith("zh") ? "zh-Hans" : "en";
    return locales[lang][key];
}
function context(page) {
    const player = UE.GameplayStatics.GetPlayerCharacter(page, 0);
    if (!player?.ActorHasTag(capability)) return null;
    const component = UE.PBZBlueprintFunctionLibrary.GetPBZAbilityNodesComponent(page);
    const backpack = Helper.getWorldSubsystem(page, UE.PBZBackPackSubsystem.StaticClass());
    const manager = Helper.getWorldSubsystem(page, UE.PBZAbilityNodesManager.StaticClass());
    const asc = UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(player);
    return component && backpack && manager && asc ? { player, component, backpack, manager, asc } : null;
}
function accessories(backpack) {
    return Array.from({ length: 6 }, (_, slot) => [slot, backpack.AssemblyAccessory.IsValidIndex(slot) ? id(backpack.AssemblyAccessory.Get(slot)) : ""]);
}
function snapshot(page, ctx, materials) {
    return JSON.stringify({
        levels: Array.from(ctx.component.AbilityNodeMap, ([key, node]) => [id(key), Number(node.Level)]).sort(),
        dust: Number(Helper.getcurrentDust(page)),
        materials: materials.map(([key]) => [key, Number(ctx.backpack.GetUsePropAmount(key))]),
        accessories: accessories(ctx.backpack)
    });
}
function current(state) {
    const ctx = context(state.page);
    return ctx && ctx.player === state.ctx.player && ctx.component === state.ctx.component && ctx.backpack === state.ctx.backpack ? ctx : null;
}
function usable(state) {
    const page = state.page;
    return current(state) && !state.job && page.IsActivated() && page.GetIsEnabled()
        && !page.resetCascadeActive && !(page.tutorialStep >= 0) && !page.IsAnyAnimationPlaying()
        && !UE.PBZBlueprintFunctionLibrary.GetUILayOut(page).HasModal()
        && !page.deactivatedAccessoryData?.length;
}
function message(state, key, detail = "") { state.status.SetText(text(t(key) + detail)); }
function refresh(state) {
    const page = state.page, ctx = current(state);
    if (!ctx) return;
    page.refreshGraphState(); page.refreshAllLevelIcons(); page.checkResetKey(ctx.component);
    const selected = page.getCurrentSelectedNodeID();
    if (selected) {
        const data = UE.PBZAbilityNodesManager.GetAbilityTableNodeData(selected);
        page.updateNodeNameAndDesc(selected, data, ctx.component);
        page.updateDescByCheckCondition(selected, ctx.component, ctx.manager, data);
    }
}
function dependency(state, nodeID) {
    for (const [key, node] of state.ctx.component.AbilityNodeMap) {
        if (Number(node.Level) <= 0 || id(key) === nodeID) continue;
        const data = UE.PBZAbilityNodesManager.GetAbilityTableNodeData(key);
        if (state.page.collectParentNodeIDs(key, data.NodeType, state.ctx.manager, data).map(id).includes(nodeID))
            return UE.KismetTextLibrary.Conv_TextToString(UE.EventPipelineManager.Conv_LocTextToText(data.AbilityNodeName));
    }
    return "";
}
function changeResources(state, record, refund) {
    const ctx = state.ctx;
    if (record.dust) Helper.changeDust(state.page, record.dust, refund);
    if (refund) {
        const map = UE.NewMap(UE.BuiltinName, UE.BuiltinInt);
        for (const [key, count] of record.materials) map.Add(key, count);
        if (map.Num()) ctx.backpack.AddPropsByMap(map, false);
    } else for (const [key, count] of record.materials) ctx.backpack.RemovePropsFromBackPack(count, key, false);
}
function complete(state) {
    const job = state.job;
    if (!job || !current(state)) return;
    if (Number(state.ctx.component.AbilityNodeMap.Get(job.id)?.Level) !== job.target) throw new Error("ARENA_SKILL_LEVEL_MISMATCH");
    // Keep occupants of slots retained by the new rank. Undo also restores
    // occupants of capacity slots after their native effect has returned.
    for (const [slot, item] of job.accessories) {
        if (item && item !== "None" && slot < state.ctx.backpack.GetCanAssembleAccessoryNum()
            && id(state.ctx.backpack.AssemblyAccessory.Get(slot)) !== item)
            state.ctx.backpack.AssembleAccessory(item, slot);
    }
    if (state.page.deactivatedAccessoryData)
        state.page.deactivatedAccessoryData = state.page.deactivatedAccessoryData.filter(
            ([slot, item]) => id(state.ctx.backpack.AssemblyAccessory.Get(slot)) !== id(item));
    if (job.undoing) {
        state.undo = null;
    } else {
        changeResources(state, job, true);
        job.signature = snapshot(state.page, state.ctx, job.materials);
        state.undo = job;
    }
    state.job = null;
    refresh(state);
    // Use the game's own equipment-capacity notification and post-reset UI.
    if (state.page.deactivatedAccessoryData?.length) state.page.OnDeactivateAllNodesLambda();
    message(state, job.undoing ? "skillRestored" : "skillRefunded");
    if (!state.page.IsActivated()) dispose(state);
}
function applyLevel(state, record, undoing) {
    const ctx = state.ctx, node = ctx.component.AbilityNodeMap.Get(record.id);
    state.job = { ...record, undoing, target: undoing ? record.before : record.before - 1 };
    state.undo = null;
    if (undoing) changeResources(state, record, false);
    // Native DeactivateNode removes the actual granted effects and resets all
    // ranks. Initialization mode reapplies the retained rank in one operation.
    ctx.component.DeactivateNode(record.id, ctx.asc);
    if (!state.job.target) complete(state);
    else {
        node.Level = state.job.target;
        ctx.component.ActivateNode(record.id, ctx.asc, true, false);
    }
}
function refund(state, nodeID) {
    if (!usable(state) || !nodeID) return;
    const node = state.ctx.component.AbilityNodeMap.Get(nodeID), before = Number(node?.Level || 0);
    if (!before) return;
    const blocked = before === 1 ? dependency(state, nodeID) : "";
    if (blocked) { message(state, "skillDependency", blocked); return; }
    const data = UE.PBZAbilityNodesManager.GetAbilityTableNodeData(nodeID);
    const materials = Array.from(data.NeedMaterials, ([key, count]) => [id(key), Number(count)]);
    const dust = Number(node.GetConsumeDust(before));
    if (!Number.isSafeInteger(dust) || dust < 0 || Helper.getcurrentDust(state.page) + dust > 2000000000
        || materials.some(([key, count]) => !Number.isSafeInteger(count) || count < 0 || state.ctx.backpack.GetUsePropAmount(key) + count > 2000000000)) {
        message(state, "skillRefundLimit"); return;
    }
    const equipped = accessories(state.ctx.backpack);
    applyLevel(state, { id: nodeID, before, dust, materials, accessories: equipped }, false);
}
function undo(state) {
    if (!usable(state) || !state.undo) return;
    const record = state.undo;
    if (snapshot(state.page, state.ctx, record.materials) !== record.signature) {
        state.undo = null; message(state, "skillUndoExpired"); return;
    }
    applyLevel(state, record, true);
}
function create(state) {
    const page = state.page;
    // Append to the original upgrade controls, inheriting their layout/scale.
    const parent = page.W_LongPressButton.GetParent().GetParent();
    if (!parent?.AddChildToVerticalBox) throw new Error("ARENA_SKILL_LAYOUT_MISMATCH");
    const body = UE.NewObject(UE.VerticalBox.StaticClass(), page); parent.AddChildToVerticalBox(body);
    state.body = body;
    const label = (parent, value) => {
        const widget = UE.NewObject(UE.TextBlock.StaticClass(), page); parent.AddChild(widget);
        const donor = page.NodeName.DefaultTextStyleOverride, font = widget.Font;
        font.FontObject = donor.Font.FontObject; font.TypefaceFontName = donor.Font.TypefaceFontName; font.Size = 16;
        widget.SetFont(font); widget.SetText(text(value)); widget.SetAutoWrapText(true); return widget;
    };
    label(body, t("skillEditingHint"));
    const row = UE.NewObject(UE.HorizontalBox.StaticClass(), page); body.AddChild(row);
    const button = (key, callback) => {
        const widget = UE.NewObject(UE.Button.StaticClass(), page); row.AddChild(widget);
        widget.SetBackgroundColor(new UE.LinearColor(.20, .14, .105, 1));
        label(widget, t(key)); widget.OnClicked.Add(callback); state.bindings.push([widget.OnClicked, callback]);
        return widget;
    };
    state.refund = button("skillRefundOne", () => safe(state, () => refund(state, id(page.getCurrentSelectedNodeID()))));
    state.undoButton = button("skillUndo", () => safe(state, () => undo(state)));
    state.status = label(body, "");
    const activated = nodeID => {
        if (state.job && id(nodeID) === state.job.id) safe(state, () => complete(state));
    };
    state.ctx.component.OnActivateNode.Add(activated); state.bindings.push([state.ctx.component.OnActivateNode, activated]);
}
function dispose(state) {
    for (const [delegate, callback] of state.bindings) delegate.Remove(callback);
    state.bindings.length = 0; state.body?.RemoveFromParent(); states.delete(state.page);
}
function safe(state, action) {
    try { action(); }
    catch (error) {
        console.error("[PhantomBladeArena] skills=" + String(error?.stack || error));
        // Do not issue a second refund or blindly replay effects after failure.
        state.undo = null; state.failed = true; state.job = null;
        if (state.status) message(state, "skillEditFailed");
    }
}
function tick(state) {
    if (!current(state)) { dispose(state); return; }
    const allowed = !state.failed && usable(state);
    const selected = state.page.getCurrentSelectedNodeID();
    state.refund.SetIsEnabled(!!allowed && !!selected && Number(state.ctx.component.AbilityNodeMap.Get(selected)?.Level) > 0);
    if (state.undo && Date.now() >= (state.nextRefresh || 0)) {
        state.nextRefresh = Date.now() + 250;
        if (snapshot(state.page, state.ctx, state.undo.materials) !== state.undo.signature) state.undo = null;
    }
    state.undoButton.SetIsEnabled(!!allowed && !!state.undo);
    if (!allowed) return;
    const pc = state.page.GetOwningPlayer();
    const pressed = name => { const key = new UE.Key(); key.KeyName = name; return pc.WasInputKeyJustPressed(key); };
    const held = name => { const key = new UE.Key(); key.KeyName = name; return pc.IsInputKeyDown(key); };
    if (pressed("RightMouseButton")) {
        for (const [nodeID, widget] of state.page.NodeMap) if (widget.IsHovered()) { refund(state, id(nodeID)); break; }
    }
    if (pressed("Z") && (held("LeftControl") || held("RightControl"))) undo(state);
}
exports.install = function (type) {
    const proto = type.prototype;
    const originalTick = proto.Tick;
    proto.Tick = function (...args) {
        const result = originalTick.apply(this, args);
        let state = states.get(this);
        if (!state && this.IsActivated()) {
            const ctx = context(this);
            if (ctx) {
                state = { page: this, ctx, bindings: [], job: null, undo: null };
                states.set(this, state); safe(state, () => create(state));
            }
        }
        if (state?.body && state.status) safe(state, () => tick(state));
        return result;
    };
    for (const name of ["executeActivateNode", "resetAllActivatedNodes"]) {
        const original = proto[name];
        proto[name] = function (...args) {
            const state = states.get(this);
            if (state?.job) return name === "resetAllActivatedNodes"
                ? { totalDustRefunded: 0, materialsRefunded: UE.NewMap(UE.BuiltinName, UE.BuiltinInt), nodesReset: 0 } : undefined;
            if (state) state.undo = null;
            return original.apply(this, args);
        };
    }
    const deactivate = proto.BP_OnDeactivated;
    proto.BP_OnDeactivated = function (...args) {
        const state = states.get(this);
        // A completing native activation still owns its callback until it
        // finishes. Inactive pages must not retain native delegate references.
        if (state && !state.job) dispose(state);
        return deactivate.apply(this, args);
    };
};
