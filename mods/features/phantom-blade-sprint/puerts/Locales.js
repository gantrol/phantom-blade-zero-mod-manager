"use strict";
const UE = require("ue");
const messages = {
    en: { enabled: "Auto sprint: on", disabled: "Auto sprint: off" },
    zh: { enabled: "自动疾跑：已开启", disabled: "自动疾跑：已关闭" }
};
function text(key) {
    const language = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).split(/[-_]/)[0];
    return (messages[language] || messages.en)[key];
}
module.exports = { text };
