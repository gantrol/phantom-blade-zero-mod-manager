using System.Globalization;
using System.Text.Json;

namespace PhantomBladeQuestManager;

static class Locale
{
    static Dictionary<string, string> messages = [];
    static CultureInfo culture = CultureInfo.CurrentCulture;
    public static async Task LoadAsync()
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "manager.json")))!;
        if (settings.TryGetValue("language", out var preferred) && preferred.GetString() is "en" or "zh-CN") language = preferred.GetString()!;
        culture = CultureInfo.GetCultureInfo(language);
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        messages = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!;
    }
    public static string Get(string key, params object[] args) => string.Format(culture, messages.GetValueOrDefault(key, key), args);
    public static string SaveDate(string value) => DateTime.TryParseExact(value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) ? time.ToString("g", culture) : value;
    public static string Error(Exception error)
    {
        if (error is ManagerException) return Get(error.Message);
        if (error is System.Security.Cryptography.CryptographicException) return Get("authentication");
        if (error is DllNotFoundException or EntryPointNotFoundException) return Get("compression");
        return Get("error", error.Message);
    }
    public static string Slot(string name)
    {
        if (name.StartsWith("AutoSave", StringComparison.Ordinal)) return Get("autoSlot", name[8..]);
        if (name.StartsWith("ManualSave", StringComparison.Ordinal)) return Get("manualSlot", name[10..]);
        return name;
    }
}
