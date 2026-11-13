"use strict";
(() => {
    const enabled = require("PhantomBladeRuntime/Enabled");
    const installers = {
        sprint() {
            require("PhantomBladeSprint/Runtime").install();
        },
        targeting() {
            require("PhantomBladeTargeting/Runtime").install();
        },
        map() {
            const map = require("UI/Widget/Map").MapInterface;
            require("PhantomBladeMap/Entry").install(map);
        },
        talisman() {
            require("PhantomBladeInfiniteTalisman/Runtime").install();
        },
        quickActions() {
            const actions = require("PhantomBladeQuickActions/Runtime");
            actions.install(require("UI/Widget/ShopSystemWidget").shopSystemMixinInterface, "buy");
            actions.install(require("UI/Widget/UpgradeSystemWidget_V1").UpgradeSystemWidgetMixin_V1_Interface, "upgrade");
        }
    };
    for (const id of enabled) {
        try {
            installers[id]();
            console.log("[PhantomBladeRuntime] installed=" + id);
        } catch (error) {
            console.error("[PhantomBladeRuntime] failed=" + id + " " + String(error?.stack || error));
        }
    }
})();
