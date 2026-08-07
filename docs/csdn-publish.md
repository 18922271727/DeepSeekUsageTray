# CSDN 自动发帖 · 调研结论（2026-08-07）

> 结论：**可行，推荐走接口直发**（和 B站 自动化同一套模式：扫码登录 → 保存 Cookie → 本地签名直连 CSDN 创作后台接口）。
> 不推荐浏览器自动化（Playwright/Selenium），CSDN 的富文本编辑器是 Vue/Shadow DOM，注入不稳定且慢。

## 现状核实

- CSDN **没有**正式对外开放的文章发布 API。创作后台的保存动作走内部接口。
- 内部接口目前（2026-08）仍在线：无 Cookie 请求返回 `401`（而非 404/403 WAF），说明路由存在、只等鉴权。
- 已有多个仍在维护的参考实现：Typora `article_uploader` 插件（仓库最近一次提交 2026-08-06，CSDN 就是逆向接口直发）；2026-07 的 Cursor MCP 实战文章也在用同一接口。
- 有人提到"CSDN 公开 Open API 于 2026 年初废弃"，指的不是这个内部接口，而是早期开放的 `open.csdn.net` 网关；内部创作接口不受影响。

## 接口清单

### 1. 登录（扫码）

- 打开 `https://passport.csdn.net/login`，支持手机号验证码 / 微信扫码 / CSDN App 扫码。
- 我们的程序用 WebView2 内嵌登录页（和 B站登录同一个组件思路），用户扫码后从 WebView2 提取 Cookie 保存到本地 `%APPDATA%\DeepSeekUsageTray\csdn-session.json`。
- Cookie 有效期约一个月；失效特征：接口返回 403（WAF）或 401，需重新扫码。

### 2. 发文章（第 1 步：创建/保存文章）

```
POST https://bizapi.csdn.net/blog-console-api/v1/postedit/saveArticle
```

请求头（注意顺序与精确值，签名与 WAF 都依赖它们）：

| Header | 值 |
|---|---|
| accept | `application/json, text/plain, */*` |
| content-type | `application/json;`（带分号，别去掉） |
| cookie | 登录后的完整 Cookie |
| origin / referer | `https://mp.csdn.net` / `https://mp.csdn.net/mp_blog/creation/editor?not_checkout=1` |
| x-ca-key | `203803574`（固定） |
| x-ca-nonce | UUID v4（每次请求随机生成） |
| x-ca-signature | 见下方签名算法 |
| x-ca-signature-headers | `x-ca-key,x-ca-nonce` |
| user-agent | 完整 Chrome UA（WAF 按浏览器指纹放行） |

请求体（JSON）：

```json
{
  "article_id": "",
  "title": "标题",
  "description": "摘要",
  "content": "<p>HTML 正文（由 Markdown 渲染）</p>",
  "tags": "DeepSeek,工具",
  "categories": "",
  "type": "original",
  "status": 0,
  "read_type": "public",
  "reason": "",
  "original_link": "",
  "authorized_status": false,
  "check_original": false,
  "source": "pc_postedit",
  "not_auto_saved": 1,
  "creator_activity_id": "",
  "cover_images": [],
  "cover_type": 1,
  "vote_id": 0,
  "resource_id": "",
  "scheduled_time": 0,
  "is_new": 1
}
```

返回：`data.article_id`。

### 3. 发文章（第 2 步：保存历史版本/上线）

```
POST https://bizapi.csdn.net/blog/phoenix/console/v1/history-version/save
```

请求体：

```json
{
  "articleId": "<上一步返回的 article_id>",
  "title": "标题",
  "content": "<HTML 正文>",
  "type": 3
}
```

签名方式与第 1 步相同（换 nonce，path 换成 `/blog/phoenix/console/v1/history-version/save`）。

### 4. 图片上传（正文里的截图）

```
POST https://blog-console-api.csdn.net/v1/upload/img?shuiyin=2
```

- multipart/form-data，字段名 `file`，返回 `data.url`（形如 `https://img-blog.csdnimg.cn/xxx.png`）。
- 仅需 Cookie（2021 年验证过无需签名头；实现时需实测，若被 WAF 拦则补浏览器指纹头）。
- 发布前把 Markdown 里的本地图片路径逐个上传并替换成 CDN 链接。

### 5. 登录状态检查 / 读取

- `POST https://me.csdn.net/api/user/show` → 返回昵称/文章数（登录校验）。
- 列出/查看已发文章：`https://blog-console-api.csdn.net/v1/...` 系列（实现时按需抓包补充，读取不涉及签名）。

## 签名算法（x-ca-signature）

CSDN 前端逆向出来的算法（Typora 插件源码 + 多个 Java/JS 复现一致）：

```
签名串 =
  "POST\n" +
  "application/json, text/plain, */*\n" +     // accept 原样
  "\n" +                                       // 空 query string
  "application/json;\n" +                      // content-type 原样（带分号）
  "\n" +                                       // 空 body hash
  "x-ca-key:203803574\n" +
  "x-ca-nonce:" + uuid + "\n" +
  "/blog-console-api/v1/postedit/saveArticle"  // 仅 pathname

signature = Base64( HMAC-SHA256(签名串, ekey) )
```

- `ekey = 9znpamsyl2c7cdrr9sas0le9vbc3r6ba`（HMAC 密钥，逆向自 CSDN 前端 webpack，2026-03 前一直硬编码在开源插件里；CSDN 若轮换此密钥，发布会失败，需重新逆向）
- `x-ca-key = 203803574`（固定 access key）
- 换行符是 `\n`，大小写、顺序一个都不能错；出错服务端返回 `HMAC signature does not match`。

C# 实现：`HMACSHA256`（System.Security.Cryptography）+ `Convert.ToBase64String`，没有难点。

## 风险与边界

- **非官方接口**：CSDN 改版可能临时失效，没有 SLA；好在插件社区一直在维护，前端一改就有人更新。
- **风控**：每日发布上限约 10 篇；高频连续发布会触发"频繁操作"提示，建议发布间隔 >1 分钟。
- **Cookie 有效期约一个月**；WAF 需要完整浏览器指纹头，403 即视为登录失效，重新扫码，不要重试硬刷。
- **敏感数据**：Cookie 和 HMAC 密钥都属于账号凭据，只存本机 `%APPDATA%`，禁止提交 Git。
- **草稿先行**：`status=2` / 草稿模式安全，首次联调先用草稿验证签名，再公开发布。

## 实现方案（与 B站 对齐）

在 DeepSeekUsageTray 里新增一组模块：

| 文件 | 职责 |
|---|---|
| `CsdnSession.cs` | 保存/加载/校验 Cookie（JSON 存 `%APPDATA%\DeepSeekUsageTray\csdn-session.json`） |
| `CsdnClient.cs` | 签名、saveArticle、history-version/save、图片上传、whoami、list/view |
| `CsdnLoginForm.cs` | WebView2 内嵌 passport.csdn.net 扫码登录，完成后提取 Cookie |
| `CsdnCli.cs` | 命令行：`csdn check / login / logout / publish / update / list / view` |

发布流程与 B站相同：

1. 草稿写入 `docs/posts/`，`git add + commit + push`，把链接给用户审核；
2. 用户确认"可以发送"后执行 `csdn publish --title ... --content ... --tags ...`（本地图片自动上传替换）；
3. 返回文章 URL（形如 `https://blog.csdn.net/<用户名>/article/details/<id>`）。

## 下一步

1. 实现 `CsdnClient.cs` + `CsdnCli.cs`（签名 + 两步发布 + 图片上传）；
2. 内嵌 WebView2 扫码登录，保存会话；
3. 首次联调：先发 `--draft` 草稿验证签名与字段，通过后再公开；
4. 验证更新已有文章（`article_id` 填入 + `is_new=0`，实现时实测）；
5. 稳定后沉淀成 `csdn-publish` skill（和 `bilibili-publish` 并列），并把本调研文档引用进去。
