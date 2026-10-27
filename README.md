# 远程连接助手（RemoteConnectHelper）

灰度科技内部工具。从聊天截图里自动识别**向日葵 / ToDesk** 的设备码与验证码，一键拉起对应远程软件并自动填码连接，减少客服手动复制输入。

## 灰Desk V2 当前测试版

- [下载 20261027.21 中文离线安装包](https://github.com/hekairui2018/RemoteConnectHelper/releases/tag/v2.2.0-test.20261027.21)
- [二代源码、构建和验证范围](RemoteConnectHelper.V2/README.md)
- [安装包构建说明](installer/README.md)

悬浮球/快捷键/明确点击识别并连接，同图可重试；预览和核对区默认上下锁定、两区置顶；密码候选最短4字符。图片识别、输入框定位、整窗填码核验和客户端状态读取全部只使用独立进程中的PP-OCRv5，已移除Windows OCR。客户端的失败、等待密码/授权、成功和尚未确认分别显示。

本仓库公开源码，客户原图、设备码、密码和原始异常包不公开上传。Release使用经核验的安装包，模型和运行时已包含，无需安装.NET。主要适配向日葵与ToDesk，其他软件能力及受控测试边界见二代说明。真实远程连接仍需人工验收。

一代源码保留于 `RemoteConnectHelper/`。以下历史目录说明不表示当前发布的入口或版本。

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
