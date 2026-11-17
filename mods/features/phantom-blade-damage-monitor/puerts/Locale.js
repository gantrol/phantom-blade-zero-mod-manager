"use strict";
const UE = require("ue"), locales = require("./Locales.generated");
function t(key, values = {}) {
    const language = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).toLowerCase().startsWith("zh") ? "zh-CN" : "en";
    let text = locales[language][key] || locales.en[key] || key;
    for (const [name, value] of Object.entries(values)) text = text.split("{" + name + "}").join(String(value));
    return text;
}
module.exports = { t };
