"use strict";
// Observed in build 26266538: KismetSystemLibrary.GetPlatformUserDir returns
// the user's Documents directory. Do not infer it from an account name or drive.
function resolveUserRoot(UE) {
    const method = UE.KismetSystemLibrary?.GetPlatformUserDir;
    if (typeof method !== "function" || typeof UE.BlueprintPathsLibrary?.FileExists !== "function") throw new Error("USER_DIRECTORY_OR_FILE_EXISTS_NOT_EXPOSED");
    const value = UE.KismetSystemLibrary.GetPlatformUserDir();
    if (typeof value !== "string" || !/^[A-Za-z]:[\\/]/.test(value) || /[\r\n]|(^|[\\/])\.\.([\\/]|$)/.test(value) || /[\\/]SaveGames([\\/]|$)/i.test(value)) throw new Error("INVALID_USER_DIRECTORY");
    return value.replace(/\\/g, "/").replace(/\/+$/, "") + "/PbzMods/Config";
}
module.exports = { resolveUserRoot };
