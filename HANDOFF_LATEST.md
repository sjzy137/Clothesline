# 最新交接：PP-OCRv6、真移动、设置窗口

2026-10-28。同一分支 `feature/windows-interactions`，仍只改 Windows 版（`Sources/` 未动）。用户反馈三件事：Windows 自带 OCR 识别率不够、Ctrl+拖动其实只复制、监控文件夹无法删除且缺少设置界面；随后补充要求右键加「取下（删除原文件）」、打包前先确认功能范围、以及把改版分支发布到自己的 GitHub。

## 文字识别换成 PP-OCRv6

选 `Sdcb.SimdPaddleOCR` 的决定性理由是**纯托管**：自带 ONNX 解释器，不依赖 ONNX Runtime / Paddle Inference / OpenCV 原生库。上一轮已经把应用做成自包含单文件发布，引入任何原生依赖都会把这条路堵死。

三档模型的实测（同一张 900×260 渲染图）：

| 档位 | 体积 | 载入 | 推理 | 结果 |
|---|---|---|---|---|
| Tiny | 6.2 MB | 879 ms | 3471 ms | 全部正确 |
| Small | 31 MB | 115 ms | 547 ms | 全部正确 |
| Medium | 139 MB | 470 ms | 883 ms | 全部正确 |

Tiny 慢是因为它是第一个跑的，吃了 JIT 预热。对照：Windows 自带引擎把 "Clothesline" 读成 "CIothesIine"。

三个只有真跑才能发现的点：

1. **字典必须分档。** Tiny 只有 6,904 个标签，Small/Medium 是 18,708 个。配错不会抛异常，只会把每个字索引到错误的类别上，输出看着合理但完全不对。
2. **下载源可以校验。** 实测 ModelScope 上六个 `inference.onnx` 的 SHA256 与 NuGet 内置模型逐一完全一致，所以落盘前校验能真正挡住截断的下载。
3. **输出已经处理混排。** `result.Text` 直接给出 "Clothesline 晾衣绳 2026"，上一轮为 Windows 引擎写的逐字符 CJK 拼接逻辑不再需要。

## Ctrl+拖动为什么上一轮是错的

上一轮写了 `CFSTR_PREFERREDDROPEFFECT` 并断言标记以 `DROPEFFECT_MOVE` 抵达剪贴板——自检也确实通过了。但用户实测拖到资源管理器只得到复制。**标记送达不代表目标会执行**：资源管理器对非 Shell 来源的 CF_HDROP 不执行移动。自检当时验证的是"标记能到达剪贴板"，而不是"文件会被移动"，这两件事不是一回事。

现在由晾衣绳自己在拖放成功后收尾。原文件送回收站而非硬删除，因为应用无法知道目标把副本放在哪、也无法验证它真的存下了。

## 拖入失效是上一轮「全屏隐身」的回归

`Native.ForegroundCoversMonitor()` 会把**最大化窗口**也算作"铺满整个显示器"。用户从最大化的资源管理器拖文件到屏幕顶部时，晾衣绳拒绝展开，drop 永远落不下去。另外 `externalDrop` 要求 `Height>8` 才算拖动，而收起态 Height 恰好是 8，形成"要先展开才能展开"的死循环。

## 顺带发现：PowerShell 脚本一直是坏的

`scripts/windows-context-menu.ps1` 缺 UTF-8 BOM，Windows PowerShell 5.1 按 GBK 解码，中文字符串里的字节被误读成引号，脚本根本无法解析。它此前从未被运行过。已给 scripts 下所有 .ps1 与 .iss 补上 BOM。

## 验证

Release 编译 0 错 0 警；`Clothesline.Tests` 通过；Windows 自检 11 项全通过（新增「recycle bin move」「pp-ocrv6 tiny read rendered text (3 Han chars)」），耗时 5.5 秒。

---

# 前一轮交接：提取文字（OCR）与安装程序

2026-10-28。接续 Windows 交互批次，同一分支 `feature/windows-interactions`，仍然只改 Windows 版。用户不再要求提交，改为要一份可试用的 exe 安装程序，同时补上先前搁置的 OCR。

OCR 走 Windows 自带引擎 `Windows.Media.Ocr`，代价是 TargetFramework 必须带 SDK 版本号才能启用 WinRT 投影。选 `net10.0-windows10.0.19041.0`：19041 是仍完整包含所需 API 的最低版本，产物因此在较老系统上依然可运行。

三个只有跑起来才能发现的问题：

1. **汉字被拆开。** Windows 引擎把每个汉字当作独立单词，直接按单词加空格拼会得到"晾 衣 绳"；反过来"晾衣绳2026"又缺了混排惯例里的空格。最终规则是逐字符边界判断：两侧都是 CJK 才不加空格。
2. **语言包不能假定。** Windows 只装用户勾选过的识别语言，一个都没装是常见状态。语言列表改为运行时读取；一台都没有时菜单明说，并提出打开 Windows 语言设置。
3. **WinRT 命名空间撞车。** 在 `namespace Clothesline.Windows` 里写 `Windows.Media.Ocr` 会解析到应用自己的命名空间，必须经 `global::` 访问。

安装程序：Inno Setup 7 便携解压（本机非管理员，装不进 Program Files），脚本 `scripts/windows-installer.iss`，构建脚本 `scripts/build-windows-installer.ps1`。自包含单文件发布，54 MB 安装包；默认装到当前用户 Programs 免管理员，以管理员运行可改为整机安装；卸载保留 `%LOCALAPPDATA%\Clothesline` 下的设置。

本机实测发现这台机器**先前已装有官方 Clothesline Windows 0.1.0-rc.1**，且两个版本叠在同一目录。已卸载双方、清理孤儿文件后干净重装一份，用户设置保留。这一改动发生在用户机器上而非沙箱，需明确告知。

验证（本机，.NET 10.0.403 SDK）：Release 编译 0 错 0 警；Clothesline.Tests 通过；自检 9 项全通过，新增的 OCR 项渲染已知文字并真正识别，报告"3 Han chars，语言 zh-Hans-CN"。**发布产物与已安装产物各自独立跑通全部自检**，已安装的程序可正常启动（PID 稳定、响应正常）。

待验收：OCR 对真实截图/照片的准确率（Windows 决定，非本代码可控）；手写体与低对比度图大概率不准。

---

# 此前交接：Windows 交互批次

2026-10-28。本轮只改 Windows 版（Sources/ 未动），落在分支 `feature/windows-interactions`，起点 45ba96c。用户以截图给出 14 项期望交互，经确认后本轮实现其中 9 项：全局热键、卡片固定/取下按钮、单击复制并浮现"已复制"、右键菜单扩充、Ctrl 拖动移动、1.8 秒收回、剪贴板挂载默认开、全屏隐身、资源管理器右键菜单脚本。

用户明确要求本轮**不做**：「取下（剪切原文件）」与「移动到文件夹…」（用户另有更好的交互设想）。OCR 提取文字当时因需改 TargetFramework 而隔离，已在上一节落地。

关键技术结论（已实测，非推断）：WPF `DataObject` 只有在 `Preferred DropEffect` 以 **MemoryStream** 写入时才会渲染为 HGLOBAL DWORD；写入 `byte[]` 会变成垃圾值，Explorer 只会复制不会移动。这条已固化为自检项 `ctrl-drag move marker`，防止日后被无意改回 `byte[]` 而静默退化为复制。

安全边界不变：×、取下、清空仍然只改展示状态，绝不删除或移动原文件。唯一会改动原位置的路径是按住 Ctrl 拖动，且由目标程序执行；卡片仅在原文件确实消失后才移除。

验证（本机，.NET 10.0.403 SDK 装在 %LOCALAPPDATA%\Microsoft\dotnet-sdk）：Release 编译 0 错误 0 警告；`Clothesline.Tests` 全部通过；`Clothesline.exe --self-test` 8 项全通过（含新增 2 项）。**未做**桌面 UI 人工验收——悬浮窗口、热键实际手感、全屏隐身与资源管理器拖放的真实结果都需要用户在本机试用确认。

下一步：在本机运行 Release 构建试用上述交互；确认 Ctrl 拖到资源管理器是否真的移动文件；确认 Ctrl+Alt+↑ 未被其他程序（如英特尔显卡快捷键）占用；试用后决定是否合并分支。

---

# 此前交接：文件夹单击直接打开

2026-10-27（Asia/Shanghai）。用户反馈文件夹单击进入 Quick Look 介绍，现将 GrabArea 单击分派按 isDirectory 区分：目录调用 Line.open，普通文件仍调用 Line.quickLook。复用原有可访问性检查；双击、拖出、取下和文件安全规则不变。双语 README 同步，ROADMAP 记录鼠标验收待办。

验证：scripts/test-core.sh、scripts/test-macos.sh、scripts/build-app.sh release 全部通过。Release 产物 build/Clothesline.app 包含 arm64 与 x86_64；构建完成签名。macOS 回归仍输出既有粘贴板/CoreDrag/颜色空间诊断，不影响通过。未进行实际鼠标单击 UI 验收；Windows 未改动；未发行新 DMG，GitHub 旧安装包不含本次修复。

此前 fetch 因自动审批服务使用额度限制未执行；本轮重试审批恢复，git fetch origin 成功，起始 HEAD 与 origin/main 均为 d48feca，无分叉。完成提交后推送 main 并核对 SHA；如同步失败须另记结果。

下一步：启动本地新构建，点击文件夹确认 Finder 打开内部，点击普通文件确认 Quick Look 预览；验收后按用户发行要求更新安装包。
