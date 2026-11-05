#pragma once
#include "Commandlets/Commandlet.h"
#include "ArenaAssetsCommandlet.generated.h"

UCLASS()
class UArenaAssetsCommandlet : public UCommandlet
{
    GENERATED_BODY()
public:
    UArenaAssetsCommandlet();
    virtual int32 Main(const FString& Params) override;
};
