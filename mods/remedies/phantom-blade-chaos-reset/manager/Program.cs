using System.Text.Json;
using System.Windows;

namespace PhantomBladeQuestManager;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            Environment.ExitCode = Task.Run(() => RunCliAsync(args)).GetAwaiter().GetResult();
            return;
        }
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try { await Locale.LoadAsync(); app.MainWindow = new MainWindow(); app.MainWindow.Show(); }
            catch (Exception error) { MessageBox.Show(error.Message); app.Shutdown(1); }
        };
        app.Run();
    }

    public static async Task<SaveService> CreateServiceAsync(string? game = null)
    {
        game = game == null ? await GameFiles.FindAsync() : GameFiles.Allowed(game);
        var rules = JsonSerializer.Deserialize<QuestRule[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "rules.json")), JsonOptions.Pretty)!;
        return new(game, await GameFiles.ReadKeyAsync(game), new(rules));
    }

    // Writes are explicit; inspect and export never replace the source save.
    static async Task<int> RunCliAsync(string[] args)
    {
        Dictionary<string, string> options = [];
        for (int i = 0; i + 1 < args.Length; i += 2) options.Add(args[i], args[i + 1]);
        string report = GameFiles.Allowed(options["--report"]);
        try
        {
            var service = await CreateServiceAsync(options.GetValueOrDefault("--game"));
            var info = await service.InspectAsync(options["--save"]);
            if (options.TryGetValue("--fingerprint", out var expected))
                SaveContainer.Require(info.Fingerprint == expected, "changedSave");
            bool write = options.GetValueOrDefault("--write") == "true";
            string? complete = options.GetValueOrDefault("--complete");
            SaveContainer.Require(!write || !options.ContainsKey("--output"), "invalidOptions");
            SaveContainer.Require(complete == null || complete == "cultDaughter" && !options.ContainsKey("--quests"), "invalidOptions");
            object result = info;
            if (options.TryGetValue("--output", out var output) || write)
                result = await service.CreateManualAsync(info.Path, info.Fingerprint, options["--slot"],
                    complete != null ? [complete] : options.GetValueOrDefault("--quests", "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                    output, completeCultDaughter: complete != null);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, JsonOptions.Pretty));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { error = error.Message, type = error.GetType().Name }, JsonOptions.Pretty));
            return 1;
        }
    }
}
