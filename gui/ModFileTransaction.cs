using System.Text.Json;

namespace PhantomBladeModManager;

// One journal covers the payload, shared loader and manager state for an operation.
// Entries use paths relative to their owner so a moved manager can recover its journal.
internal sealed class ModFileTransaction(string root, string game, bool dryRun)
{
    private sealed record Entry(string Owner, string Path, string? Backup);
    private readonly List<Entry> entries = [];
    private readonly string directory = DirectoryFor(root, game);
    private string DirectoryOwner => FileSafety.IsSubpath(game, directory) ? game : root;
    private string Journal => Path.Combine(directory, "journal.json");

    private static string DirectoryFor(string root, string game)
    {
        var legacy = Path.Combine(root, ".local", "updates", FileSafety.StringHash(game.ToLowerInvariant())[..16]);
        FileSafety.AssertRealParentChain(Path.Combine(legacy, "journal.json"), root);
        return File.Exists(Path.Combine(legacy, "journal.json")) ? legacy
            : Path.Combine(ManagerEngine.StateDirectoryFor(game), "updates", "active");
    }
    public bool HasPendingRecovery
    {
        get
        {
            FileSafety.AssertRealParentChain(Journal, DirectoryOwner);
            return FileSafety.GetNodeInfoIfExists(Journal) != null;
        }
    }

    private string Resolve(Entry entry)
    {
        var owner = entry.Owner == "game" ? game : root;
        var target = FileSafety.AllowedPath(Path.Combine(owner, entry.Path), new[] { owner });
        FileSafety.AssertRealParentChain(target, owner);
        return target;
    }

    public async Task RecoverAsync()
    {
        if (dryRun || !HasPendingRecovery) return;
        entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(await File.ReadAllTextAsync(Journal), FileSafety.JsonOptions)!);
        await RollbackAsync();
        entries.Clear();
    }

    public async Task CaptureAsync(IEnumerable<string> targets)
    {
        if (dryRun) return;
        FileSafety.AssertRealParentChain(directory, DirectoryOwner);
        Directory.CreateDirectory(directory);
        var capturedCount = entries.Count;
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var owner = FileSafety.IsSubpath(game, target) ? "game" : "manager";
            var ownerRoot = owner == "game" ? game : root;
            var relative = Path.GetRelativePath(ownerRoot, target);
            if (entries.Any(e => e.Owner == owner && string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new Entry(owner, relative, null);
            var path = Resolve(entry);
            if (FileSafety.GetNodeInfoIfExists(path) is FileInfo)
            {
                var backup = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                await RuntimePackageBuilder.CopyFileAsync(path, Path.Combine(directory, backup));
                entry = entry with { Backup = backup };
            }
            entries.Add(entry);
        }
        if (entries.Count == capturedCount) return;
        var pending = Journal + ".next";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(entries, FileSafety.JsonOptions));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(pending, Journal, true);
                break;
            }
            catch (Exception ex) when (attempt < 5 &&
                (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33))
            {
                // Windows file scanners can briefly hold the journal after a replacement.
                await Task.Delay(20 << attempt);
            }
        }
    }

    public async Task RollbackAsync()
    {
        if (dryRun) return;
        try
        {
            foreach (var entry in entries.AsEnumerable().Reverse())
            {
                var target = Resolve(entry);
                if (entry.Backup == null)
                {
                    if (FileSafety.GetNodeInfoIfExists(target) is FileInfo) File.Delete(target);
                }
                else
                {
                    var backup = FileSafety.AllowedPath(Path.Combine(directory, entry.Backup), new[] { directory });
                    FileSafety.AssertRealParentChain(backup, DirectoryOwner);
                    await RuntimePackageBuilder.CopyFileAsync(backup, target);
                }
            }
            Commit();
        }
        catch (Exception)
        {
            FileSafety.Fail("rollback_failed", ("path", directory));
        }
    }

    public void Commit()
    {
        if (dryRun) return;
        // The journal is the commit marker. Leftover backups are safe to clean next time.
        File.Delete(Journal);
        try { FileSafety.RemoveTreeIfExists(directory, new[] { Path.GetDirectoryName(directory)! }); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
