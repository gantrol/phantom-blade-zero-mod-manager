using UnrealBuildTool;
using System.Collections.Generic;
public class ProjectPBZEditorTarget : TargetRules
{
    public ProjectPBZEditorTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        DefaultBuildSettings = BuildSettingsVersion.V5;
        IncludeOrderVersion = EngineIncludeOrderVersion.Unreal5_6;
        ExtraModuleNames.Add("MapDataTools");
    }
}
