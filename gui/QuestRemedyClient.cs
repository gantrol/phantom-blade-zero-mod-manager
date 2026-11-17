using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomBladeModManager;

internal sealed record RemedyQuest(string Id, bool Eligible, string Status);
internal sealed record RemedySlot(string Name, string Date, RemedyQuest[] Quests);
internal sealed record RemedySave(string Path, string Fingerprint, RemedySlot[] Slots);
internal sealed record RemedyResult(string Slot, string? Backup);

internal sealed class QuestRemedyClient(string root, ManagerClient client)
{
    private readonly string package = Path.Combine(root, "mods/remedies/phantom-blade-chaos-reset/dist/PhantomBladeQuestManager-current");
    private Dictionary<string, string> messages = [];
    public bool Available => File.Exists(Path.Combine(package, "PhantomBladeQuestManager.exe"));

    public async Task InitializeAsync()
    {
        messages = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(package, "locales", client.Locale + ".json")))!;
    }

    public string Text(string key, params object[] args) => string.Format(client.Culture, messages.GetValueOrDefault(key, key), args);
    public string SlotLabel(string name) => name.StartsWith("NightmareRun", StringComparison.Ordinal)
        ? Text("nightmareSlot", int.Parse(name[12..], CultureInfo.InvariantCulture) + 1)
        : name.StartsWith("ManualSave", StringComparison.Ordinal) ? Text("manualSlot", name[10..]) : Text("autoSlot", name[8..]);

    public Task<string[]> FindSavesAsync(string game) => Task.Run(() =>
    {
        string folder = Path.Combine(game, "ProjectPBZ/Saved/SaveGames/Steam");
        return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), "^[0-9]+$"))
            .Select(path => Path.Combine(path, "PBZSaveGame.sav")).Where(File.Exists).ToArray() : [];
    });

    public Task<RemedySave> InspectAsync(string game, string path) => RunAsync<RemedySave>(game, path, []);
    public Task<RemedyResult> RepairAsync(string game, RemedySave save, string slot) => RunAsync<RemedyResult>(game, save.Path,
        ["--slot", slot, "--quests", "guiTu", "--fingerprint", save.Fingerprint, "--write", "true"]);

    private async Task<T> RunAsync<T>(string game, string path, string[] options)
    {
        string reports = Path.Combine(root, ".local/quest-remedy");
        Directory.CreateDirectory(reports);
        string report = Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json");
        var result = await FileSafety.RunProcessBytesAsync(Path.Combine(package, "PhantomBladeQuestManager.exe"),
            new[] { "--game", game, "--save", path, "--report", report }.Concat(options));
        if (!File.Exists(report)) throw new InvalidOperationException(client.Text("gui_questToolFailed"));
        string json = await File.ReadAllTextAsync(report);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(Text(error.GetString() ?? "verification"));
        if (result.ExitCode != 0) throw new InvalidOperationException(client.Text("gui_questToolFailed"));
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException(client.Text("gui_questToolFailed"));
    }
}
