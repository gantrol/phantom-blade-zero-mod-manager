"use strict";
const UE = require("ue");
const { toOneShotDelegate } = require("puerts");

function load(owner, paths, complete) {
    const references = UE.NewArray(UE.SoftObjectPath);
    for (const path of paths) references.Add(UE.KismetSystemLibrary.MakeSoftObjectPath(path));
    return UE.PBZAssetManager.AsyncLoadObjects(owner, references, toOneShotDelegate(loaded => {
        const byPath = new Map();
        if (loaded) for (let index = 0; index < loaded.Num(); index++) {
            const asset = loaded.Get(index);
            if (asset && UE.KismetSystemLibrary.IsValid(asset))
                byPath.set(String(UE.KismetSystemLibrary.GetPathName(asset)).toLowerCase(), asset);
        }
        complete(paths.map(path => byPath.get(path.toLowerCase())));
    }));
}

function cancel(handle) {
    if (handle) UE.PBZAssetManager.CancelLoad(handle);
}

module.exports = { load, cancel };
