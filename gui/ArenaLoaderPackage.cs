namespace PhantomBladeModManager;

internal static class ArenaLoaderPackage
{
    // Retired filenames, retained only for validating and parking old installations.
    public static readonly string[] Paths =
    [
        "dwmapi.dll", "ue4ss/UE4SS.dll", "ue4ss/UE4SS-settings.ini",
        "ue4ss/MemberVariableLayout.ini", "ue4ss/UE4SS_Signatures/GNatives.lua",
        "ue4ss/UE4SS_Signatures/GUObjectArray.lua", "ue4ss/LICENSE"
    ];

    public static Task<List<(string Source, string Target)>> ReadAsync(string modRoot, string buildId) =>
        throw new ManagerException("arena_legacy_retired", new());
}
