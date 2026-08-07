---
name: csdn-publish
description: 发布和更新 CSDN（CSDN 博客）文章。当用户要求把草稿、README、文章发布到 CSDN，更新已有 CSDN 文章，或说"发CSDN""CSDN发帖""发到CSDN""发博客""发CSDN博客"时使用。流程包含网页扫码/手机号登录、Markdown 转 HTML、本地图片自动上传、默认鲸鱼封面、发布新文章、更新已有文章、列出/查看文章，以及草稿-审核-发布流程（草稿放 git 仓库 docs/posts/，用户审核通过后自动发布）。
---

# CSDN 发帖

## 工具

发帖命令集成在 DeepSeekUsageTray 程序里。改动过代码后先编译最新版本：

```powershell
dotnet publish -c Debug --no-restore -o publish\csdn-dev
```

之后用 `publish\csdn-dev\DeepSeekUsageTray.exe csdn ...` 执行所有发帖操作（任何发布输出目录均可，下文以 csdn-dev 为例）。

## 命令

- `csdn check` — 检查登录状态
- `csdn login [--force true]` — 打开内嵌网页窗口登录（扫码或手机号），完成后自动保存 Cookie
- `csdn logout` — 清除登录状态
- `csdn list` — 列出已发布文章（ID + 标题）
- `csdn view --aid <ID或URL>` — 查看文章信息（标题 + 链接）
- `csdn upload --file <图片>` — 单独上传一张图片，返回 CDN 链接（排查用）
- `csdn publish --title <标题> --content <正文.md> [--tags <a,b>] [--description <摘要>] [--cover <封面图>] [--draft true]` — 发布新文章
- `csdn update --aid <ID或URL> --title <标题> --content <正文.md> [--tags ...] [--description ...] [--cover <封面图>] [--draft true]` — 更新已有文章

## 草稿-审核-发布流程

1. 把草稿写入 `docs/posts/<日期>-<名称>.md`；本地图片放 `docs/screenshots/` 或正文同目录，用相对路径引用（发布时自动上传替换成 CSDN CDN 链接）。
2. `git add` + `git commit`，推送到 GitHub，把文件链接发给用户审核。
3. 用户确认"可以发送"后，运行 `csdn publish`（更新已有文章用 `csdn update --aid <ID> ...`）。
4. 联调/验收阶段加 `--draft true`（保存为草稿，安全）；用户确认无误后去掉 `--draft` 再跑一次即为正式发布。
5. 发布成功后把 `https://blog.csdn.net/<用户名>/article/details/<ID>` 发给用户。

## 登录

- 登录状态存在 `%APPDATA%\DeepSeekUsageTray\csdn-session.json`（含 Cookie，敏感数据，只存本机，不提交 git）。
- 先 `csdn check`；未登录或失效时运行 `csdn login`（内嵌 WebView2 网页窗口，扫码或手机号）。**不要用 `--qr` 二维码模式**：CSDN 二维码只能用 CSDN App/微信小程序扫，用户已否掉该方案。
- Cookie 约一个月有效；接口返回 401/403（WAF）即视为失效，重新登录即可。

## 封面

- 默认封面是内嵌的鲸鱼+文字图（`assets/logo-gray-blue.png`：深蓝鲸鱼/浅蓝肚皮/灰底，两行字"DeepSeek / 计费工具 v0.1"），`csdn publish/update` 不带 `--cover` 时自动使用。
- `--cover <本地图片>` 换其他封面；`--cover none` 不设封面。
- 要改默认封面：改 `assets/logo-gray-blue.png` 后重新编译（改图前先读 `assets/whale-design/README.md`，从设计源数据出发，不要对着 PNG 重画）。

## 注意事项（踩过的坑，别重踩）

- **图片上传走"取签名 → 直传华为云 OBS"两步链路**（`bizapi.csdn.net/resource-api/v1/image/direct/upload/signature` + OBS POST）。旧接口 `blog-console-api.csdn.net/v1/upload/img` 已被 WAF 拦截，返回 HTML 403，别再用。
- **multipart 必须手写**：.NET 的 `MultipartFormDataContent` 会给文件部分加 `filename*=utf-8''` 扩展头，华为云 OBS 不认，报 `POST requires exactly one file upload per request`（ArgumentName=file，ArgumentValue=0）。程序里已手写标准表单，改代码时不要退回 MultipartFormDataContent。
- 发文章/传图需要完整浏览器头（UA/Origin/Referer）+ x-ca 网关签名（含时间戳变体）；程序已封装，正常发帖无需手工抓包。详细接口、签名串、密钥、字段见仓库 `docs/csdn-publish.md`。
- 更新已有文章会重新上传正文里的所有本地图片（每次生成新 CDN 链接），属正常现象。
- 发布成功后 CSDN 对第三方抓取工具可能返回 521/403（风控），用 `csdn view --aid <ID>` 验证公开页面即可，别反复直接抓取。
- 目前没有 `csdn delete` 命令；联调产生的旧草稿让用户在草稿箱手动删除（或先给程序加删除命令）。
- 只用网页登录，不要索要 CSDN 密码。
