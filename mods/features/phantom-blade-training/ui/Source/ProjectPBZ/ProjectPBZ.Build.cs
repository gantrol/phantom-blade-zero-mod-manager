using UnrealBuildTool;
public class ProjectPBZ : ModuleRules
{
    public ProjectPBZ(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicIncludePaths.Add(ModuleDirectory);
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "UMG", "CommonUI" });
    }
}
