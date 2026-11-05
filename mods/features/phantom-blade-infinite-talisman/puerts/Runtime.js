"use strict";

function install() {
    const hooks = require("./Hooks.generated");
    const map = require("UI/Widget/Map").MapInterface.prototype;
    const upgrade = require("UI/Widget/UpgradeSystemWidget_V1").UpgradeSystemWidgetMixin_V1_Interface.prototype;
    if (Object.prototype.hasOwnProperty.call(map, "__pbzInfiniteTalismanInstalled")) return;
    const replacements = [
        [map, hooks.MapHooks.prototype, ["runTeleportGates", "confirmAndConsumeCharm"]],
        [upgrade, hooks.UpgradeHooks.prototype, ["executeDowngrade"]],
    ];
    for (const [target, , names] of replacements)
        for (const name of names)
            if (typeof target[name] !== "function") throw new Error("TALISMAN_INTERFACE_MISMATCH: " + name);
    for (const [target, source, names] of replacements)
        for (const name of names) target[name] = source[name];
    Object.defineProperty(map, "__pbzInfiniteTalismanInstalled", { value: true });
}

module.exports = { install };
