using UnrealBuildTool;
public class ArenaAssetBuilder : ModuleRules
{
    public ArenaAssetBuilder(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[] {
            "Core", "CoreUObject", "Engine", "UMG", "Slate", "SlateCore",
            "UnrealEd", "UMGEditor", "Kismet", "BlueprintGraph", "KismetCompiler",
            "AssetRegistry", "ProjectPBZ", "CharacterBasics", "GameplayAbilities", "GameplayTags"
        });
    }
}
