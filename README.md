# 远程连接助手（RemoteConnectHelper）

灰度科技内部工具。从聊天截图里自动识别**向日葵 / ToDesk** 的设备码与验证码，一键拉起对应远程软件并自动填码连接，减少客服手动复制输入。

- 技术栈：C# / WPF / .NET 10（`net10.0-windows10.0.19041.0`，x64）
- 支持对象：向日葵 `AweSun.exe`、ToDesk（其余远程软件不处理）
- 交互：悬浮球 + 全局热键 `Ctrl+Alt+R`；剪贴板截图监听，识别后弹确认卡片
- 识别：本地 OCR + 设备码/验证码解析，回归样例见 `testcases/`

> 本仓库为**私有内部库**，不含发布用的自包含单文件 exe 与构建产物。

## 目录结构

```
RemoteConnectHelper/          # WPF 项目源码
  App.xaml(.cs)               # 应用入口、整体流程编排
  RemoteConnectHelper.csproj
  Assets/                     # 图标等资源
  Automation/                 # UIA 自动化：定位窗口、填码、点击、连接器
  Recognition/                # OCR、设备码/验证码识别与数据模型
  Hotkey/                     # 全局热键与键盘钩子
  Services/                   # 配置、日志、剪贴板图片、OCR 服务
  UI/                         # 悬浮球、确认卡片、状态 HUD、欢迎窗
testcases/                    # 识别引擎回归样例（截图 + chat_cases.json）
接手说明_笔记本先读.md
远程连接助手_开发需求文档_V1.0.md
```

## 构建

```powershell
dotnet build .\RemoteConnectHelper\RemoteConnectHelper.csproj -c Debug
```

发布自包含单文件（x64）：

```powershell
dotnet publish .\RemoteConnectHelper\RemoteConnectHelper.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 约定

- 大改前先复制一份带日期的备份。
- 仅识别/自动化向日葵与 ToDesk，不扩展到其它远程软件。
- 界面/窗口改动后需真机运行截图确认，再交付。
