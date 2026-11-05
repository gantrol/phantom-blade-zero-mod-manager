using System.Runtime.InteropServices;
using System.Text;

namespace PhantomBladeModManager;

internal static class Program
{
    private static readonly HashSet<string> CliCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "record", "backups", "controls", "map-settings", "config",
        "install", "on", "off", "restore", "restore-save", "set-game", "build", "help", "--help", "-h"
    };

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        var root = ResolveRoot(args, out var cliArgs, out var isCli);
        if (isCli)
        {
            if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
                AttachConsole(-1);
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = new UTF8Encoding(false);
            }
            catch { }
            return ManagerEngine.RunCliAsync(root, cliArgs).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ManagerForm(root));
        return 0;
    }

    private static string ResolveRoot(string[] args, out string[] cliArgs, out bool isCli)
    {
        var defaultRoot = FindCatalogRoot(AppContext.BaseDirectory)
            ?? FindCatalogRoot(Environment.CurrentDirectory)
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

        if (args.Length == 0)
        {
            cliArgs = Array.Empty<string>();
            isCli = false;
            return defaultRoot;
        }

        if (Directory.Exists(args[0]) && File.Exists(Path.Combine(args[0], "catalog.json")))
        {
            var root = Path.GetFullPath(args[0]);
            cliArgs = args.Skip(1).ToArray();
            isCli = cliArgs.Length > 0;
            return root;
        }

        if (CliCommands.Contains(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            cliArgs = args;
            isCli = true;
            return defaultRoot;
        }

        cliArgs = Array.Empty<string>();
        isCli = false;
        return Path.GetFullPath(args[0]);
    }

    private static string? FindCatalogRoot(string startDir)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "catalog.json")))
                return dir.FullName;
        }
        return null;
    }
}
