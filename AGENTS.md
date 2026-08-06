# AGENTS.md — DeepSeek 用量托盘工具

## 图标 / Logo 使用规则（重要）

涉及鲸鱼图标、logo、封面的任何任务，**先读 `assets/whale-design/README.md`（设计源文件包）**，从原始数据出发，不要对着 PNG 重新描摹。

### 文件地图

| 文件 | 用途 |
|---|---|
| `whale_source.png` | 鲸鱼原始透明原图（1024×1024，AI 生成，保留不改） |
| `whale.png` | 应用标题栏小鲸鱼（512×512，EmbeddedResource） |
| `whale.ico` | 托盘/程序图标（16~256 多尺寸，EmbeddedResource + ApplicationIcon） |
| `assets/logo-with-title.png` | 默认单行文字组合图（透明底） |
| `assets/logo-gray-blue.png` | 灰蓝版两行文字组合图（当前品牌版） |
| `assets/bili-cover.png` | B站 专栏封面 |
| `assets/whale-design/prompt.txt` | 鲸鱼原始 AI 生成提示词 |
| `assets/whale-design/palette.json` | 色板（角色 + HEX） |
| `assets/whale-design/layout*.json` | 排版参数（鲸鱼/文字位置字号颜色） |
| `assets/whale-design/recolor.py` | 换色脚本（按映射 JSON 整体换色） |
| `assets/whale-design/make-logo.ps1` | 重排版脚本（支持背景色、两行文字） |
| `assets/whale-design/recolor-gray-blue.json` | 灰蓝版换色映射 |

### 规则

1. 当前品牌配色（灰蓝版）：鲸鱼身体深蓝 `#3860DA`、肚皮/泡泡浅蓝 `#8BA5F6`、高光白 `#FEFEFE`、背景灰 `#D8D9DA`、文字深蓝 `#3860DA` / 深灰 `#4A4B4F`。
2. 做新配色：在 `assets/whale-design/` 建映射 JSON，然后
   `python recolor.py whale_source.png 输出.png 映射.json`；重新排版用 `make-logo.ps1`。
3. 改程序内图标：更新 `whale.png` / `whale.ico` 后必须重新构建
   `dotnet publish -c Release -o dist`（桌面快捷方式指向 `dist\DeepSeekUsageTray.exe`），并提示用户重启程序生效。
4. 用户反馈桌面快捷方式图标没变：重建 `.lnk`（WScript.Shell，需提权）并运行 `ie4uinit.exe -show` 刷新图标缓存。

## 环境注意

- Python（带 PIL）：`C:\Users\admin\AppData\Local\Programs\Python\Python312\python.exe`；系统默认 python 只有 3.6，不能使用新语法。
- PowerShell 5.1 读 UTF-8 无 BOM 中文会乱码：脚本避免中文字面量，或从 UTF-8 文件读取文本。
- 图像识别：DashScope API（环境变量 `DASHSCOPE_API_KEY`），模型 `qwen3-vl-plus`。
- 用完剪贴板图片、临时请求文件后立即删除。

## 蓝奏云发布（2026-08 新增）

- 登录态：`DeepSeekUsageTray.exe lanzou login`（内嵌网页登录，保存到 `%APPDATA%\DeepSeekUsageTray\lanzou-session.json`，已被 .gitignore 忽略）。
- 登录窗口必须由用户在**自己的桌面**双击 `tools\lanzou-login.cmd` 打开（沙箱里启动的 GUI 用户看不到），登录后点底部蓝色“完成登录并保存”。
- 一键发布：`tools\publish-lanzou.ps1`（打包单文件 exe → zip → 上传蓝奏云 → 更新 `docs/download.md`）。
- 上传脚本：`tools\lanzou_publish_pw.py`（Playwright + 本机 Edge 真实浏览器方案，第三方 lanzou-api 已失效）；补抓链接用 `tools\lanzou_get_share_pw.py`。两者输出 `URL: / PWD: / SIZE_MB:` 供脚本解析。
- 蓝奏云免费账号单文件上限 100MB，zip 通常 40~50MB；不要上传裸 exe（约 109MB）。
