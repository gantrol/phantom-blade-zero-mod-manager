using System.Text.Json;

namespace PhantomBladeMap.Core.Runtime;

internal sealed class MapSettings
{
    private readonly MarkerWriter writer;
    public bool Native { get; set; } = true;
    public bool ExploredOnly { get; set; } = true;
    public bool Overlay { get; set; } = true;
    public bool Mini { get; set; }
    public bool List { get; set; } = true;
    public string? Error { get; private set; }
    public int Flags => (Native ? 1 : 0) | (ExploredOnly ? 2 : 0) | (Overlay ? 4 : 0) | (Mini ? 8 : 0) | (List ? 16 : 0);

    // Constructed on the initialization worker; no settings reads on EngineTick.
    public MapSettings(string path, bool mini)
    {
        writer = new(path); Mini = mini;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("settings_size");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Native = !root.TryGetProperty("mapStyle", out var style) || style.GetString() != "survey";
            bool Read(string name, bool fallback) => root.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
            ExploredOnly = Read("exploredOnly", true); Overlay = Read("overlayVisible", true);
            Mini = Read("minimapVisible", mini); List = Read("remainingList", true);
        }
        catch (Exception error) { Native=true; ExploredOnly=true; Overlay=true; Mini=mini; List=true; Error = error.GetType().Name; }
    }

    public void Save()
    {
        if (Error is not null) return; // Keep an unreadable user file intact.
        string Flag(bool value) => value ? "true" : "false";
        writer.Save($$"""
            {"schema":1,"mapStyle":"{{(Native ? "native" : "survey")}}","exploredOnly":{{Flag(ExploredOnly)}},"overlayVisible":{{Flag(Overlay)}},"minimapVisible":{{Flag(Mini)}},"remainingList":{{Flag(List)}}}
            """);
    }

    public Task CompleteAsync() => writer.CompleteAsync();
}
