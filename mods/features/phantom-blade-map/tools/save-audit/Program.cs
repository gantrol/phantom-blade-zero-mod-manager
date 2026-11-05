using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PhantomBladeQuestManager;

// Offline read-only audit. The save parser and build-specific key reader are
// shared with the quest manager; no save-writing method is invoked here.
var options = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i += 2) options.Add(args[i], args[i + 1]);
string game = GameFiles.Allowed(options["--game"]);
string source = GameFiles.Allowed(options["--save"]);
string output = GameFiles.Allowed(options["--out"]);
SaveContainer.Require(!string.Equals(source, output, StringComparison.OrdinalIgnoreCase), "outputIsSource");
NativeLibrary.SetDllImportResolver(typeof(SaveContainer).Assembly, (name, _, _) =>
    name == "oo2core_9_win64.dll" ? NativeLibrary.Load(GameFiles.Allowed(options["--oodle"])) : IntPtr.Zero);
byte[] bytes = await File.ReadAllBytesAsync(source);
var container = SaveContainer.Parse(bytes, await GameFiles.ReadKeyAsync(game));
var slots = new List<(string Name, string Date, JsonObject Data)>();
foreach (var block in container.Blocks)
{
    if (!Regex.IsMatch(block.Name, "^(AutoSave[0-9]+|ManualSave[1-9][0-9]*)$")) continue;
    var data = container.ReadSlot(block.Name);
    slots.Add((block.Name, data["PBZGameSystem"]?["DateTimeString"]?.GetValue<string>() ?? "", data));
}
var selected = options.TryGetValue("--slot", out var slot)
    ? slots.Single(s => s.Name == slot)
    : slots.OrderByDescending(s => s.Date, StringComparer.Ordinal).First();
var report = new
{
    source, sha256 = GameFiles.Hash(bytes), selectedSlot = selected.Name,
    slots = slots.Select(s => new { s.Name, s.Date, level = s.Data["PBZGameSystem"]?["CurrentLevelName"],
        playSeconds = s.Data["PBZGameSystem"]?["HasGameTime"] }),
    data = selected.Data
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
Console.WriteLine(JsonSerializer.Serialize(new { selected.Name, selected.Date,
    slots = report.slots, systems = selected.Data.Select(p => p.Key) }));
