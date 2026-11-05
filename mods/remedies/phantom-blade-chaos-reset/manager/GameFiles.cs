using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhantomBladeQuestManager;

static class GameFiles
{
    public const string Build = "26367565";
    public static readonly string[] SupportedBuilds = ["26367565", "26266538"];
    public static string Allowed(string path)
    {
        string full = Path.GetFullPath(path);
        SaveContainer.Require(!full.Contains("trash", StringComparison.OrdinalIgnoreCase), "excludedPath");
        return full;
    }

    public static async Task<string> FindAsync()
    {
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        List<string> libraries = [steam];
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(vdf), "\"path\"\\s+\"([^\"\\r\\n]+)\""))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        foreach (string library in libraries.Distinct())
        {
            string game = Allowed(Path.Combine(library, "steamapps", "common", "PBZ"));
            if (File.Exists(Path.Combine(game, "platform", "steam", "PBZ.exe"))) return game;
        }
        throw new ManagerException("gameMissing");
    }

    public static void RequireClosed()
    {
        var processes = Process.GetProcessesByName("PBZ");
        bool open = processes.Length != 0;
        foreach (var process in processes) process.Dispose();
        SaveContainer.Require(!open, "gameRunning");
    }

    public static async Task<byte[]> ReadKeyAsync(string game)
    {
        string manifest = Allowed(Path.GetFullPath(Path.Combine(game, "..", "..", "appmanifest_4115450.acf")));
        string buildId = Regex.Match(await File.ReadAllTextAsync(manifest), "\"buildid\"\\s+\"(\\d+)\"").Groups[1].Value;
        SaveContainer.Require(SupportedBuilds.Contains(buildId), "wrongBuild");
        await using var file = new FileStream(Allowed(Path.Combine(game, "platform", "steam", "PBZ.exe")),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        async Task<byte[]> Read(long offset, int length)
        {
            byte[] data = new byte[length];
            file.Position = offset;
            await file.ReadExactlyAsync(data);
            return data;
        }
        byte[] dos = await Read(0, 64);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60));
        byte[] coff = await Read(pe, 24);
        SaveContainer.Require(coff.AsSpan(0, 4).SequenceEqual("PE\0\0"u8), "wrongBuild");
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        byte[] optional = await Read(pe + 24, optionalSize);
        long imageBase = BinaryPrimitives.ReadInt64LittleEndian(optional.AsSpan(24));
        byte[] sections = await Read(pe + 24 + optionalSize, sectionCount * 40);
        async Task<byte[]> At(long address, int length)
        {
            long rva = address - imageBase;
            for (int i = 0; i < sectionCount; i++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 12));
                int size = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 16));
                int offset = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 20));
                if (rva >= start && rva + length <= (long)start + size) return await Read(offset + rva - start, length);
            }
            throw new ManagerException("wrongBuild");
        }
        // Read this build's key material from the user's installed executable.
        // No game key is shipped in the manager or written to its configuration.
        var (va0, va1, va2, va3) = buildId == "26367565"
            ? (0x14693ae1aL + 0x58ff0aeL, 0x14693ae28L + 0x58ff080L, 0x14693ae37L + 0x58ff081L, 0x14693ae46L + 0x58ff092L)
            : (0x1468ff5baL + 0x58c918eL, 0x1468ff5c8L + 0x58c9160L, 0x1468ff5d7L + 0x58c9161L, 0x1468ff5e6L + 0x58c9172L);
        byte[] a = await At(va0, 16);
        byte[] b = await At(va1, 16);
        byte[] c = await At(va2, 16);
        byte[] d = await At(va3, 16);
        byte[] key = new byte[32];
        for (int i = 0; i < 16; i++) { key[i] = (byte)(a[i] ^ b[i]); key[i + 16] = (byte)(c[i] ^ d[i]); }
        return key;
    }

    public static string[] FindSaves(string game)
    {
        string root = Allowed(Path.Combine(game, "ProjectPBZ", "Saved", "SaveGames", "Steam"));
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Where(p => Regex.IsMatch(Path.GetFileName(p), "^[0-9]+$"))
            .Select(p => Allowed(Path.Combine(p, "PBZSaveGame.sav"))).Where(File.Exists).ToArray();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task DisableLegacyModAsync(string game)
    {
        RequireClosed();
        string mods = Allowed(Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods"));
        string prefix = "zzz_PhantomBladeChaosReset_P.";
        Dictionary<string, string[]> known = new()
        {
            ["pak"] = ["AAF18BC9429212C679E277EA894B1100BD31135FAC95778960238EEA8250D1D7", "6FB2F73300C1AD39C48567120766EBF7F3C5B15A4B958C2317C053BA68301111"],
            ["utoc"] = ["A8DF65F00506B03B7B1C0D3FE689333D81819997D18234BEB161FBE04EC1C442"],
            ["ucas"] = ["ED22C8ECC1C5B7C0C1095FD149E65BD87542C6247EFD762310F46C5CD63ACC05"]
        };
        List<string> files = [];
        foreach (var (extension, hashes) in known)
        {
            string path = Allowed(Path.Combine(mods, prefix + extension));
            if (!File.Exists(path)) continue;
            string digest = Hash(await File.ReadAllBytesAsync(path));
            SaveContainer.Require(hashes.Contains(digest), "legacyChanged");
            files.Add(path);
        }
        if (files.Count == 0) return;
        string backup = Allowed(Path.Combine(game, "platform", "steam", ".pbz-chaos-reset", "disabled", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(backup);
        List<(string Source, string Target)> moved = [];
        try
        {
            RequireClosed();
            foreach (string file in files)
            {
                string target = Path.Combine(backup, Path.GetFileName(file));
                File.Move(file, target);
                moved.Add((file, target));
            }
        }
        catch
        {
            foreach (var (source, target) in moved) File.Move(target, source);
            throw;
        }
    }
}
