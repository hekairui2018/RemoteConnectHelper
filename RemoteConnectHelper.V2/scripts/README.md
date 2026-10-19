# 本地测试工具

这些工具不调用云端OCR或在线AI。只操作本机文件，不执行真实远程连接。

- `Build-AnnotationWorkbook.py`：从本机图片目录生成不含OCR预测的人工核对页面，支持确认软件、设备码、密码状态、多目标、保存进度与导入答案。需要本机 Python、Pillow、NumPy。
- `Probe-UnlabelledImages.py`：逐图启动离线 `--selftest` 子进程，记录原始OCR证据与耗时。默认每图15秒上限；超时仅终止由此工具建立的诊断子进程。冷进程耗时包含运行时与模型初始化，不能直接与热流水线时延比较。
- `Compare-Recognition.ps1`：比较同一固定答案清单的两次 `--evaluate` 输出，分别统计字段、原有四项通过数、错误自动判定、人工比例和时延。
- `Install-Models.ps1`：安装并校验匹配的本地模型与字典。

例如在仓库根目录执行（示例路径需换成本机路径）：

```powershell
python RemoteConnectHelper.V2/scripts/Build-AnnotationWorkbook.py --images <本机原图目录> --output <新的核对目录> --known testcases --archive-sha <原压缩包SHA256>
python RemoteConnectHelper.V2/scripts/Probe-UnlabelledImages.py --workbook <核对目录> --dotnet <dotnet.exe路径> --assembly <编译后的V2.dll路径> --output <新的诊断目录> --timeout 15
```

先对原图独立标注，未知、被遮挡和多目标要明确记录；不要把OCR输出当作真值。重复图与旧样本交叉需要另列，不能全部当作新增盲测。人工答案尚未确认时，只能报告识别缺失、冲突、自动/人工提议和耗时，不能报告准确率、错误自动次数或高置信度错误数。

生成的HTML包含原图文件名等本机信息，图像、答案和诊断JSON可含真实凭据。放在被忽略的obj目录或约定的本机产物目录；不要提交Git、放入公开Release或上传整个核对目录。

第一阶段优化在原有35张开发回归集上：完整字段32/35→33/35；软件33/35不变；错误自动判定1→0；人工9→11；原含决策的四项通过32/35→31/35。三轮平均441.78→464.86ms，p95约持平。该结果不是独立盲测或现场连接率，未证明真实客户恶劣图片场景的准确率。已建立人工核对工具，真实样本的答案仍待确认。
