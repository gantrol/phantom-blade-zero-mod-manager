"use strict";
const UE = require("ue");
const messages = {
    en: { teleportConfirm: "Travel to the selected bell?" },
    zh: { teleportConfirm: "传送至选中的铃铛？" },
};
function text(key) {
    const language = String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).split(/[-_]/)[0];
    return (messages[language] || messages.en)[key];
}
module.exports = { text };
