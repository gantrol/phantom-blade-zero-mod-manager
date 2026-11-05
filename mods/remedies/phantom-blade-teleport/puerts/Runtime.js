"use strict";
const UE = require("ue");
const puerts = require("puerts");
const cpp = require("cpp");
const TsHelper = require("../Library/TsHelper").default;
const UIHelper = require("../Library/UIHelper").default;
const Controls = require("./Controls");

const TARGET = {
    map: "PBZ_Zhengdaodaying_WP",
    x: -71819.05,
    y: 74968.25,
    z: 708.89,
    pitch: 0.0,
    yaw: 12.15,
    roll: 0.0,
    nearDistance: 420.0,
};

const TICK_INTERVAL_MS = 50;
let cachedReturnPoint = null;
let cachedValues = null;
let lastControlsCheckMs = 0;
const keyCache = new Map();
const wasKeyDown = new Map();

function isNativeObjectAlive(obj) {
    if (!obj) return false;
    try {
        return UE.KismetSystemLibrary.IsValid(obj);
    } catch (error) {
        if (error?.message === "passing a invalid object") return false;
        return false;
    }
}

function getKey(name) {
    let key = keyCache.get(name);
    if (!key) {
        key = new UE.Key();
        key.KeyName = name;
        keyCache.set(name, key);
    }
    return key;
}

function isKeyDown(pc, name) {
    if (!name || name === "None") return false;
    try {
        return !!pc.IsInputKeyDown(getKey(name));
    } catch (_) {
        return false;
    }
}

function isAltOnlyDown(pc) {
    const alt = isKeyDown(pc, "LeftAlt") || isKeyDown(pc, "RightAlt");
    const ctrl = isKeyDown(pc, "LeftControl") || isKeyDown(pc, "RightControl");
    const shift = isKeyDown(pc, "LeftShift") || isKeyDown(pc, "RightShift");
    return alt && !ctrl && !shift;
}

function checkAltJustPressed(pc, id, keyName) {
    if (!keyName || keyName === "None") {
        wasKeyDown.set(id, false);
        return false;
    }
    const down = isAltOnlyDown(pc) && isKeyDown(pc, keyName);
    const prev = !!wasKeyDown.get(id);
    wasKeyDown.set(id, down);
    return down && !prev;
}

function returnPointCsvDir() {
    const root = String(UE.KismetSystemLibrary.GetPlatformUserDir()).replace(/\\/g, "/").replace(/\/+$/, "");
    return root + "/PbzMods/Config/PhantomBladeTeleport";
}

function saveReturnPoint(point) {
    cachedReturnPoint = point;
    try {
        const csv = [
            "---,value",
            `map,"${point.map}"`,
            `x,"${point.x}"`,
            `y,"${point.y}"`,
            `z,"${point.z}"`,
            `pitch,"${point.pitch}"`,
            `yaw,"${point.yaw}"`,
            `roll,"${point.roll}"`,
            "",
        ].join("\r\n");
        UE.EasyCsv.SaveStringToFileWithFullPath(csv, returnPointCsvDir(), "return-point", true);
    } catch (_) {
    }
}

function loadReturnPoint() {
    if (cachedReturnPoint) return cachedReturnPoint;
    try {
        const file = returnPointCsvDir() + "/return-point.csv";
        if (!UE.BlueprintPathsLibrary.FileExists(file)) return null;
        const out = puerts.$ref();
        if (!UE.EasyCsv.MakeCsvInfoStructFromFile(file, out)) return null;
        const info = puerts.$unref(out);
        const raw = {};
        for (let i = 0; i < info.CSV_Keys.Num(); i++) {
            const key = info.CSV_Keys.Get(i);
            const row = info.CSV_Map.Get(key);
            if (row && row.StringValues.Num() >= 1) {
                raw[String(key)] = String(row.StringValues.Get(0));
            }
        }
        const x = Number(raw.x), y = Number(raw.y), z = Number(raw.z);
        const pitch = Number(raw.pitch || 0), yaw = Number(raw.yaw || 0), roll = Number(raw.roll || 0);
        if (!raw.map || !Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) return null;
        cachedReturnPoint = { map: raw.map, x, y, z, pitch, yaw, roll };
        return cachedReturnPoint;
    } catch (_) {
        return null;
    }
}

function isNearTarget(mapName, loc) {
    if (String(mapName) !== TARGET.map) return false;
    const dx = loc.X - TARGET.x, dy = loc.Y - TARGET.y, dz = loc.Z - TARGET.z;
    return (dx * dx + dy * dy + dz * dz) <= (TARGET.nearDistance * TARGET.nearDistance);
}

function canTeleport(context, character) {
    if (!isNativeObjectAlive(character)) return false;
    const seqMgr = TsHelper.getWorldSubsystem(context, UE.PBZLevelSequenceManager.StaticClass());
    if (seqMgr && seqMgr.IsInSequence()) return false;
    const asc = UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(character);
    if (!asc) return false;
    if (asc.HasMatchingGameplayTag(TsHelper.constructGameplayTag("CharacterState.InCombat"))) {
        try { UIHelper.showTipsText(context, cpp.FText.FromString("正道大营传送：战斗中无法传送")); } catch (_) {}
        return false;
    }
    if (asc.HasMatchingGameplayTag(TsHelper.constructGameplayTag("CharacterState.ShipNavigation"))) {
        const shipSystem = TsHelper.getWorldSubsystem(context, UE.ShipNavigationSystem.StaticClass());
        if (shipSystem && typeof shipSystem.ForceEndShipNavigation === "function") {
            shipSystem.ForceEndShipNavigation(character);
        }
    }
    return true;
}

function executeTeleport(context, mapName, x, y, z, pitch, yaw, roll) {
    const perfSub = TsHelper.getWorldSubsystem(context, UE.PerformanceSystemSubsystem.StaticClass());
    if (!perfSub || typeof perfSub.TeleportToTargetPoint !== "function") return false;
    const transform = UE.KismetMathLibrary.MakeTransform(
        new UE.Vector(x, y, z),
        new UE.Rotator(pitch, yaw, roll),
        new UE.Vector(1, 1, 1)
    );
    perfSub.TeleportToTargetPoint(mapName, transform, false, true);
    return true;
}

function refreshControlsIfNeeded() {
    const now = Date.now();
    if (!cachedValues || now - lastControlsCheckMs > 3000) {
        lastControlsCheckMs = now;
        try {
            cachedValues = Controls.read();
        } catch (_) {
            if (!cachedValues) cachedValues = { "teleport.go": "F7", "teleport.return": "F8" };
        }
    }
    return cachedValues;
}

function stepTeleport(gameMode) {
    if (!isNativeObjectAlive(gameMode) || UE.GameplayStatics.IsGamePaused(gameMode)) return;
    const pc = UE.GameplayStatics.GetPlayerController(gameMode, 0);
    if (!isNativeObjectAlive(pc)) return;
    const values = refreshControlsIfNeeded();
    const goPressed = checkAltJustPressed(pc, "teleport.go", values["teleport.go"]);
    const backPressed = checkAltJustPressed(pc, "teleport.return", values["teleport.return"]);
    if (!goPressed && !backPressed) return;

    const character = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(gameMode);
    if (!canTeleport(gameMode, character)) return;

    if (goPressed) {
        const currentMap = String(UE.GameplayStatics.GetCurrentLevelName(gameMode, true) || "");
        const loc = character.K2_GetActorLocation();
        const rot = pc.GetControlRotation ? pc.GetControlRotation() : character.K2_GetActorRotation();
        if (currentMap && loc && !isNearTarget(currentMap, loc)) {
            saveReturnPoint({
                map: currentMap,
                x: Number(loc.X),
                y: Number(loc.Y),
                z: Number(loc.Z),
                pitch: Number(rot?.Pitch || 0),
                yaw: Number(rot?.Yaw || 0),
                roll: Number(rot?.Roll || 0),
            });
        }
        if (executeTeleport(gameMode, TARGET.map, TARGET.x, TARGET.y, TARGET.z, TARGET.pitch, TARGET.yaw, TARGET.roll)) {
            console.log("[PhantomBladeTeleport] teleported to lakeside chest");
        }
    } else if (backPressed) {
        const point = loadReturnPoint();
        if (!point) {
            try { UIHelper.showTipsText(gameMode, cpp.FText.FromString("正道大营传送：尚未记录返回点")); } catch (_) {}
            return;
        }
        if (executeTeleport(gameMode, point.map, point.x, point.y, point.z, point.pitch, point.yaw, point.roll)) {
            console.log(`[PhantomBladeTeleport] returned to ${point.map}`);
        }
    }
}

function stopTimer(gameMode) {
    if (gameMode && gameMode.__pbzTeleportTimer) {
        clearInterval(gameMode.__pbzTeleportTimer);
        gameMode.__pbzTeleportTimer = 0;
    }
}

function install(InterfaceType) {
    const proto = InterfaceType && InterfaceType.prototype;
    if (!proto || proto.__pbzTeleportInstalled) return;
    Object.defineProperty(proto, "__pbzTeleportInstalled", { value: true });

    const originalBind = proto.bindSaveTipExtension;
    if (typeof originalBind === "function") {
        proto.bindSaveTipExtension = function () {
            const result = originalBind.apply(this, arguments);
            stopTimer(this);
            cachedValues = null;
            this.__pbzTeleportTimer = setInterval(() => {
                if (!isNativeObjectAlive(this)) {
                    stopTimer(this);
                    return;
                }
                try {
                    stepTeleport(this);
                } catch (_) {
                }
            }, TICK_INTERVAL_MS);
            return result;
        };
    }

    const originalEndPlay = proto.ReceiveEndPlay;
    if (typeof originalEndPlay === "function") {
        proto.ReceiveEndPlay = function () {
            stopTimer(this);
            return originalEndPlay.apply(this, arguments);
        };
    }
}

module.exports = { install };
