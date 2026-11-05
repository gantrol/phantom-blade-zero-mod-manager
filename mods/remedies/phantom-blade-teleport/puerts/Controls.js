"use strict";
const definitions = require("./Bindings.generated");
const digits = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"];

function keyName(value) {
    return /^\d$/.test(value) ? digits[Number(value)] : value;
}

function read() {
    const UE = require("ue"), puerts = require("puerts");
    const values = Object.fromEntries(definitions.map(d => [d.id, d.key]));
    values["tap.buy"] = "true";
    values["tap.upgrade"] = "true";
    const root = String(UE.KismetSystemLibrary.GetPlatformUserDir()).replace(/\\/g, "/").replace(/\/+$/, "");
    const path = root + "/PbzMods/Config/PhantomBladeControls/New_CSV.csv";
    if (!UE.BlueprintPathsLibrary.FileExists(path)) return values;
    const out = puerts.$ref();
    if (!UE.EasyCsv.MakeCsvInfoStructFromFile(path, out)) return values;
    const info = puerts.$unref(out);
    for (let i = 0; i < info.CSV_Keys.Num(); i++) {
        const key = info.CSV_Keys.Get(i), id = String(key).toLowerCase(), row = info.CSV_Map.Get(key);
        if (id in values && row?.StringValues.Num() === 1) {
            values[id] = keyName(String(row.StringValues.Get(0)));
        }
    }
    return values;
}

module.exports = { read };
