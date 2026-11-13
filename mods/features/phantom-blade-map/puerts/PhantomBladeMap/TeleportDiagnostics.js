"use strict";

// UI_Map_33 is shared by the movement and checkpoint gates. Capture their
// inputs at the rejected request; a saved slot cannot reveal the live mode.
function record(page) {
    const UE = require("ue"), helper = require("../Library/TsHelper").default;
    const character = UE.PBZBlueprintFunctionLibrary.GetPBZLocalCharacter(page);
    const movement = character?.CharacterMovement;
    const movementMode = movement ? Number(movement.MovementMode) : undefined;
    const customMovementMode = movement ? Number(movement.CustomMovementMode) : undefined;
    const asc = character && UE.AbilitySystemBlueprintLibrary.GetAbilitySystemComponent(character);
    const shipNavigation = !!asc?.HasMatchingGameplayTag(helper.constructGameplayTag("CharacterState.ShipNavigation"));
    const inAir = !!asc?.HasMatchingGameplayTag(helper.constructGameplayTag("CharacterState.Space.Air"));
    const bell = helper.getWorldSubsystem(page, UE.BellManager.StaticClass());
    const checkpointOnly = bell ? !!bell.IsSavedFromTriggerBoxOnly() : undefined;
    const activeBells = bell ? bell.GetAllActivateBellIDs().Num() : undefined;
    const modes = [
        ["none", UE.EMovementMode.MOVE_None],
        ["walking", UE.EMovementMode.MOVE_Walking],
        ["navWalking", UE.EMovementMode.MOVE_NavWalking],
        ["falling", UE.EMovementMode.MOVE_Falling],
        ["swimming", UE.EMovementMode.MOVE_Swimming],
        ["flying", UE.EMovementMode.MOVE_Flying],
        ["custom", UE.EMovementMode.MOVE_Custom]
    ];
    // Match the shipped gate order without changing movement or save state.
    const movementBlocked = movementMode !== UE.EMovementMode.MOVE_Walking && !shipNavigation;
    require("./Diagnostics").record("teleport-blocked", {
        textKey: "UI_Map_33",
        reason: movementBlocked ? "movement" : checkpointOnly ? "checkpointOnly" : "unknown",
        movementMode, movementName: modes.find(([, value]) => value === movementMode)?.[0] || "unknown",
        customMovementMode, shipNavigation, inAir, checkpointOnly, activeBells,
        paused: !!UE.GameplayStatics.IsGamePaused(page),
        mapID: String(page.CurrentDisplayMapID || ""),
        character: character ? String(UE.KismetSystemLibrary.GetPathName(character)) : ""
    });
}

module.exports = { record };
