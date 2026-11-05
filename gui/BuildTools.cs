using Microsoft.Win32;

namespace PhantomBladeModManager;

internal static class BuildTools
{
    public static string CMake()
    {
        var configured = Environment.GetEnvironmentVariable("PBZ_CMAKE_EXE");
        if (!string.IsNullOrWhiteSpace(configured)) return FileSafety.ValidatePath(configured);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var executable = Path.Combine(directory.Trim('"'), "cmake.exe");
            if (File.Exists(executable)) return FileSafety.ValidatePath(executable);
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CMake", "bin", "cmake.exe");
        if (File.Exists(installed)) return FileSafety.ValidatePath(installed);
        FileSafety.Fail("build_tool_missing", ("tool", "CMake"), ("variable", "PBZ_CMAKE_EXE"));
        return "";
    }

    public static string UnrealEngine()
    {
        var configured = Environment.GetEnvironmentVariable("PBZ_UE_ROOT");
        using var installed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine\5.6");
        var candidates = !string.IsNullOrWhiteSpace(configured)
            ? new[] { configured }
            : new[] { installed?.GetValue("InstalledDirectory") as string,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "UE_5.6") };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var directory = FileSafety.ValidatePath(candidate);
            if (File.Exists(Path.Combine(directory, "Engine", "Build", "BatchFiles", "Build.bat"))
                && File.Exists(Path.Combine(directory, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe")))
                return directory;
        }
        FileSafety.Fail("build_tool_missing", ("tool", "Unreal Engine 5.6"), ("variable", "PBZ_UE_ROOT"));
        return "";
    }
}
