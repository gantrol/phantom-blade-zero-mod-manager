"use strict";
const UE = require("ue"), cpp = require("puerts");
const Helper = require("../Library/TsHelper").default;
const locales = require("./Locales.generated");
const valid = object => !!object && UE.KismetSystemLibrary.IsValid(object);
const context = () => UE.PBZBlueprintFunctionLibrary.GetGameWorldContext();
const player = () => UE.GameplayStatics.GetPlayerCharacter(context(), 0);
const instance = () => UE.GameplayStatics.GetGameInstance(context());
const saves = () => Helper.getGameInstanceSubsystem(context(), UE.PBZSaveLoadSubsystem.StaticClass());
const system = () => Helper.getGameSystem(context());
const bag = () => Helper.getBackPackSubsystem(context());
const copy = value => JSON.parse(JSON.stringify(value));
const text = value => UE.KismetTextLibrary.Conv_StringToText(String(value));
const language = () => String(UE.KismetInternationalizationLibrary.GetCurrentLanguage()).startsWith("zh") ? "zh-Hans" : "en";
const t = key => locales[language()][key] || key;
function local(value) {
    const out = cpp.$ref(text(value.key));
    UE.KismetTextLibrary.FindTextInLocalizationTable(value.namespace, value.key, out, "");
    return UE.KismetTextLibrary.Conv_TextToString(cpp.$unref(out));
}
function json(value) {
    const out = cpp.$ref("");
    if (!UE.JsonBlueprintFunctionLibrary.ToString(value, out)) throw new Error("arena_json_read_failed");
    return JSON.parse(cpp.$unref(out));
}
function wrapper(value) {
    const out = cpp.$ref(new UE.JsonObjectWrapper());
    if (!UE.JsonBlueprintFunctionLibrary.FromString(JSON.stringify(value), out)) throw new Error("arena_json_write_failed");
    return cpp.$unref(out);
}
function canonical(value) {
    if (Array.isArray(value)) return "[" + value.map(canonical).join(",") + "]";
    if (value && typeof value === "object") return "{" + Object.keys(value).sort().map(k => JSON.stringify(k) + ":" + canonical(value[k])).join(",") + "}";
    return JSON.stringify(value);
}
function transform(value) {
    const p = value.Location, r = value.Rotation;
    return UE.KismetMathLibrary.MakeTransform(new UE.Vector(p.X, p.Y, p.Z), new UE.Rotator(r.Pitch, r.Yaw, r.Roll), new UE.Vector(1, 1, 1));
}
function trace(start, end, ignore) {
    const hit = cpp.$ref(new UE.HitResult()), actors = UE.NewArray(UE.Actor);
    for (const actor of ignore || []) if (valid(actor)) actors.Add(actor);
    const found = UE.KismetSystemLibrary.LineTraceSingle(context(), start, end, UE.ETraceTypeQuery.TraceTypeQuery1,
        false, actors, UE.EDrawDebugTrace.None, hit, true, new UE.LinearColor(), new UE.LinearColor(), 0);
    return found ? cpp.$unref(hit) : null;
}
function actors(type) {
    const out = cpp.$ref(UE.NewArray(UE.Actor));
    UE.GameplayStatics.GetAllActorsOfClass(context(), type, out);
    return Array.from(cpp.$unref(out));
}
function report(error) { console.error("[PhantomBladeArena] " + String(error?.stack || error)); }
module.exports = { UE, cpp, Helper, valid, context, player, instance, saves, system, bag, copy, text, language, t, local, json, wrapper, canonical, transform, trace, actors, report };
