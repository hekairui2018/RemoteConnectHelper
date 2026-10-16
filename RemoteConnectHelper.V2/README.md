# 二代远程助手

独立 WPF 程序，默认读取一张图片后识别并发起向日葵或 ToDesk 连接。没有识别出软件时保留设备码，等待用户选软件；没有密码也允许尝试连接。关闭“识别后自动连接”可先修改信息再连接。“自动读取新截图”默认关闭。

## 使用

解压完整测试包，保留程序旁边的 Models 文件夹，运行 `RemoteConnectHelper.V2.exe`。先退出旧助手，避免 Ctrl+Alt+R 快捷键冲突。框选截图后点“读取截图”、点悬浮球或按 Ctrl+Alt+R；也可打开或拖入图片。

预览保持整图，支持查看完整原图；蓝框标出设备码，橙框标出密码。识别、连接停止或失败后，普通输入框可直接修改。Esc 只在操作中停止，不再定时关闭输入界面。主窗口关闭后保留托盘和悬浮球，从托盘退出程序。

连接/停止按钮固定在底部，候选和详情单独滚动。切换候选同步切换密码与原图标记，也能切回初始候选；手改后取消对应旧标记。换新图立即清除旧连接信息。在窗口非文本编辑位置按 Ctrl+V 可读取截图；编辑框中继续使用正常文字粘贴。

软件、设备码和密码候选保留来源文字框。候选密码按设备码绑定，避免把另一窗口的密码拼给当前设备。密码保留大小写，不擅自将 o 改成 0；模糊字符仍可能需要手动修改。

## 结构

```
图片 → 方向/对比处理 → RapidOCR（PP-OCRv5 中文）
                         ↓ 异常或未读到设备码时 Windows OCR 兜底
OcrDocument（文字、原图坐标、真实 OCR 置信度）
    → RecognitionEngine（软件规则、码/密码候选、空间配对）
    → ConfidenceEngine（选择候选、解释规则、自动/人工）
    → AssistantController → 客户端兼容层
```

Domain/Parsing 不依赖窗口、鼠标或识别引擎。原图坐标和方向变换后的阅读坐标分别保存。规则分数不是统计正确率；Windows OCR 未提供的置信度用 null 表示。

Application 负责取消、等待前一操作收尾和阶段状态；Ocr 负责图像、识别和按图片内容缓存；UI 为重新实现的主窗口与悬浮球。Legacy 是从一代固定复制的连接、热键、剪贴板和 Windows OCR 兼容代码，并非全新实现的客户端驱动；没有引用一代项目或改动一代程序。

配置与运行日志独立存放在 `%AppData%\RemoteConnectHelperV2`。常规日志不记录密码，设备码连续数字脱敏。显式离线诊断命令会输出原始识别信息，含实际凭据的诊断文件只保留本地，不上传公开仓库。

## 编译

需要 Windows 10 19041+、x64、.NET 10 SDK。先在本目录执行 `./scripts/Install-Models.ps1`，该脚本从 RapidAI 官方模型仓库下载中文模型与字典并验证 SHA256。NuGet `RapidOcrNet 4.2.0` 自带检测/方向模型。

```powershell
& E:\开发工具\dotnet\dotnet.exe restore ./RemoteConnectHelper.V2.csproj -r win-x64
& E:\开发工具\dotnet\dotnet.exe publish ./RemoteConnectHelper.V2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

程序为自包含，中文模型和 NuGet 模型需与程序一并交付。源码中的大模型不提交 Git，通过固定地址和哈希恢复。

## 无桌面操作的检查

```powershell
./RemoteConnectHelper.V2.exe --verify
./RemoteConnectHelper.V2.exe --selftest <image>
./RemoteConnectHelper.V2.exe --evaluate <图片目录> <固定答案.json> <结果目录>
./RemoteConnectHelper.V2.exe --render <示例图片> <预览.png>
./RemoteConnectHelper.V2.exe --verify-ui <示例图片> <预览.png>
```

这些入口在热键、托盘、窗口显示、剪贴板监听初始化前分流，不会发起远程连接。`--render` 是离屏 WPF 渲染，不代表真机输入/焦点验收。新界面仍需用户手测确认。

评估清单包含全部图片的 SHA256、预先标注的软件/设备码/可见密码/决策；测试前检查图片集合和哈希，缺失、增删或改图直接失败。每图一次完整流水线调用，内部可以自动旋转、增强和兜底，不允许人工修改或删难图。四项全部精确匹配才算通过。空白图正确拒绝与有软件图片的自动连接信息正确率分别报告。

本次 35 张为现有开发回归集，包含合成聊天图、界面截图、模糊图、旋转拍屏与空白负样本；不是独立盲测，也不能据此推断客服现场连接成功率。真实图片与带凭据的答案/明细放在 obj 或本机交付记录中，均不提交公开仓库。

## 已知限制

- 模糊密码仍有 0/o、1/l 混淆，不强行按猜测替换。
- 严重模糊、品牌标签缺失或裁掉的软件区域，可能需要手选软件。
- 多个可连接目标同时出现时，按标签和配对排序，不能理解客户聊天中的最终意图。
- 底层向日葵/ToDesk 操作复用兼容代码，需要用户实际工作图片与客户端版本手测验证。

只在 3060 出测试包。本轮不更新客服机、GitHub 或现网。
