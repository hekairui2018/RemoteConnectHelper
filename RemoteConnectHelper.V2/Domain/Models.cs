namespace RemoteConnectHelper.V2.Domain;

public enum RemoteApp { Unknown, Sunlogin, ToDesk, AnyDesk, UU, RustDesk, TeamViewer, UltraViewer }
public enum ConnectionDecision { Manual, Automatic }
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public static Box Union(IEnumerable<Box> boxes)
    {
        var a = boxes.ToArray();
        if (a.Length == 0) return default;
        double x = a.Min(b => b.X), y = a.Min(b => b.Y);
        return new(x, y, a.Max(b => b.Right) - x, a.Max(b => b.Bottom) - y);
    }
}

public sealed record OcrTextBlock(string Id, string Text, Box Bounds, float? Confidence,
    string Engine, string Variant, Box? ReadingBounds = null)
{
    public Box Layout => ReadingBounds ?? Bounds;
}

public sealed record OcrDocument(IReadOnlyList<OcrTextBlock> Blocks, int Width, int Height,
    string Engine, string Variant, int Rotation = 0, long ElapsedMs = 0);

public sealed record SoftwareCandidate(RemoteApp App, double RuleScore, Box Bounds, string[] BlockIds, string[] Evidence);
public sealed record DeviceCodeCandidate(string Id, string Value, RemoteApp App, Box Bounds, Box ReadingBounds,
    float? OcrConfidence, double RuleScore, string[] BlockIds, string[] Evidence)
{
    public string RawText { get; init; } = "";
    public bool HadCharacterConversion { get; init; }
    public float? MinimumOcrConfidence { get; init; }
    public double? PairingScore { get; init; }
    public double RankingScore => PairingScore ?? RuleScore;
}
public sealed record PasswordCandidate(string Value, string DeviceCandidateId, RemoteApp App, Box Bounds,
    float? OcrConfidence, double RuleScore, string[] BlockIds, string[] Evidence)
{
    public string RawText { get; init; } = "";
    public bool HadCharacterConversion { get; init; }
    public float? MinimumOcrConfidence { get; init; }
}

public sealed record OcrAttempt(OcrDocument Document, IReadOnlyList<DeviceCodeCandidate> DeviceCodes,
    IReadOnlyList<PasswordCandidate> Passwords, DeviceCodeCandidate? SelectedCode, PasswordCandidate? SelectedPassword);

public sealed record RecognitionResult(OcrDocument Document, IReadOnlyList<SoftwareCandidate> Software,
    IReadOnlyList<DeviceCodeCandidate> DeviceCodes, IReadOnlyList<PasswordCandidate> Passwords,
    DeviceCodeCandidate? SelectedCode, PasswordCandidate? SelectedPassword,
    ConnectionDecision Decision, string Reason, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<OcrAttempt> Attempts { get; init; } = [];
    public IReadOnlyList<string> CrossCheckIssues { get; init; } = [];
    public RemoteApp App => SelectedCode?.App ?? RemoteApp.Unknown;
    public string DeviceCode => SelectedCode?.Value ?? "";
    public string Password => SelectedPassword?.Value ?? "";
    public string AppName => App switch { RemoteApp.Sunlogin => "向日葵", RemoteApp.ToDesk => "ToDesk", RemoteApp.AnyDesk=>"AnyDesk",RemoteApp.UU=>"UU远程",RemoteApp.RustDesk=>"RustDesk",RemoteApp.TeamViewer=>"TeamViewer",RemoteApp.UltraViewer=>"UltraViewer", _ => "请选择软件" };
}

public static class ConnectionPolicy
{
    public static bool ValidCode(RemoteApp app, string? code)
    {
        code = (code ?? "").Replace(" ", "").Trim();
        bool length = app switch { RemoteApp.Sunlogin or RemoteApp.AnyDesk => code.Length is 9 or 10, RemoteApp.ToDesk or RemoteApp.UU => code.Length == 9, RemoteApp.RustDesk=>code.Length is 7 or 8 or 9 or 10, RemoteApp.TeamViewer or RemoteApp.UltraViewer=>code.Length is >= 9 and <= 10, _ => false };
        return length && code.All(c => c is >= '0' and <= '9');
    }
    public static bool CanConnect(RemoteApp app, string code) => ValidCode(app, code);
}

public static class RemoteApps
{
    public static readonly RemoteApp[] Supported=[RemoteApp.Sunlogin,RemoteApp.ToDesk,RemoteApp.AnyDesk,RemoteApp.UU,RemoteApp.RustDesk,RemoteApp.TeamViewer,RemoteApp.UltraViewer];
    public static string Name(RemoteApp app)=>app switch{RemoteApp.Sunlogin=>"向日葵",RemoteApp.ToDesk=>"ToDesk",RemoteApp.AnyDesk=>"AnyDesk",RemoteApp.UU=>"UU远程",RemoteApp.RustDesk=>"RustDesk",RemoteApp.TeamViewer=>"TeamViewer",RemoteApp.UltraViewer=>"UltraViewer",_=>"未知软件"};
    public static string Id(RemoteApp app)=>app switch{RemoteApp.Sunlogin=>"sunlogin",RemoteApp.ToDesk=>"todesk",RemoteApp.AnyDesk=>"anydesk",RemoteApp.UU=>"uu",RemoteApp.RustDesk=>"rustdesk",RemoteApp.TeamViewer=>"teamviewer",RemoteApp.UltraViewer=>"ultraviewer",_=>throw new ArgumentOutOfRangeException(nameof(app))};
    public static string[] Processes(RemoteApp app)=>app switch{RemoteApp.Sunlogin=>["AweSun","SunloginClient"],RemoteApp.ToDesk=>["ToDesk"],RemoteApp.AnyDesk=>["AnyDesk"],RemoteApp.UU=>["GameViewer"],RemoteApp.RustDesk=>["rustdesk"],RemoteApp.TeamViewer=>["TeamViewer"],RemoteApp.UltraViewer=>["UltraViewer_Desktop","UltraViewer"],_=>[]};
}
