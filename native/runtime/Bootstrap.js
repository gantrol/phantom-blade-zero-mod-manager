"use strict";
(() => {
    const enabled = require("PhantomBladeRuntime/Enabled");
    const installers = {
        arena() {
            require("PhantomBladeArena/Runtime").install();
        },
        damageMonitor() {
            require("PhantomBladeDamageMonitor/Runtime").install(require("UI/Widget/Hud/PlayerHpBar").playerHPBarMixinInterface);
        },
        killingIntent() {
            const meters = require("PhantomBladeKillingIntent/Runtime");
            meters.install(require("UI/Widget/Hud/PlayerHpBar").playerHPBarMixinInterface);
        },
        equipment() {
            require("PhantomBladeEquipmentSync/Runtime").install(require("Gameplay/UIGameplayLogicComponent").uiGameplayLogicMixinInterface);
        },
        teleport() {
            require("PhantomBladeTeleport/Runtime").install(require("Gameplay/GameModeBuild").GameModeBuildMixinInterface);
        },
        documentRecovery() {
            require("PhantomBladeDocumentRecovery/Runtime").install(require("UI/Widget/Literature").LiteratureWidgetInteraface);
        },
        memoryFlowers() {
            require("PhantomBladeMemoryFlowers/Runtime").install();
        },
        sprint() {
            require("PhantomBladeSprint/Runtime").install();
        },
        story() {
            require("PhantomBladeStorySkip/Runtime").install();
        },
        targeting() {
            require("PhantomBladeTargeting/Runtime").install();
        },
        loadouts() {
            require("PhantomBladeLoadouts/Runtime").install();
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
