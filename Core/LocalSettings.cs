using Microsoft.Win32;
using System.Text.Json.Nodes;

namespace CodexUsageNotch;

internal enum StripAlignment { Left, Center, Right }

internal sealed class UserPreferences(string? path = null)
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageNotch");
    private readonly string _path = path ?? Path.Combine(DataDirectory, "settings.json");
    private JsonObject _data = new();
    public bool RingEnabled { get; set; }
    public bool StripVisible { get; set; } = true;
    public double StripPosition { get; set; } = 0.5;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public void Load()
    {
        try
        {
            _data = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new();
            RingEnabled = ReadBool("taskbarUsageRingEnabled", false);
            StripVisible = ReadBool("stripVisible", true);
            var legacyAlignment = _data["stripAlignment"] is JsonValue value && value.TryGetValue<string>(out var name)
                && Enum.TryParse<StripAlignment>(name, true, out var alignment) && Enum.IsDefined(alignment)
                ? alignment : StripAlignment.Center;
            StripPosition = _data["stripPosition"] is JsonValue position && position.TryGetValue<double>(out var fraction)
                && double.IsFinite(fraction) && fraction is >= 0 and <= 1
                ? fraction : (double)legacyAlignment / 2;
            LastUpdateCheckUtc = _data["lastUpdateCheckUtc"] is JsonValue checkedAt && checkedAt.TryGetValue<string>(out var timestamp)
                && DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Diagnostics.Write("settings-read", e); }
    }
    private bool ReadBool(string key, bool fallback) => _data[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _data["taskbarUsageRingEnabled"] = RingEnabled;
            _data["stripVisible"] = StripVisible;
            _data["stripPosition"] = StripPosition;
            _data["lastUpdateCheckUtc"] = LastUpdateCheckUtc?.ToString("O");
            File.WriteAllText(_path + ".tmp", _data.ToJsonString());
            File.Move(_path + ".tmp", _path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Diagnostics.Write("settings-write", e); return false; }
    }
}
internal static class Diagnostics
{
    private static readonly object Gate = new();
    public static void Write(string operation, Exception? error = null)
    {
        // Fixed operation names only. Never serialize messages, paths, payloads or URLs.
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(UserPreferences.DataDirectory);
                var path = Path.Combine(UserPreferences.DataDirectory, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation} {error?.GetType().Name ?? "ok"} {error?.HResult ?? 0}\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(Key); return string.Equals(key?.GetValue("CodexUsageNotch") as string, Command, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key, true);
            if (enabled) key.SetValue("CodexUsageNotch", Command); else key.DeleteValue("CodexUsageNotch", false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Diagnostics.Write("startup-setting", e); return false; }
    }
    private static string Command => $"\"{Application.ExecutablePath}\"";
}
