# 灰Desk V2

当前测试版：`2.2.0-test.20261021.20`。完整离线安装包见仓库的预发布 Release；安装包内含运行时和模型。

主要流程为截图或复制图片后，单击悬浮球、按 Ctrl+Alt+R 或点击“识别并连接”。明确点击会重新执行，同图也可重试；“识别”只填入核对区。密码候选最短4字符，仍按字段位置与软件区域配对，不添加图片放大。

默认预览在上、核对区在下，每区384×500、整体384×1000，默认锁定，两区均置顶。可解锁拖动、四边吸附，托盘可显示/收起主界面、切换剪贴板监听和悬浮球。主题和本地壁纸支持静态图片及GIF。

PP-OCRv5通过RapidOcrNet离线运行在独立子进程中；工作进程退出、取消或超时不结束主界面，识别失败尝试Windows OCR。客户端状态侧读取当前可见状态区域并接入PP-OCR，显示失败、等待密码/授权、成功或尚未确认。成功仍需目标、会话控制及连续状态证据；OCR文字不会单独判成功。异常仅写本地日志，未接通自动日志回传。

向日葵、ToDesk是主要连接适配对象。其他软件有发现/输入或实验性连接路径，不能视为全部客户端均稳定通过真实连接验收。启动时在后台检查正在运行的软件，补充条目或更新经校验的路径，保留名称。

## 构建

需要x64 Windows 10 19041及以上、.NET 10 SDK。先运行 `scripts/Install-Models.ps1` 安装并校验识别模型；检测与方向模型随固定版本RapidOcrNet依赖提供。

```powershell
dotnet publish RemoteConnectHelper.V2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:InformationalVersion=2.2.0-test.20261021.20 -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=None
```

原生库提取参数不可省略。安装包脚本见仓库根目录 `installer/README.md`。配置与日志位于 `%APPDATA%\GrayDeskV2`，客户图片和识别凭据不应提交仓库或作为公开Release附件。

## 验证范围

内部识别回归35/35、状态9/9、模块50/50、OCR隔离10/10、窗口49/49、明确操作17/17、客户端状态23/23。窗口与状态测试包含真实PP-OCR和自建状态窗口，连接器采用受控模拟；这些结果不等于真实远程连接成功率，也不能替代客服机人工实测。历史键盘/前台限制和部分第三方客户端限制仍存在。
