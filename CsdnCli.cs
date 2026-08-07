using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

/// <summary>
/// CSDN 发帖命令行模式：供自动化流程调用，完成扫码登录、发布、更新、查询。
/// 用法：DeepSeekUsageTray.exe csdn <check|login|logout|list|view|publish|update> [--key value ...]
/// </summary>
internal static class CsdnCli
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var command = args[0];
            var opts = ParseOptions(args.Skip(1).ToArray());
            switch (command)
            {
                case "check":
                    return await CheckAsync();
                case "login":
                    return await LoginAsync(opts);
                case "logout":
                    return Logout();
                case "list":
                    return await ListAsync();
                case "view":
                    return await ViewAsync(opts);
                case "upload":
                    return await UploadAsync(opts);
                case "publish":
                    return await PublishAsync(opts, update: false);
                case "update":
                    return await PublishAsync(opts, update: true);
                default:
                    Console.Error.WriteLine("未知命令: " + command);
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("操作失败: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> CheckAsync()
    {
        var session = CsdnSession.Load();
        if (!session.HasLogin)
        {
            Console.WriteLine("未登录");
            return 0;
        }

        var (ok, userName, nick) = await new CsdnClient(session).CheckLoginAsync();
        if (ok)
        {
            session.UserName = userName;
            session.UserNick = nick;
            session.Save();
            var display = string.IsNullOrWhiteSpace(nick) ? userName : nick;
            Console.WriteLine("已登录: " + display + "（" + userName + "）");
        }
        else
        {
            Console.WriteLine("会话已失效，需要重新扫码登录（csdn login）");
        }
        return 0;
    }

    private static async Task<int> LoginAsync(Dictionary<string, string> opts)
    {
        var qrMode = opts.TryGetValue("qr", out var qr) && qr == "true";
        var generateOnly = opts.TryGetValue("generate-only", out var go) && go == "true";
        var pollKeyFile = opts.TryGetValue("poll-key-file", out var pk) ? pk : null;
        var hasPollFile = !string.IsNullOrWhiteSpace(pollKeyFile) && File.Exists(pollKeyFile);
        if (!qrMode && !generateOnly && !hasPollFile)
        {
            return LoginForm(opts);
        }

        var force = opts.TryGetValue("force", out var f) && f == "true";
        if (!force && !hasPollFile)
        {
            var existing = CsdnSession.Load();
            if (existing.HasLogin)
            {
                var (ok, _, _) = await new CsdnClient(existing).CheckLoginAsync();
                if (ok)
                {
                    Console.WriteLine("已登录，无需重复扫码（如需重新登录请加 --force true）");
                    return 0;
                }
            }
        }

        var container = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = container };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ChromeUa);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://passport.csdn.net/login");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://passport.csdn.net");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        string sceneId;
        if (hasPollFile)
        {
            sceneId = (await File.ReadAllTextAsync(pollKeyFile!)).Trim();
            Console.WriteLine("等待手机扫码确认…（请用 CSDN App / 微信扫描之前显示的二维码）");
        }
        else
        {
            var outPath = opts.TryGetValue("out", out var o) ? o : "csdn-login-qr.jpg";
            var keyFile = opts.TryGetValue("key-file", out var k) ? k : null;
            Console.WriteLine("正在获取登录二维码…");
            sceneId = await GenerateQrAsync(http, outPath, keyFile);
            Console.WriteLine("二维码已保存: " + Path.GetFullPath(outPath));
            if (generateOnly)
            {
                Console.WriteLine("扫码完成后运行: csdn login --poll-key-file " + (keyFile ?? "sceneId.txt"));
                return 0;
            }
            Console.WriteLine("请用 CSDN App 或微信扫码并确认登录…");
        }

        var deadline = DateTime.Now.AddMinutes(2);
        var scanned = false;
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(2000);
            var (status, code, message) = await CheckScanAsync(http, sceneId);
            if (status || code == "200")
            {
                scanned = true;
                break;
            }
            if (!string.IsNullOrWhiteSpace(message) && code != "1070")
            {
                Console.WriteLine("扫码状态: " + code + " " + message);
            }
        }

        if (!scanned)
        {
            throw new InvalidOperationException("等待扫码超时（2 分钟），请重新运行 csdn login --qr");
        }

        Console.WriteLine("扫码成功，正在完成登录…");
        var newSession = await DoLoginAsync(http, container, sceneId);
        var client = new CsdnClient(newSession);
        var (okFinal, userName, nick) = await client.CheckLoginAsync();
        if (okFinal)
        {
            newSession.UserName = userName;
            newSession.UserNick = nick;
        }
        newSession.Save();
        Console.WriteLine("✓ 登录成功: " + (string.IsNullOrWhiteSpace(nick) ? userName : nick));
        return 0;
    }

    private static int LoginForm(Dictionary<string, string> opts)
    {
        var force = opts.TryGetValue("force", out var f) && f == "true";
        var session = CsdnSession.Load();
        if (!force && session.HasLogin)
        {
            Console.WriteLine("已保存登录状态（如需重新扫码请加 --force true）");
            return 0;
        }

        using var form = new CsdnLoginForm();
        Application.Run(form);
        if (form.Saved)
        {
            var saved = CsdnSession.Load();
            var name = string.IsNullOrWhiteSpace(saved.UserNick) ? saved.UserName : saved.UserNick;
            Console.WriteLine("✓ 登录成功" + (string.IsNullOrWhiteSpace(name) ? "" : ": " + name));
            return 0;
        }

        Console.Error.WriteLine("未保存登录状态");
        return 1;
    }

    private static async Task<string> GenerateQrAsync(HttpClient http, string outPath, string? keyFile)
    {
        using var content = new StringContent("{}", Encoding.UTF8);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=utf-8");
        using var response = await http.PostAsync(
            "https://passport.csdn.net/v1/register/pc/wxapplets/createQrCode",
            content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("qrCodeUrl", out var qrEl))
        {
            throw new InvalidOperationException("获取二维码失败: " + json);
        }

        var sceneId = data.TryGetProperty("sceneId", out var s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            throw new InvalidOperationException("二维码返回缺少 sceneId: " + json);
        }

        var qrDataUri = qrEl.GetString() ?? "";
        var comma = qrDataUri.IndexOf(',');
        var b64 = comma >= 0 ? qrDataUri[(comma + 1)..] : qrDataUri;
        File.WriteAllBytes(outPath, Convert.FromBase64String(b64));

        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            await File.WriteAllTextAsync(keyFile, sceneId);
        }
        return sceneId;
    }

    private static async Task<(bool Status, string Code, string Message)> CheckScanAsync(
        HttpClient http,
        string sceneId)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { sceneId }), Encoding.UTF8);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=utf-8");
        using var response = await http.PostAsync(
            "https://passport.csdn.net/v1/register/pc/wxapplets/checkScan",
            content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True;
        var code = ReadCode(root);
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        return (status, code, message);
    }

    private static async Task<CsdnSession> DoLoginAsync(
        HttpClient http,
        CookieContainer container,
        string sceneId)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { sceneId }), Encoding.UTF8);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=utf-8");
        using var response = await http.PostAsync(
            "https://passport.csdn.net/v1/register/pc/wxapplets/doLogin",
            content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = ReadCode(root);
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (code is not ("200" or "0"))
        {
            throw new InvalidOperationException(
                "扫码登录失败（" + code + "）：" + (string.IsNullOrWhiteSpace(message) ? json : message));
        }

        // 跟随 redirectUrl 拉取完整登录 Cookie（如 UserInfo / UserToken）
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("redirectUrl", out var url) &&
            url.GetString() is { Length: > 0 } raw)
        {
            var redirect = raw;
            if (!redirect.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !redirect.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                redirect = "https://passport.csdn.net" + (redirect.StartsWith("/", StringComparison.Ordinal) ? "" : "/") + redirect;
            }
            try
            {
                await http.GetAsync(redirect);
            }
            catch
            {
                // 跟随跳转失败不影响已写入的 Cookie
            }
        }

        var newSession = new CsdnSession
        {
            SavedAt = DateTime.Now
        };
        foreach (var uri in new[]
                 {
                     new Uri("https://passport.csdn.net"),
                     new Uri("https://www.csdn.net"),
                     new Uri("https://blog.csdn.net"),
                     new Uri("https://mp.csdn.net"),
                     new Uri("https://editor.csdn.net"),
                     new Uri("https://me.csdn.net"),
                     new Uri("https://bizapi.csdn.net"),
                     new Uri("https://blog-console-api.csdn.net")
                 })
        {
            foreach (Cookie cookie in container.GetCookies(uri))
            {
                newSession.Cookies[cookie.Name] = cookie.Value;
            }
        }

        if (newSession.Cookies.Count < 3)
        {
            throw new InvalidOperationException("登录成功但未获取到登录 Cookie，请重试");
        }
        return newSession;
    }

    private static string ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeEl))
        {
            return "";
        }
        if (codeEl.ValueKind == JsonValueKind.String)
        {
            return codeEl.GetString() ?? "";
        }
        if (codeEl.ValueKind == JsonValueKind.Number)
        {
            return codeEl.GetInt32().ToString();
        }
        return "";
    }

    private static int Logout()
    {
        CsdnSession.Load().Clear();
        Console.WriteLine("已清除 CSDN 登录状态");
        return 0;
    }

    private static async Task<int> ListAsync()
    {
        var session = RequireLogin();
        var list = await new CsdnClient(session).ListArticlesAsync();
        if (list.Count == 0)
        {
            Console.WriteLine("没有拉到文章列表（可能账号下还没有公开文章）");
            return 0;
        }

        foreach (var article in list)
        {
            var url = string.IsNullOrWhiteSpace(article.Url) ? "" : "  " + article.Url;
            Console.WriteLine(article.Id + "  " + article.Title + url);
        }
        return 0;
    }

    private static async Task<int> ViewAsync(Dictionary<string, string> opts)
    {
        var session = RequireLogin();
        var aid = ParseAid(opts.GetValueOrDefault("aid", ""));
        if (aid <= 0)
        {
            throw new InvalidOperationException("缺少 --aid（文章 ID 或文章 URL）");
        }

        var client = new CsdnClient(session);
        var (ok, userName, _) = await client.CheckLoginAsync();
        if (!ok || string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException("无法确认账号名，登录可能已失效");
        }

        var title = await client.GetArticleTitleAsync(userName, aid);
        Console.WriteLine("ID: " + aid);
        Console.WriteLine("标题: " + (string.IsNullOrWhiteSpace(title) ? "（未能读取）" : title));
        Console.WriteLine("URL: https://blog.csdn.net/" + userName + "/article/details/" + aid);
        return 0;
    }

    private static async Task<int> UploadAsync(Dictionary<string, string> opts)
    {
        var session = RequireLogin();
        var file = opts.GetValueOrDefault("file", "");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            throw new InvalidOperationException("缺少 --file 或找不到图片文件: " + file);
        }

        Console.WriteLine("正在上传图片: " + Path.GetFileName(file));
        var url = await new CsdnClient(session).UploadImageAsync(file);
        Console.WriteLine(url);
        return 0;
    }

    private static async Task<int> PublishAsync(Dictionary<string, string> opts, bool update)
    {
        var session = RequireLogin();
        var client = new CsdnClient(session);

        var title = opts.GetValueOrDefault("title", "").Trim();
        var contentPath = opts.GetValueOrDefault("content", "");
        var draftMode = opts.TryGetValue("draft", out var d) && d == "true";
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("缺少 --title");
        }
        if (!File.Exists(contentPath))
        {
            throw new InvalidOperationException("找不到正文文件: " + contentPath);
        }

        long aid = 0;
        if (update)
        {
            aid = ParseAid(opts.GetValueOrDefault("aid", ""));
            if (aid <= 0)
            {
                throw new InvalidOperationException("更新需要 --aid（文章 ID 或文章 URL）");
            }
        }

        var contentDir = Path.GetDirectoryName(Path.GetFullPath(contentPath)) ?? ".";
        var markdown = await File.ReadAllTextAsync(contentPath);
        var draft = new CsdnDraft
        {
            Title = title,
            Description = opts.GetValueOrDefault("description", "").Trim(),
            Tags = opts.GetValueOrDefault("tags", "").Trim(),
            ContentHtml = await MarkdownToHtml.ConvertAsync(
                markdown,
                src => ResolveImageAsync(client, src, contentDir))
        };

        var coverPath = opts.GetValueOrDefault("cover", "");
        var tempCover = false;
        if (string.IsNullOrWhiteSpace(coverPath))
        {
            // 默认封面：内嵌的鲸鱼 + 文字版（logo-gray-blue.png）
            coverPath = ExtractDefaultCover();
            tempCover = !string.IsNullOrWhiteSpace(coverPath);
        }
        if (!string.IsNullOrWhiteSpace(coverPath) &&
            !string.Equals(coverPath, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(coverPath))
            {
                throw new InvalidOperationException("找不到封面图片: " + coverPath);
            }
            try
            {
                Console.WriteLine("正在上传封面: " + Path.GetFileName(coverPath));
                draft.Cover = await client.UploadImageAsync(coverPath);
                Console.WriteLine("封面上传成功: " + draft.Cover);
            }
            finally
            {
                if (tempCover)
                {
                    try
                    {
                        File.Delete(coverPath);
                    }
                    catch
                    {
                        // 临时封面删不掉不影响主流程
                    }
                }
            }
        }

        var articleId = await client.SaveArticleAsync(draft, draftMode, aid);
        if (articleId <= 0)
        {
            throw new InvalidOperationException("CSDN 未返回文章 ID，发布失败");
        }

        session.LastArticleId = articleId;
        session.LastTitle = draft.Title;
        session.Save();

        var label = update ? "文章更新成功" : draftMode ? "草稿保存成功" : "文章发布成功";
        Console.WriteLine("✓ " + label + ": " + articleId);
        if (!string.IsNullOrWhiteSpace(session.UserName))
        {
            Console.WriteLine("https://blog.csdn.net/" + session.UserName + "/article/details/" + articleId);
        }
        return 0;
    }

    private static CsdnSession RequireLogin()
    {
        var session = CsdnSession.Load();
        if (!session.HasLogin && session.Cookies.Count < 5)
        {
            throw new InvalidOperationException("未登录 CSDN，请先运行 csdn login 扫码登录");
        }
        return session;
    }

    private static async Task<string> ResolveImageAsync(CsdnClient client, string src, string baseDir)
    {
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return src;
        }

        var full = Path.GetFullPath(Path.Combine(baseDir, src.Trim().TrimStart('/')));
        if (!File.Exists(full))
        {
            throw new InvalidOperationException("找不到要上传的图片: " + full);
        }
        Console.WriteLine("正在上传图片: " + Path.GetFileName(full));
        var url = await client.UploadImageAsync(full);
        Console.WriteLine("图片已上传: " + url);
        return url;
    }

    private static long ParseAid(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var tail = value.TrimEnd('/').Split('/').LastOrDefault();
            return long.TryParse(tail, out var id) ? id : 0;
        }
        return long.TryParse(value, out var direct) ? direct : 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                result[key[..eq]] = key[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = "true";
            }
        }
        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            CSDN 发帖命令行:
              csdn check                         检查登录状态
              csdn login [--force true]          打开网页窗口登录（扫码/手机号）
              csdn login --qr [--out qr.jpg]     生成二维码直接扫码登录
              csdn login --generate-only --out qr.jpg --key-file scene.txt
                                                  只生成二维码，扫码后由 --poll-key-file 完成登录
              csdn login --poll-key-file scene.txt  轮询扫码状态并完成登录
              csdn logout                        清除登录状态
              csdn list                          列出已发布文章
              csdn view --aid <id或URL>           查看文章信息
              csdn upload --file <图片>            上传一张图片，返回 CDN 链接
              csdn publish --title <标题> --content <正文.md> [--tags <a,b>] [--description <摘要>] [--cover <封面图>] [--draft true]
              csdn update --aid <id或URL> --title <标题> --content <正文.md> [--tags ...] [--description ...] [--cover <封面图>]
            """); 
    }

    /// <summary>把内嵌的 CSDN 默认封面释放到临时目录，返回临时文件路径。</summary>
    private static string ExtractDefaultCover()
    {
        try
        {
            var asm = typeof(CsdnCli).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("csdn-default-cover.png", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                return "";
            }
            var tmp = Path.Combine(Path.GetTempPath(), "csdn-default-cover-" + Guid.NewGuid().ToString("N") + ".png");
            using var src = asm.GetManifestResourceStream(name);
            if (src == null)
            {
                return "";
            }
            using var dst = File.Create(tmp);
            src.CopyTo(dst);
            return tmp;
        }
        catch
        {
            return "";
        }
    }
}
