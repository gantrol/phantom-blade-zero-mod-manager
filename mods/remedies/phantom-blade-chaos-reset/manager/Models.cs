global using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhantomBladeQuestManager;

sealed class ManagerException(string code) : Exception(code);
static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
sealed record QuestState(string Id, bool Eligible, string Status);
sealed record SlotInfo(string Name, string Date, string Level, int PlaySeconds, QuestState[] Quests);
sealed record SaveInfo(string Path, string Fingerprint, SlotInfo[] Slots);
sealed record Change(string Path, string? Before, string? After);
sealed record ManualSaveResult(string SourceSlot, string Slot, string[] Quests, string? Backup, Change[] Changes, string Fingerprint);
sealed class QuestRule
{
    public string Id { get; set; } = "";
    public string Quest { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string[] Spawners { get; set; } = [];
    public string[] Owners { get; set; } = [];
    public string[] ExecutionNodes { get; set; } = [];
    public string[] DataLayers { get; set; } = [];
}
