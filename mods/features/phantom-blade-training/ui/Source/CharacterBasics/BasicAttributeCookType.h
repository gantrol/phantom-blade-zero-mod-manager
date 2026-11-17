#pragma once
#include "AttributeSet.h"
#include "BasicAttributeCookType.generated.h"
// Cooker-only field identity. The shipped asset resolves the game's own class.
UCLASS()
class CHARACTERBASICS_API UBasicAttributeSet : public UAttributeSet
{
    GENERATED_BODY()
public:
    UPROPERTY() FGameplayAttributeData Health;
    UPROPERTY() FGameplayAttributeData Health_Max;
    UPROPERTY() FGameplayAttributeData Attack;
    UPROPERTY() FGameplayAttributeData ShachiAttack;
};
