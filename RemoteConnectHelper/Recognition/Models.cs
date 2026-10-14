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
    /// <summary>密码只从弱线索读到、或标签旁没有可读值，供预览与纠错提示使用，不单独阻止直连。</summary>
    public bool PasswordNeedsReview { get; set; }
    /// <summary>识别采用的旋转角度，供卡片按同一方向展示截图。</summary>
    public int ImageRotation { get; set; }
    /// <summary>只有增强副本才读到的结果，用于日志与预览，不单独阻止直连。</summary>
    public bool ImageEnhanced { get; set; }
    /// <summary>是否识别到密码（false = 免密连接）</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>当有多个候选设备码时列出，供卡片下拉</summary>
    public List<string> CandidateCodes { get; set; } = new();

    /// <summary>密码的其它 OCR 读法候选（来自图上真实识别到的不同短串，非机械字符互换），供卡片点选</summary>
    public List<string> CandidatePasswords { get; set; } = new();

    /// <summary>非空表示识别失败/需要人工，卡片应切手动模式并显示该提示</summary>
    public string? Error { get; set; }

    /// <summary>品牌（是向日葵还是 ToDesk）是否有决定性证据（品牌名/“识别码·验证码” vs “设备代码·安全密码”）。
    /// false=只凭中性词或码长默认 guessed，码虽认到也应弹气泡让客服瞄一眼，防止连错软件。</summary>
    public bool BrandConfident { get; set; } = true;

    /// <summary>品牌判定的人话依据（命中了哪个品牌名/术语，或“无强信号默认向日葵”），仅用于日志复盘。</summary>
    public string BrandReason { get; set; } = "";

    /// <summary>目标设备码在截图中的中心坐标（像素，-1 表示无）。用于同屏冲突时按“目标码所在区域”的
    /// 品牌证据定默认软件（双窗口截图：左边向日葵窗口、右边 ToDesk 窗口，码在哪个窗口就按哪个窗口判）。</summary>
    public double TargetCodeX { get; set; } = -1;
    public double TargetCodeY { get; set; } = -1;

    public string KindName => Kind switch
    {
        RemoteAppKind.Sunlogin => "向日葵",
        RemoteAppKind.ToDesk => "ToDesk",
        _ => "未识别",
    };
}
