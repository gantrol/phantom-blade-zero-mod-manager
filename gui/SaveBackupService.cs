using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomBladeModManager;

internal static class SaveBackupService
{
    public static string ResolveSaveDirectory(string game, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return FileSafety.ValidatePath(explicitPath, "save directory");
        return FileSafety.ValidatePath(Path.Combine(game, "ProjectPBZ", "Saved", "SaveGames", "Steam"), "save directory");
    }

    public static string BackupRootFor(string game) =>
        Path.Combine(FileSafety.ValidatePath(game, "game directory"), "platform", "steam", ".pbz-mod-manager", "save-backups");

    public static Dictionary<string, object?> CreateSaveBackup(string game, string modId, bool dryRun = false, string? saveDirectory = null)
    {
        var sourceDir = ResolveSaveDirectory(game, saveDirectory);
        var info = FileSafety.GetNodeInfoIfExists(sourceDir, "save directory");
        if (info is not DirectoryInfo srcDirInfo)
        {
            FileSafety.Fail("save_not_found");
            return null!;
        }
        FileSafety.AssertRealTree(sourceDir, game, "save directory");

        var accounts = srcDirInfo.GetDirectories()
            .Where(d => Regex.IsMatch(d.Name, @"^\d+$"))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var acc in accounts)
        {
            var tree = FileSafety.TreeDigest(acc.FullName);
            foreach (var f in tree.Files)
                files[$"{acc.Name}/{f.Path}"] = f.Sha256;
        }
        if (!files.Keys.Any(name => name.EndsWith("/PBZSaveGame.sav", StringComparison.OrdinalIgnoreCase)))
            FileSafety.Fail("save_not_found");

        var created = DateTime.UtcNow.ToString("O");
        var stamp = Regex.Replace(created, "[:.]", "-");
        var safeModId = Regex.Replace(modId ?? "mod", "[^a-zA-Z0-9_-]", "-");
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var backupRoot = BackupRootFor(game);
        var targetDir = Path.Combine(backupRoot, $"{safeModId}-{stamp}-{shortId}");
        var payloadDir = Path.Combine(targetDir, "SaveGames");
        var backupJsonPath = Path.Combine(targetDir, "backup.json");

        if (dryRun)
        {
            return new Dictionary<string, object?>
            {
                ["created"] = false,
                ["dryRun"] = true,
                ["mod"] = modId,
                ["source"] = sourceDir,
                ["target"] = targetDir,
                ["fileCount"] = files.Count,
            };
        }

        foreach (var (rel, expectedSha) in files)
        {
            var srcFile = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var dstFile = Path.Combine(payloadDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, true);
            if (FileSafety.FileHash(dstFile) != expectedSha)
            {
                FileSafety.RemoveTreeIfExists(targetDir, new[] { game });
                FileSafety.Fail("backup_verify_failed", ("target", targetDir));
            }
        }

        var metadata = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["mod"] = modId,
            ["created"] = created,
            ["source"] = sourceDir,
            ["files"] = files,
        };
        FileSafety.WriteJsonAtomic(backupJsonPath, metadata, new[] { game });

        return new Dictionary<string, object?>
        {
            ["created"] = true,
            ["mod"] = modId,
            ["createdAt"] = created,
            ["source"] = sourceDir,
            ["target"] = targetDir,
            ["fileCount"] = files.Count,
        };
    }

    public static List<Dictionary<string, object?>> ListSaveBackups(string game)
    {
        var root = BackupRootFor(game);
        var info = FileSafety.GetNodeInfoIfExists(root, "save backup directory");
        if (info == null) return new List<Dictionary<string, object?>>();
        if (info is not DirectoryInfo dirInfo) { FileSafety.Fail("expected_directory", ("target", root)); return new List<Dictionary<string, object?>>(); }
        FileSafety.AssertRealTree(root, game, "save backup directory");
        var entries = dirInfo.GetDirectories().OrderByDescending(e => e.Name, StringComparer.Ordinal).ToList();
        var backups = new List<Dictionary<string, object?>>();
        foreach (var subDir in entries)
        {
            var backupJson = Path.Combine(subDir.FullName, "backup.json");
            var manifestJson = Path.Combine(subDir.FullName, "manifest.json");
            var metaFile = File.Exists(backupJson) ? backupJson : File.Exists(manifestJson) ? manifestJson : null;
            if (metaFile == null) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile));
                var el = doc.RootElement;
                var modId = el.TryGetProperty("mod", out var mProp) ? mProp.GetString()
                    : el.TryGetProperty("modId", out var m2Prop) ? m2Prop.GetString() : null;
                var createdAt = el.TryGetProperty("created", out var cProp) ? cProp.GetString()
                    : el.TryGetProperty("createdAt", out var c2Prop) ? c2Prop.GetString() : null;
                var fileCount = el.TryGetProperty("files", out var fObj) && fObj.ValueKind == JsonValueKind.Object
                    ? fObj.EnumerateObject().Count()
                    : el.TryGetProperty("fileCount", out var fcProp) && fcProp.ValueKind == JsonValueKind.Number ? fcProp.GetInt32() : 0;

                backups.Add(new Dictionary<string, object?>
                {
                    ["name"] = subDir.Name,
                    ["modId"] = modId,
                    ["createdAt"] = createdAt,
                    ["fileCount"] = fileCount,
                    ["target"] = subDir.FullName,
                });
            }
            catch
            {
            }
        }
        return backups;
    }

    public static Dictionary<string, object?> RestoreSaveBackup(
        string game, string? targetModOrName = null, bool dryRun = false, string? saveDirectory = null)
    {
        var root = BackupRootFor(game);
        var info = FileSafety.GetNodeInfoIfExists(root, "save backup directory");
        if (info is not DirectoryInfo dirInfo)
        {
            FileSafety.Fail("no_save_backup", ("id", targetModOrName ?? ""));
            return null!;
        }
        FileSafety.AssertRealTree(root, game, "save backup directory");

        var filter = string.IsNullOrWhiteSpace(targetModOrName) || string.Equals(targetModOrName, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : targetModOrName.Trim();

        DirectoryInfo? chosenDir = null;
        string? chosenModId = null;
        var chosenFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subDir in dirInfo.GetDirectories().OrderByDescending(e => e.Name, StringComparer.Ordinal))
        {
            var backupJson = Path.Combine(subDir.FullName, "backup.json");
            if (!File.Exists(backupJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(backupJson));
                var el = doc.RootElement;
                var modId = el.TryGetProperty("mod", out var mProp) ? mProp.GetString()
                    : el.TryGetProperty("modId", out var m2Prop) ? m2Prop.GetString() : null;
                if (filter == null)
                {
                    if (string.Equals(modId, "pre-restore", StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (!string.Equals(subDir.Name, filter, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(modId, filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!el.TryGetProperty("files", out var fObj) || fObj.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in fObj.EnumerateObject())
                {
                    var sha = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(sha)) chosenFiles[prop.Name] = sha!;
                }
                if (chosenFiles.Count == 0) continue;
                chosenDir = subDir;
                chosenModId = modId ?? filter ?? "save";
                break;
            }
            catch
            {
            }
        }

        if (chosenDir == null)
        {
            FileSafety.Fail("no_save_backup", ("id", targetModOrName ?? ""));
            return null!;
        }

        var payloadDir = Path.Combine(chosenDir.FullName, "SaveGames");
        foreach (var (rel, expectedSha) in chosenFiles)
        {
            var srcFile = FileSafety.AllowedPath(Path.Combine(payloadDir, rel.Replace('/', Path.DirectorySeparatorChar)), new[] { chosenDir.FullName }, rel);
            if (FileSafety.GetNodeInfoIfExists(srcFile, rel) is not FileInfo || FileSafety.FileHash(srcFile) != expectedSha)
                FileSafety.Fail("backup_verify_failed", ("target", chosenDir.FullName));
        }

        var targetSaveDir = ResolveSaveDirectory(game, saveDirectory);
        Dictionary<string, object?>? preRestoreBackup = null;
        if (FileSafety.GetNodeInfoIfExists(targetSaveDir, "save directory") is DirectoryInfo)
        {
            try { preRestoreBackup = CreateSaveBackup(game, "pre-restore", dryRun, saveDirectory); }
            catch { }
        }

        if (!dryRun)
        {
            foreach (var (rel, expectedSha) in chosenFiles)
            {
                var srcFile = Path.Combine(payloadDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var dstFile = FileSafety.AllowedPath(Path.Combine(targetSaveDir, rel.Replace('/', Path.DirectorySeparatorChar)), new[] { game }, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                File.Copy(srcFile, dstFile, true);
                if (FileSafety.FileHash(dstFile) != expectedSha)
                    FileSafety.Fail("backup_verify_failed", ("target", dstFile));
            }
        }

        return new Dictionary<string, object?>
        {
            ["id"] = chosenModId,
            ["action"] = "save_restored",
            ["backup"] = chosenDir.Name,
            ["source"] = chosenDir.FullName,
            ["target"] = targetSaveDir,
            ["fileCount"] = chosenFiles.Count,
            ["dryRun"] = dryRun,
            ["preRestoreBackup"] = preRestoreBackup,
        };
    }
}
