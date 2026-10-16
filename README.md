# 远程连接助手（RemoteConnectHelper）

灰度科技内部工具。从聊天截图里自动识别**向日葵 / ToDesk** 的设备码与验证码，一键拉起对应远程软件并自动填码连接，减少客服手动复制输入。

## 二代测试版

- [二代完整测试包与版本说明](https://github.com/hekairui2018/RemoteConnectHelper/releases/tag/v2.0.0-test.20261015.02)
- [二代源码与构建说明](RemoteConnectHelper.V2/README.md)
- 二代使用 RapidOCR / PP-OCRv5 中文模型，Windows OCR 兜底；软件、设备码、密码按原图位置配对。
- 下载 Release 中的完整 ZIP，解压后运行 `RemoteConnectHelper.V2.exe`，旁边的 `Models` 文件夹必须保留，不需要安装 .NET。先退出旧版，避免快捷键冲突。
- 已知软件和有效设备码默认自动发起连接；软件无法确定时手选。支持完整原图预览、手改、候选切换和固定连接/停止按钮。
- 固定 35 张开发回归图通过 32 张（91.43%），其中应自动连接的 28 张中信息全部正确 25 张（89.29%）；19 项内部回归通过。该结果是离线开发回归，非独立盲测或现场连接成功率。
- 界面完成离屏检查，原生输入、焦点与现场连接仍待手测。真实截图及带凭据的诊断记录不放入公开仓库或 Release。

一代源码仍保留在 `RemoteConnectHelper/`，下面的技术和构建说明主要描述一代。二代为独立项目，未替换客服机上的运行版本。

- 技术栈：C# / WPF / .NET 10（`net10.0-windows10.0.19041.0`，x64）
- 支持对象：向日葵 `AweSun.exe`、ToDesk（其余远程软件不处理）
- 交互：悬浮球 + 全局热键 `Ctrl+Alt+R`；剪贴板截图监听，识别后弹确认卡片
- 识别：本地 OCR + 设备码/验证码解析，回归样例见 `testcases/`

> 本仓库公开源码。测试版完整运行包通过 GitHub 的预发布 Release 附件提供，构建目录及本机验收明细不提交到 Git。

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
