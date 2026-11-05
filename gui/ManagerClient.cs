using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal sealed class ManagerClient(string root)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private Dictionary<string, string> messages = [];
    public string Locale { get; private set; } = "zh-CN";
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Locale);

    public string Text(string key, params (string Key, string Value)[] parameters)
    {
        var result = messages.GetValueOrDefault(key, key);
        foreach (var (name, value) in parameters) result = result.Replace("{" + name + "}", value);
        return result;
    }

    public async Task InitializeAsync()
    {
        var locale = "zh-CN";
        try
        {
            var settingsPath = Path.Combine(root, "settings.local.json");
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(
                    await File.ReadAllTextAsync(settingsPath, Encoding.UTF8), JsonOptions);
                locale = settings?.Locale ?? locale;
            }
        }
        catch { }
        Locale = locale is "en" or "zh-CN" ? locale : "en";
        messages = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(root, "locales", "en.json"), Encoding.UTF8))!;
        if (Locale != "en")
        {
            var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(root, "locales", Locale + ".json"), Encoding.UTF8))!;
            foreach (var (key, value) in translated) messages[key] = value;
        }
    }

    public async Task<Snapshot> StatusAsync()
    {
        return await Task.Run(async () =>
        {
            var raw = await ExecuteCheckedAsync("status");
            var game = (string)raw["game"]!;
            var gameBuildId = raw.TryGetValue("gameBuildId", out var gbObj) ? gbObj as string : null;
            var running = (bool)raw["running"]!;
            var mods = (List<Dictionary<string, object?>>)raw["mods"]!;
            var catalog = ManagerEngine.LoadCatalog(root);
            var backups = SaveBackupService.ListSaveBackups(game);
            var plugins = new List<Plugin>();

            foreach (var item in mods)
            {
                var id = (string)item["id"]!;
                var category = item.TryGetValue("category", out var catObj) && catObj is string c ? c : "feature";
                var status = (string)item["status"]!;
                var retired = status == "retired";
                var installedVersion = item.GetValueOrDefault("installedVersion") as string;
                var sourceVersion = item.GetValueOrDefault("sourceVersion") as string;
                var sourceAvailable = item.TryGetValue("sourceAvailable", out var saObj) && saObj is true;
                var configurable = item.TryGetValue("configurable", out var cfgObj) && cfgObj is true;
                var enabledFlag = item.TryGetValue("enabled", out var enObj) && enObj is true;
                var recordedFlag = item.TryGetValue("recorded", out var recObj) && recObj is true;

                if (!sourceAvailable && status == "not_installed" && !retired)
                    continue;

                var incompatibleBuild = status == "incompatible_build";
                var loadState = status switch
                {
                    "enabled" or "installed_unrecorded" => "enabled",
                    "disabled" or "disabled_unrecorded" => "disabled",
                    "outdated" => enabledFlag ? "enabled" : "disabled",
                    "incompatible_build" => enabledFlag ? "enabled" : (recordedFlag ? "disabled" : "absent"),
                    "not_installed" or "retired" => "absent",
                    _ => "conflict",
                };

                string? sourceState = status switch
                {
                    "retired" => null,
                    "enabled" or "disabled" => sourceAvailable ? "same" : "unknown",
                    "outdated" or "installed_unrecorded" or "disabled_unrecorded" => "different",
                    "incompatible_build" => "unsupported_build",
                    "modified" => "modified",
                    "not_installed" => sourceAvailable ? "absent" : "unknown",
                    _ => "different",
                };

                var modDef = catalog.FirstOrDefault(m => m.Id == id);
                var projectDir = modDef != null && !retired ? ManagerEngine.ResolveModProjectRoot(root, modDef) : null;
                var configFlag = configurable || id is "map" or "quickActions" or "sprint" or "teleport" ? id : null;

                var modBackups = backups
                    .Where(b => b.TryGetValue("modId", out var mId) && string.Equals(mId as string, id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                DateTimeOffset? latestBackup = null;
                if (modBackups.Count > 0 && modBackups[0].TryGetValue("createdAt", out var ca) && ca is string caStr &&
                    DateTimeOffset.TryParse(caStr, out var dto))
                {
                    latestBackup = dto;
                }

                plugins.Add(new Plugin(
                    Id: id,
                    Category: category,
                    SourceVersion: sourceVersion,
                    InstalledVersion: installedVersion,
                    LoadState: loadState,
                    SourceState: sourceState,
                    Directory: projectDir,
                    Config: configFlag,
                    Retired: retired,
                    BackupCount: modBackups.Count,
                    LatestBackup: latestBackup,
                    IncompatibleBuild: incompatibleBuild));
            }

            var logPath = catalog.Any(mod => mod.Backend == "nativeRuntime")
                ? Path.Combine(game, "platform", "steam", "PhantomBladeRuntime", "loader.log")
                : Path.Combine(game, "platform", "steam", "ue4ss", "UE4SS.log");
            return new Snapshot(game, gameBuildId, running, plugins, [], logPath);
        });
    }

    public async Task<string> RunAsync(string action, string? id = null, IReadOnlyDictionary<string, string>? options = null)
    {
        return await Task.Run(async () =>
        {
            var opts = options?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var result = await ExecuteCheckedAsync(action, id, opts);
            return JsonSerializer.Serialize(result, FileSafety.JsonOptions);
        });
    }

    private async Task<Dictionary<string, object?>> ExecuteCheckedAsync(
        string action, string? id = null, Dictionary<string, string>? options = null)
    {
        try
        {
            return await ManagerEngine.ExecuteAsync(root, action, id, options);
        }
        catch (ManagerException mex)
        {
            var msg = Text(mex.Code);
            foreach (var (k, v) in mex.Params)
                msg = msg.Replace("{" + k + "}", Convert.ToString(v) ?? "");
            throw new InvalidOperationException(msg, mex);
        }
    }

    public static Task<bool> GameRunningAsync() => Task.Run(FileSafety.IsGameRunning);

    public async Task SetGameDirectoryAsync(string path) =>
        await Task.Run(() => ExecuteCheckedAsync("set-game", path));

    private sealed record Settings(string? Locale);
}

internal sealed record Snapshot(string Game, string? GameBuildId, bool GameRunning, List<Plugin> Plugins, List<OtherPlugin> OtherMods, string Log);
internal sealed record OtherPlugin(string Id, string LoadState);
internal sealed record Plugin(
    string Id, string Category, string? SourceVersion, string? InstalledVersion, string LoadState, string? SourceState,
    string? Directory, string? Config, bool Retired, int BackupCount, DateTimeOffset? LatestBackup, bool IncompatibleBuild = false);
