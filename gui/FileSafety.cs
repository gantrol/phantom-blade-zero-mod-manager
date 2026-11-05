using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomBladeModManager;

internal sealed class ManagerException : Exception
{
    public string Code { get; }
    public Dictionary<string, object?> Params { get; }

    public ManagerException(string code, Dictionary<string, object?>? parameters = null) : base(code)
    {
        Code = code;
        Params = parameters ?? new Dictionary<string, object?>();
    }
}

internal sealed record FileDigestEntry(string Path, long Bytes, string Sha256);
internal sealed record TreeDigestResult(string Digest, List<FileDigestEntry> Files);

internal static class FileSafety
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [DoesNotReturn]
    public static void Fail(string code, params (string Key, object? Value)[] parameters)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (k, v) in parameters) dict[k] = v;
        throw new ManagerException(code, dict);
    }

    public static string ValidatePath(string? input, string label = "path")
    {
        var value = (input ?? "").Trim();
        if (string.IsNullOrEmpty(value) || value.IndexOf('\0') >= 0) Fail("invalid_path", ("label", label));
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) Fail("unc_forbidden", ("label", label));
        var resolved = Path.GetFullPath(value);
        if (resolved.StartsWith(@"\\", StringComparison.Ordinal)) Fail("unc_forbidden", ("label", label));
        var parts = resolved.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => string.Equals(part, "trash", StringComparison.OrdinalIgnoreCase)))
            Fail("trash_forbidden", ("label", label));
        return resolved;
    }

    public static bool IsSubpath(string parent, string target)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(target));
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    public static string AllowedPath(string target, IEnumerable<string> allowedRoots, string label = "path")
    {
        var resolved = ValidatePath(target, label);
        if (!allowedRoots.Any(root => IsSubpath(ValidatePath(root, label), resolved)))
            Fail("outside_allowed_root", ("label", label), ("target", resolved));
        return resolved;
    }

    public static FileSystemInfo? GetNodeInfoIfExists(string target, string label = "path")
    {
        var resolved = ValidatePath(target, label);
        var fileInfo = new FileInfo(resolved);
        if (fileInfo.Exists || (fileInfo.Attributes != (FileAttributes)(-1) && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || fileInfo.LinkTarget != null)
                Fail("symlink_forbidden", ("target", resolved));
            return fileInfo;
        }
        var dirInfo = new DirectoryInfo(resolved);
        if (dirInfo.Exists || (dirInfo.Attributes != (FileAttributes)(-1) && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.LinkTarget != null)
                Fail("symlink_forbidden", ("target", resolved));
            return dirInfo;
        }
        return null;
    }

    public static void AssertRealParentChain(string target, string stopRoot, string label = "path")
    {
        var resolved = ValidatePath(target, label);
        var root = ValidatePath(stopRoot, label);
        if (!IsSubpath(root, resolved)) Fail("outside_allowed_root", ("label", label), ("target", resolved));
        var chain = new List<string>();
        var current = resolved;
        while (true)
        {
            chain.Add(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            var next = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) break;
            current = next;
        }
        chain.Reverse();
        foreach (var node in chain) GetNodeInfoIfExists(node, label);
    }

    public static void AssertRealTree(string dir, string stopRoot, string label = "path")
    {
        var resolved = ValidatePath(dir, label);
        AssertRealParentChain(resolved, stopRoot, label);
        var info = GetNodeInfoIfExists(resolved, label);
        if (info == null) return;
        if (info is not DirectoryInfo dirInfo) { Fail("expected_directory", ("target", resolved)); return; }
        foreach (var entry in dirInfo.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget != null)
                Fail("symlink_forbidden", ("target", entry.FullName));
            if (entry is DirectoryInfo subDir)
                AssertRealTree(subDir.FullName, stopRoot, label);
        }
    }

    public static string BytesHash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string StringHash(string text) => BytesHash(Encoding.UTF8.GetBytes(text));

    public static string FileHash(string filePath)
    {
        var resolved = ValidatePath(filePath, "file");
        var info = GetNodeInfoIfExists(resolved, "file");
        if (info is not FileInfo) Fail("expected_file", ("target", resolved));
        using var stream = File.OpenRead(resolved);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static TreeDigestResult TreeDigest(string rootDir, Func<string, bool>? ignoreRelative = null)
    {
        var resolved = ValidatePath(rootDir, "directory");
        var files = new List<FileDigestEntry>();

        void Walk(string current)
        {
            var info = GetNodeInfoIfExists(current, "directory");
            if (info is not DirectoryInfo dirInfo) { Fail("expected_directory", ("target", current)); return; }
            var entries = dirInfo.GetFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget != null)
                    Fail("symlink_forbidden", ("target", entry.FullName));
                var rel = Path.GetRelativePath(resolved, entry.FullName).Replace('\\', '/');
                if (ignoreRelative != null && ignoreRelative(rel)) continue;
                if (entry is DirectoryInfo) Walk(entry.FullName);
                else if (entry is FileInfo fi)
                    files.Add(new FileDigestEntry(rel, fi.Length, FileHash(fi.FullName)));
            }
        }

        Walk(resolved);
        var lines = string.Join("\n", files.Select(f => $"{f.Path}\t{f.Bytes}\t{f.Sha256}"));
        return new TreeDigestResult(StringHash(lines), files);
    }

    public static void RemoveTreeIfExists(string target, IEnumerable<string> allowedRoots)
    {
        var resolved = AllowedPath(target, allowedRoots, "remove target");
        foreach (var root in allowedRoots)
        {
            if (IsSubpath(root, resolved)) AssertRealParentChain(resolved, root, "remove target");
        }
        var info = GetNodeInfoIfExists(resolved, "remove target");
        if (info == null) return;
        if (info is DirectoryInfo)
        {
            foreach (var root in allowedRoots)
            {
                if (IsSubpath(root, resolved)) AssertRealTree(resolved, root, "remove target");
            }
            Directory.Delete(resolved, true);
        }
        else
        {
            File.Delete(resolved);
        }
    }

    public static void CopyCleanTree(string sourceDir, string destinationDir, IEnumerable<string> allowedRoots, Func<string, bool>? ignoreRelative = null)
    {
        var src = AllowedPath(sourceDir, allowedRoots, "source");
        var dst = AllowedPath(destinationDir, allowedRoots, "destination");
        RemoveTreeIfExists(dst, allowedRoots);

        void CopyNode(string currentSrc, string currentDst)
        {
            var info = GetNodeInfoIfExists(currentSrc, "source");
            if (info is not DirectoryInfo dirInfo) { Fail("expected_directory", ("target", currentSrc)); return; }
            Directory.CreateDirectory(currentDst);
            foreach (var entry in dirInfo.GetFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget != null)
                    Fail("symlink_forbidden", ("target", entry.FullName));
                var rel = Path.GetRelativePath(src, entry.FullName).Replace('\\', '/');
                if (ignoreRelative != null && ignoreRelative(rel)) continue;
                var nextDst = Path.Combine(currentDst, entry.Name);
                if (entry is DirectoryInfo) CopyNode(entry.FullName, nextDst);
                else if (entry is FileInfo fi) fi.CopyTo(nextDst, true);
            }
        }

        CopyNode(src, dst);
    }

    public static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
    }

    public static void MoveTreeOrCopy(string sourceDir, string destinationDir, IEnumerable<string> allowedRoots)
    {
        var src = AllowedPath(sourceDir, allowedRoots, "source");
        var dst = AllowedPath(destinationDir, allowedRoots, "destination");
        foreach (var root in allowedRoots)
        {
            if (IsSubpath(root, src)) AssertRealTree(src, root, "source");
            if (IsSubpath(root, dst)) AssertRealParentChain(dst, root, "destination");
        }
        RemoveTreeIfExists(dst, allowedRoots);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        try
        {
            Directory.Move(src, dst);
        }
        catch (IOException)
        {
            CopyCleanTree(src, dst, allowedRoots);
            RemoveTreeIfExists(src, allowedRoots);
        }
    }

    public static void WriteJsonAtomic<T>(string filePath, T value, IEnumerable<string> allowedRoots)
    {
        var resolved = AllowedPath(filePath, allowedRoots, "json output");
        foreach (var root in allowedRoots)
        {
            if (IsSubpath(root, resolved)) AssertRealParentChain(resolved, root, "json output");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        var temp = $"{resolved}.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.tmp";
        var text = JsonSerializer.Serialize(value, JsonOptions) + "\n";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, resolved, true);
    }

    public static void WriteTextAtomic(string filePath, string content, IEnumerable<string> allowedRoots)
    {
        var resolved = AllowedPath(filePath, allowedRoots, "text output");
        foreach (var root in allowedRoots)
        {
            if (IsSubpath(root, resolved)) AssertRealParentChain(resolved, root, "text output");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        var temp = $"{resolved}.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, resolved, true);
    }

    private static bool IsProcessRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<T> WithOperationLockAsync<T>(string lockPath, IEnumerable<string> allowedRoots, Func<Task<T>> action)
    {
        var resolved = AllowedPath(lockPath, allowedRoots, "lock");
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using (var stream = new FileStream(resolved, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedAt = DateTime.UtcNow.ToString("O") }));
                }
                break;
            }
            catch (IOException)
            {
                if (attempt == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(resolved));
                        var pid = doc.RootElement.TryGetProperty("pid", out var pidProp) ? pidProp.GetInt32() : 0;
                        if (!IsProcessRunning(pid))
                        {
                            File.Delete(resolved);
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }
                Fail("lock_busy", ("lockPath", resolved));
            }
        }
        try
        {
            return await action();
        }
        finally
        {
            try { File.Delete(resolved); } catch { }
        }
    }

    public static bool IsGameRunning()
    {
        foreach (var name in new[] { "PBZ", "ProjectPBZ-Win64-Shipping" })
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                var any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            catch
            {
            }
        }
        return false;
    }

    public static async Task<(int ExitCode, byte[] StdOut, string StdErr)> RunProcessBytesAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        using var ms = new MemoryStream();
        var stdOutTask = process.StandardOutput.BaseStream.CopyToAsync(ms);
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync());
        return (process.ExitCode, ms.ToArray(), await stdErrTask);
    }

    public static async Task<byte[]> RunToolCheckedAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var (code, stdout, stderr) = await RunProcessBytesAsync(fileName, arguments, workingDirectory);
        if (code != 0)
        {
            var errText = string.IsNullOrWhiteSpace(stderr) ? Encoding.UTF8.GetString(stdout) : stderr;
            throw new InvalidOperationException($"Tool {Path.GetFileName(fileName)} failed ({code}): {errText.Trim()}");
        }
        return stdout;
    }
}
