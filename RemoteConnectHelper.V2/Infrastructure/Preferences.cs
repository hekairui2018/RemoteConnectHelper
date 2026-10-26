using System.Text.Json;

namespace RemoteConnectHelper.V2.Infrastructure;

public sealed class Preferences
{
    public List<RemoteConnectHelper.V2.Discovery.SoftwareEntry>? SoftwareEntries {get;set;}
    public string? WallpaperPath {get;set;}
    public string? LastWallpaperFolder {get;set;}
    public double[]? WallpaperPickerBounds {get;set;}
    public string Theme {get;set;}="Light";
    public bool AutoConnect { get; set; } = true;
    public bool WatchClipboard { get; set; } = true;
    public bool ClipboardConsent { get; set; }
    public bool ShowFloatingBall { get; set; } = true;
    public string? LastImageFolder {get;set;}
    public double[]? ImagePickerBounds {get;set;}
    public double FloatingOpacity { get; set; } = .85;
    public double EffectiveFloatingOpacity => double.IsFinite(FloatingOpacity)?Math.Clamp(FloatingOpacity,.1,1):.85;
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
