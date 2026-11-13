"use strict";
const {aliases}=require("./OfflineAtlas");
const world=id=>aliases[String(id).toLowerCase()] || String(id).toLowerCase();

// Read the same discovered lock registry as the native map. Only copied values
// leave this call; the HUD never retains a map widget or a native lock object.
function read(context,mapIDs,UE,text) {
    const map=UE.SubsystemBlueprintLibrary.GetWorldSubsystem(context,UE.MapSubsystem.StaticClass());
    if(!map)return [];
    const currentWorld=world(map.GetCurrentMapID());
    const ids=mapIDs || Array.from(map.LockObjectInfos,([id])=>String(id)).filter(id=>world(id)===currentWorld);
    const locks=[];
    for(const mapID of ids) {
        const group=map.LockObjectInfos.GetRef(mapID)?.MapLockGroups.GetRef(0);
        if(!group)continue;
        for(let i=0,n=group.LockObjArray.Num();i<n;i++) {
            const lock=group.LockObjArray.Get(i);
            if(lock)locks.push({mapID,lock});
        }
    }
    const points=[];
    for(const {mapID,lock} of locks) {
        if(!UE.KismetSystemLibrary.IsValid(lock))continue;
        const kind=lock.IsA(UE.DoorDirLockObject.StaticClass())?"door":
            lock.IsA(UE.SwitchLockObject.StaticClass())?"item":
            lock.IsA(UE.ShaChiWeaponLockObject.StaticClass())?"weapon":undefined;
        if(!kind)continue;
        const guid=String(lock.GuidString),position=lock.ActorLocation;
        const x=position.X,y=position.Y,z=position.Z,unlocked=!!lock.bUnLocked;
        if(!guid || guid==="None" || ![x,y,z].every(Number.isFinite))continue;
        points.push({id:"native-lock:"+String(mapID).toLowerCase()+":"+guid.toLowerCase(),
            mapID:String(mapID),x,y,z,layer:-1,lockGuid:guid,lockKind:kind,nativeLock:true,landmark:true,
            category:"mapLock",pointKind:"nativeLock",icon:kind+"Lock",state:unlocked?"collected":"remaining",
            label:text[{door:"nativeDoorLock",item:"nativeItemLock",weapon:"nativeWeaponLock"}[kind]],
            stage:text[unlocked?"nativeLockOpen":"nativeLockBlocked"]});
    }
    return points;
}
module.exports={read};
