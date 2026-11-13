"use strict";

// Match the shipped map's getplayerTransform: the character remains attached
// to the vessel, while ArrowComponent supplies the vessel's actual heading.
// All engine references are borrowed for this call and are never cached here.
function read(context, UE, helper) {
    const valid = object => !!object && UE.KismetSystemLibrary.IsValid(object);
    if (!valid(context)) return;
    const pawn = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(context)
        || UE.GameplayStatics.GetPlayerCharacter(context, 0);
    if (!valid(pawn) || pawn.IsActorBeingDestroyed()) return;
    const location = pawn.K2_GetActorLocation();
    const capsule=pawn.CapsuleComponent;
    const halfHeight=valid(capsule) && typeof capsule.GetScaledCapsuleHalfHeight==="function" ? capsule.GetScaledCapsuleHalfHeight() : 0;
    let yaw = pawn.K2_GetActorRotation().Yaw;
    const ship = helper.getWorldSubsystem(context, UE.ShipNavigationSystem.StaticClass());
    const aboard = valid(ship) && ship.IsInNavigation();
    const currentBoat = aboard ? ship.GetCurrentBoat() : undefined;
    const boat = valid(currentBoat) && !currentBoat.IsActorBeingDestroyed() ? currentBoat : undefined;
    const arrow = boat?.ArrowComponent;
    if (valid(arrow)) yaw = arrow.K2_GetComponentRotation().Yaw;
    return { pawn, boat, aboard, x: location.X, y: location.Y, z: location.Z, groundZ:location.Z-halfHeight, yaw };
}

module.exports = { read };
