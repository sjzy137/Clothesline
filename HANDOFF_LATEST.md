# 最新交接：文件夹单击直接打开

2026-10-27（Asia/Shanghai）。用户反馈文件夹单击进入 Quick Look 介绍，现将 GrabArea 单击分派按 isDirectory 区分：目录调用 Line.open，普通文件仍调用 Line.quickLook。复用原有可访问性检查；双击、拖出、取下和文件安全规则不变。双语 README 同步，ROADMAP 记录鼠标验收待办。

验证：scripts/test-core.sh、scripts/test-macos.sh、scripts/build-app.sh release 全部通过。Release 产物 build/Clothesline.app 包含 arm64 与 x86_64；构建完成签名。macOS 回归仍输出既有粘贴板/CoreDrag/颜色空间诊断，不影响通过。未进行实际鼠标单击 UI 验收；Windows 未改动；未发行新 DMG，GitHub 旧安装包不含本次修复。

此前 fetch 因自动审批服务使用额度限制未执行；本轮重试审批恢复，git fetch origin 成功，起始 HEAD 与 origin/main 均为 d48feca，无分叉。完成提交后推送 main 并核对 SHA；如同步失败须另记结果。

下一步：启动本地新构建，点击文件夹确认 Finder 打开内部，点击普通文件确认 Quick Look 预览；验收后按用户发行要求更新安装包。
