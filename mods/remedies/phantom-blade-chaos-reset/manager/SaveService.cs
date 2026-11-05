using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhantomBladeQuestManager;

sealed class SaveService(string game, byte[] key, QuestPatcher patcher)
{
    public string Game { get; } = game;

    public async Task<SaveInfo> InspectAsync(string path)
    {
        path = GameFiles.Allowed(path);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        var container = SaveContainer.Parse(bytes, key);
        List<SlotInfo> slots = [];
        foreach (var block in container.Blocks)
        {
            if (!Regex.IsMatch(block.Name, "^(AutoSave[0-9]+|ManualSave[1-9][0-9]*)$")) continue;
            var slot = container.ReadSlot(block.Name);
            var system = slot["PBZGameSystem"];
            slots.Add(new(block.Name, system?["DateTimeString"]?.GetValue<string>() ?? "",
                system?["CurrentLevelName"]?.GetValue<string>() ?? "", system?["HasGameTime"]?.GetValue<int>() ?? 0, patcher.Inspect(slot)));
        }
        return new(path, GameFiles.Hash(bytes), slots.OrderByDescending(s => s.Date, StringComparer.Ordinal).ToArray());
    }

    public async Task<ManualSaveResult> CreateManualAsync(string path, string fingerprint, string slotName, string[] ids, string? output = null, bool completeCultDaughter = false)
    {
        path = GameFiles.Allowed(path);
        if (output == null) GameFiles.RequireClosed();
        byte[] original = await File.ReadAllBytesAsync(path);
        SaveContainer.Require(GameFiles.Hash(original) == fingerprint, "changedSave");
        var container = SaveContainer.Parse(original, key);
        SaveContainer.Require(Regex.IsMatch(slotName, "^(AutoSave[0-9]+|ManualSave[1-9][0-9]*)$")
            && container.Blocks.Any(b => b.Name == slotName), "slotMissing");
        string destination = container.NextManualSlot();
        JsonObject source = container.ReadSlot(slotName);
        SaveContainer.Require(source["PBZGameSystem"]?["NightmareSlotIndex"]?.GetValue<int>() < 0, "nightmare");
        SaveContainer.Require(!completeCultDaughter || ids.SequenceEqual(new[] { "cultDaughter" }), "noSelection");
        var plan = completeCultDaughter ? patcher.CompleteCultDaughter(source)
            : ids.Length > 0 ? patcher.Prepare(source, ids) : (Slot: source, Changes: Array.Empty<Change>());
        byte[] encoded = container.AddManualSlot(destination, plan.Slot);
        string? backup = null;
        if (output != null)
        {
            output = GameFiles.Allowed(output);
            SaveContainer.Require(!string.Equals(output, path, StringComparison.OrdinalIgnoreCase), "outputIsSource");
            await using var export = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await export.WriteAsync(encoded);
        }
        else
        {
            await GameFiles.DisableLegacyModAsync(Game);
            backup = await CommitAsync(path, original, encoded);
        }
        byte[] stored = await File.ReadAllBytesAsync(output ?? path);
        SaveContainer.Require(stored.AsSpan().SequenceEqual(encoded), "verification");
        var reread = SaveContainer.Parse(stored, key);
        SaveContainer.Require(JsonNode.DeepEquals(plan.Slot, reread.ReadSlot(destination)), "verification");
        return new(slotName, destination, ids, backup, plan.Changes, GameFiles.Hash(stored));
    }

    public async Task<string> RestoreAsync(string path, string fingerprint)
    {
        path = GameFiles.Allowed(path);
        GameFiles.RequireClosed();
        string? backup = LatestBackup(path);
        SaveContainer.Require(backup != null, "noBackup");
        byte[] original = await File.ReadAllBytesAsync(path);
        SaveContainer.Require(GameFiles.Hash(original) == fingerprint, "changedSave");
        byte[] restored = await File.ReadAllBytesAsync(backup!);
        _ = SaveContainer.Parse(restored, key);
        await CommitAsync(path, original, restored);
        SaveContainer.Require((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(restored), "verification");
        return backup!;
    }

    public static string BackupDirectory(string path) => GameFiles.Allowed(Path.Combine(Path.GetDirectoryName(path)!, ".quest-manager-backups", Path.GetFileName(path)));
    public static string? LatestBackup(string path)
    {
        string folder = BackupDirectory(path);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.sav").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
    }

    static async Task<string> CommitAsync(string path, byte[] original, byte[] result)
    {
        GameFiles.RequireClosed();
        string folder = BackupDirectory(path);
        Directory.CreateDirectory(folder);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(folder, stamp + ".sav");
        string temp = path + ".quest-manager-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(result);
                await stream.FlushAsync();
            }
            SaveContainer.Require((await File.ReadAllBytesAsync(temp)).AsSpan().SequenceEqual(result), "verification");
            GameFiles.RequireClosed();
            SaveContainer.Require((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(original), "changedSave");
            // Atomic Windows replacement, on the worker; the original file becomes the backup.
            File.Replace(temp, path, backup);
            SaveContainer.Require((await File.ReadAllBytesAsync(backup)).AsSpan().SequenceEqual(original), "verification");
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
