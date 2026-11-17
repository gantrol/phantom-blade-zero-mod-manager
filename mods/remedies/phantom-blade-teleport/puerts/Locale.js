"use strict";
const UE = require("ue");
const locales = require("./Locales.generated");

function text(key) {
    const language = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).toLowerCase().startsWith("zh") ? "zh-CN" : "en";
    return locales[language][key] || locales.en[key] || key;
}

module.exports = { text };
