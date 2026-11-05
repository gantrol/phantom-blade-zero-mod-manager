using UnrealBuildTool;
public class MapDataTools : ModuleRules
{
    public MapDataTools(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "Json", "UnrealEd" });
    }
}
