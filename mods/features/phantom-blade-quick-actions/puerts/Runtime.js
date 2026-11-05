"use strict";
const Controls = require("./Controls");
const states = new WeakMap();
function ue() { return require("ue"); }
function helper() { return require("../Library/TsHelper").default; }
function backpack(page) { return helper().getWorldSubsystem(page, ue().PBZBackPackSubsystem.StaticClass()); }
function item(page) { const button = page.W_SystemPanel?.ButtonGroup?.GetSelectedButtonBase(); return button ? String(page.getItemFromButton(button)) : ""; }
function tab(page) { return String(page.FirstTab?.GetActiveTab()).toLowerCase() + "/" + String(page.SecondTab?.GetActiveTab?.() || "").toLowerCase(); }
function active(page) { return ue().KismetSystemLibrary.IsValid(page) && page.IsActivated(); }
function upgrading(page) { return !page.isDowngradeState() && !page.isZhuHunState() && !page.activeUpgradeTutorial && !page.isUpgradeTutorialEnterInputLocked(); }
function modal(page) { return ue().PBZBlueprintFunctionLibrary.GetUILayOut(page)?.HasModal(); }
function busy(page) {
    return page.bIsUpgradeSuccessAnimationPlaying || page.bIsInCompleteHold || page.bIsDowngradeExitTransition
        || page.maxUpgradeTransitionActive || page.weaponSwitchAnimationTimer || page.bIsBreakthroughConfirmPending;
}
function record(event, detail) { console.log("[PhantomBladeQuickActions] " + event + " " + JSON.stringify(detail || {})); }
function failed(page, error) { stop(page, "error"); record("error", { message: String(error) }); }
function stop(page, reason) {
    const state = states.get(page);
    if (!state) return;
    if (state.pending) clearTimeout(state.pending);
    state.pending = undefined;
    if (state.job) record("batch-finished", { reason, completed: state.job.completed });
    state.job = undefined;
}
function refreshUpgradeQuote(page) {
    // Equipment-sync can change another item's level between batch steps. Refresh
    // this item's native state and cost cache immediately before the native purchase.
    const UE = ue(), bag = backpack(page), logic = page.UIGameplayLogicComponent;
    const id = item(page), kind = tab(page).split("/")[0], dust = helper().getcurrentDust(page);
    if (!id || !bag || !logic) return false;
    if (kind === "weapon") logic.CheckMainWeaponState(id, bag.MainWeaponInfos.Get(id), bag.GetMainWeaponTableData(id), bag, dust);
    else if (kind === "shachiweapon") logic.CheckShaChiWeaponState(id, bag.ShaqiWeaponInfos.Get(id), bag.GetShaqiWeaponTableData(id), bag, dust);
    else if (kind === "accessory") logic.CheckAccessoryState(id, bag.Accessories.Get(id), UE.PBZBackPackSubsystem.GetAccessoryData(id), bag, dust);
    else if (kind === "medicine") logic.CheckMedicineState(id, bag.MedicineInfos.Get(id), UE.PBZBackPackSubsystem.GetMedicineData(id), bag, dust);
    else return false;
    return page.isCurrentItemUpgradable();
}
function upgradeOnce(page) {
    if (!active(page) || !upgrading(page) || busy(page) || modal(page) || !refreshUpgradeQuote(page)) return false;
    const state = states.get(page);
    state.performingUpgrade = true;
    page.bIsUserHoldingButton = true;
    try { page.continuousUpgradeFn(); }
    finally {
        state.performingUpgrade = false;
        // resetHoldState also closes breakthrough dialogs; leave an active native confirmation intact.
        if (page.bIsBreakthroughConfirmPending) page.interruptHoldForConfirmation();
        else page.resetHoldState(true);
    }
    return true;
}
function purchasePlan(page, bag, requested) {
    const UE = ue(), id = item(page), shop = helper().getWorldSubsystem(page, UE.ShopSystem.StaticClass());
    if (!id || !bag || !shop || !page.isBuyTabActive()) return;
    let price, materials, limit = Number(page.getMaxNumForActiveTab());
    if (shop.IsCycleLimitedItem(id)) price = Number(shop.GetCycleItemPrice(id));
    else if (shop.IsCycleLimitedAccessory(id)) { price = Number(shop.GetCycleAccessoryPrice(id)); limit = 1; }
    else if (shop.IsNgPlusBuyShop()) price = Number(shop.GetNgPlusBuyPrice(id));
    else {
        const data = UE.ShopSystem.GetShopTableData(id);
        if (!data) return;
        price = Number(data.ConsumeDust);
        materials = [];
        const consumeMap = data.ConsumeMaterials;
        if (consumeMap) {
            for (let i = 0, n = consumeMap.GetMaxIndex(); i < n; i++) {
                if (!consumeMap.IsValidIndex(i)) continue;
                const key = consumeMap.GetKey(i);
                materials.push([String(key), Number(consumeMap.Get(key))]);
            }
        }
        if (data.SellType !== UE.ESellType.Item) limit = Math.min(limit, 1);
    }
    const dust = Number(helper().getcurrentDust(page));
    if (!Number.isFinite(price) || price < 0 || !Number.isFinite(limit) || limit < 1) return;
    limit = Math.min(Math.floor(limit), 99999, requested ?? 99999, price > 0 ? Math.floor(dust / price) : 99999);
    if (materials?.some(([, count]) => !Number.isInteger(count) || count < 0)) return;
    const affordable = count => {
        const needed = UE.NewMap(UE.BuiltinName, UE.BuiltinInt);
        for (const [key, amount] of materials || []) if (amount) needed.Add(key, amount * count);
        return bag.HasEnoughItems(needed);
    };
    let low = 0, high = limit;
    while (low < high) { const middle = Math.ceil((low + high) / 2); if (affordable(middle)) low = middle; else high = middle - 1; }
    return low > 0 ? { id, count: low, dust } : undefined;
}
function buyOnce(page, maximum) {
    if (!active(page) || !page.isBuyTabActive() || modal(page)) return false;
    const bag = backpack(page), desc = page.W_UnifiedTextDescription;
    const requested = maximum ? undefined : Number(desc.getcurrentShopEntryNum());
    if (!maximum && (!Number.isInteger(requested) || requested < 1)) return false;
    const plan = purchasePlan(page, bag, requested);
    if (!plan || (!maximum && plan.count !== requested)) return false;
    if (!page.isShopTradeEnabled()) page.enterShopSecond();
    if (maximum) desc.changeShopEntryNum(plan.count, plan.dust, page.getLiveBackpackNum());
    if (!page.isCurrentItemAffordable()) return false;
    page.boundShopLongPressButton?.HoldReset();
    page.W_SystemPanel.ButtonGroup.GetSelectedButtonBase()?.HoldReset();
    page.stopShopCharge();
    page.playBuyFinishSound();
    page.executeBuyLogic(plan.id, bag);
    return true;
}
function deferTap(page, kind) {
    const state = states.get(page);
    if (!state || state.job || state.pending || state.values["tap." + kind] !== "true") return;
    if (kind === "upgrade" && (!upgrading(page) || !page.bIsUserHoldingButton)) return;
    if (kind === "buy" && (!page.isBuyTabActive() || !page.isShopTradeEnabled() || !page.isShopChargeActive)) return;
    const selected = item(page), currentTab = tab(page);
    state.pending = setTimeout(() => {
        state.pending = undefined;
        if (!active(page) || item(page) !== selected || tab(page) !== currentTab || modal(page)) return;
        try { if (kind === "upgrade") upgradeOnce(page); else buyOnce(page, false); }
        catch (error) { failed(page, error); }
    }, 0);
}
function level(page, id) {
    const bag = backpack(page), kind = tab(page).split("/")[0];
    if (kind === "weapon" || kind === "shachiweapon") return Number(bag.GetWeaponLevel(id, kind === "weapon"));
    const info = kind === "accessory" ? bag.Accessories.Get(id) : bag.MedicineInfos.Get(id);
    return Number(info?.CurrentLevel);
}
function begin(page, kind) {
    const state = states.get(page);
    if (!state) return false;
    if (state.job) { stop(page, "cancelled"); return true; }
    if (modal(page) || (kind === "upgrade" ? !upgrading(page) || busy(page) : !page.isBuyTabActive())) return false;
    const group = page.W_SystemPanel?.ButtonGroup;
    if (!group) return false;
    const ids = [];
    for (let i = 0; i < group.GetButtonCount(); i++) {
        const id = String(page.getItemFromButton(group.GetButtonBaseAtIndex(i)) || "");
        if (id && id !== "None" && !ids.includes(id)) ids.push(id);
    }
    stop(page, "restart");
    state.job = { kind, ids, index: 0, completed: 0, tab: tab(page), next: 0, stage: "select" };
    record("batch-started", { kind, count: ids.length });
    return true;
}
function step(page) {
    const state = states.get(page), job = state?.job;
    if (!job || Date.now() < job.next) return;
    if (!active(page) || tab(page) !== job.tab || (job.kind === "upgrade" && !upgrading(page))) { stop(page, "page-changed"); return; }
    if (modal(page) || (job.kind === "upgrade" && busy(page)) || (job.kind === "buy" && page.shopListRebuildTimer)) return;
    if (job.index >= job.ids.length) { stop(page, "complete"); return; }
    job.next = Date.now() + 180;
    const target = job.ids[job.index], group = page.W_SystemPanel.ButtonGroup;
    if (job.stage === "select") {
        let index = -1;
        for (let i = 0; i < group.GetButtonCount(); i++) if (String(page.getItemFromButton(group.GetButtonBaseAtIndex(i))) === target) { index = i; break; }
        if (index < 0) { job.index++; return; }
        group.SelectButtonAtIndex(index);
        job.stage = "execute";
        return;
    }
    if (item(page) !== target) { stop(page, "selection-changed"); return; }
    if (job.kind === "buy") {
        if (buyOnce(page, true)) job.completed++;
        job.index++; job.stage = "select"; return;
    }
    if (job.stage === "settle") {
        if (level(page, target) <= job.before) { job.index++; job.stage = "select"; return; }
        job.completed++;
    }
    job.before = level(page, target);
    if (!Number.isFinite(job.before) || !upgradeOnce(page)) { job.index++; job.stage = "select"; return; }
    job.stage = "settle";
}
function after(proto, name, callback) {
    const original = proto[name];
    if (typeof original !== "function") throw new Error("QUICK_ACTIONS_INTERFACE_MISMATCH: " + name);
    proto[name] = function (...args) {
        const result = original.apply(this, args);
        try { callback(this, ...args); } catch (error) { failed(this, error); }
        return result;
    };
}
function install(Type, kind) {
    const proto = Type.prototype;
    if (Object.prototype.hasOwnProperty.call(proto, "__quickActions")) return;
    Object.defineProperty(proto, "__quickActions", { value: true });
    after(proto, "BP_OnActivated", page => { stop(page, "reopened"); states.delete(page); states.set(page, { values: Controls.read() }); });
    after(proto, "BP_OnDeactivated", page => { stop(page, "closed"); states.delete(page); });
    after(proto, "Destruct", page => { stop(page, "destroyed"); states.delete(page); });
    after(proto, "Tick", step);
    const back = proto.HandleOverrideBackInputAction;
    proto.HandleOverrideBackInputAction = function (...args) { stop(this, "back"); return back?.apply(this, args); };
    const originalKey = proto.OnPreviewKeyDown;
    proto.OnPreviewKeyDown = function (geometry, event) {
        try {
            const state = states.get(this);
            const binding = state && Controls.action(kind === "buy" ? "shop" : "upgrade", event, state.values);
            if (binding) {
                // Native key repeat must not toggle a newly started batch off again.
                if (ue().KismetInputLibrary.InputEvent_IsRepeat(event) || begin(this, kind)) return ue().WidgetBlueprintLibrary.Handled();
            }
        } catch (error) { failed(this, error); }
        return originalKey ? originalKey.call(this, geometry, event) : ue().WidgetBlueprintLibrary.Unhandled();
    };
    if (kind === "buy") after(proto, "startShopCharge", page => deferTap(page, "buy"));
    else {
        after(proto, "startPhysicalChargeVisuals", page => deferTap(page, "upgrade"));
        const confirm = proto.showBreakthroughConfirm;
        proto.showBreakthroughConfirm = function (id, isMain, bag, confirmed, cancelled, targetLevel) {
            if (!states.get(this)?.performingUpgrade) return confirm.call(this, id, isMain, bag, confirmed, cancelled, targetLevel);
            const before = level(this, id), currentTab = tab(this);
            const guarded = () => {
                if (!active(this) || tab(this) !== currentTab || item(this) !== String(id) || level(this, id) !== before || !refreshUpgradeQuote(this)) {
                    cancelled(); return;
                }
                confirmed();
            };
            return confirm.call(this, id, isMain, bag, guarded, cancelled, targetLevel);
        };
    }
}
module.exports = { install };
