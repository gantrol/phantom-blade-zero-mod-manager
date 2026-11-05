#pragma once
#include "CommonUserWidget.h"
#include "PBZWidgetCookTypes.generated.h"

// Cooker-only parent types. The cooked widget resolves these paths to the game's
// actual native classes. No module from this authoring project is distributed.
UCLASS(Blueprintable)
class PROJECTPBZ_API UPBZCommonUserWidget : public UCommonUserWidget
{
    GENERATED_BODY()
};

UCLASS(Blueprintable)
class PROJECTPBZ_API UPBZSystemWidgetBase : public UPBZCommonUserWidget
{
    GENERATED_BODY()
public:
    UFUNCTION(BlueprintImplementableEvent) void BP_FirstInitWidget();
    UFUNCTION(BlueprintImplementableEvent) void BP_InitWidget();
    UFUNCTION(BlueprintNativeEvent) void BackInput();
    virtual void BackInput_Implementation() {}
};
