namespace RemoteConnectHelper.Recognition;

public enum RemoteAppKind
{
    Unknown = 0,
    Sunlogin = 1,   // 向日葵
    ToDesk = 2,
}

/// <summary>OCR 出来的一个词及其在截图中的位置（像素）</summary>
public sealed class OcrWord
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterY => Y + H / 2.0;
    public double CenterX => X + W / 2.0;
}

public sealed class OcrDoc
{
    public List<OcrWord> Words { get; set; } = new();
    public string FullText { get; set; } = "";
    /// <summary>去掉所有空白的全文（OCR 常把中文拆成单字词，关键词匹配要用它）</summary>
    public string FlatText { get; set; } = "";
    public double ImgWidth { get; set; }
    public double ImgHeight { get; set; }
    /// <summary>本次 OCR 前对图像做的放大倍数（词坐标已按该倍数还原回原图坐标系）。1=未放大。</summary>
    public double ScaleApplied { get; set; } = 1.0;
}

/// <summary>识别结果</summary>
public sealed class RecognizedResult
{
    public RemoteAppKind Kind { get; set; } = RemoteAppKind.Unknown;
    public string DeviceCode { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>是否识别到密码（false = 免密连接）</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>当有多个候选设备码时列出，供卡片下拉</summary>
    public List<string> CandidateCodes { get; set; } = new();

    /// <summary>非空表示识别失败/需要人工，卡片应切手动模式并显示该提示</summary>
    public string? Error { get; set; }

    /// <summary>品牌（是向日葵还是 ToDesk）是否有决定性证据（品牌名/“识别码·验证码” vs “设备代码·安全密码”）。
    /// false=只凭中性词或码长默认 guessed，码虽认到也应弹气泡让客服瞄一眼，防止连错软件。</summary>
    public bool BrandConfident { get; set; } = true;

    /// <summary>品牌判定的人话依据（命中了哪个品牌名/术语，或“无强信号默认向日葵”），仅用于日志复盘。</summary>
    public string BrandReason { get; set; } = "";

    public string KindName => Kind switch
    {
        RemoteAppKind.Sunlogin => "向日葵",
        RemoteAppKind.ToDesk => "ToDesk",
        _ => "未识别",
    };
}
