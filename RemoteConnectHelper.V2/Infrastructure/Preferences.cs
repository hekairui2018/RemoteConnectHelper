using System.Text.Json;

namespace RemoteConnectHelper.V2.Infrastructure;

public sealed class Preferences
{
    public bool AutoConnect { get; set; } = true;
    public bool WatchClipboard { get; set; }
    public bool ShowFloatingBall { get; set; } = true;
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Log.Root, "preferences.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Log.Root); File.WriteAllText(Path.Combine(Log.Root, "preferences.json"), JsonSerializer.Serialize(this)); }
        catch { }
    }
}
