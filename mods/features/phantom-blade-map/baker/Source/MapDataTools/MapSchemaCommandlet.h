#pragma once
#include "Commandlets/Commandlet.h"
#include "MapSchemaCommandlet.generated.h"

UCLASS()
class UMapSchemaCommandlet : public UCommandlet
{
    GENERATED_BODY()
public:
    UMapSchemaCommandlet();
    virtual int32 Main(const FString& Params) override;
};
