using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;

namespace DeepSeekUsageTray;

/// <summary>
/// B站发帖命令行模式：供 Codex skill 直接调用，完成扫码登录、发布、更新、查询。
/// 用法：DeepSeekUsageTray.exe bili <check|login|logout|list|publish|update> [--key value ...]
/// </summary>
internal static class BiliCli
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
                    return await LogoutAsync();
                case "list":
                    return await ListAsync();
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
        var session = BiliSession.Load();
        if (!session.HasLogin)
        {
            Console.WriteLine("未登录");
            return 0;
        }

        var (ok, uname) = await new BiliClient(session).CheckLoginAsync();
        Console.WriteLine(ok ? "已登录: " + uname : "会话已失效，需要重新登录");
        return 0;
    }

    private static async Task<int> LoginAsync(Dictionary<string, string> opts)
    {
        var force = opts.TryGetValue("force", out var f) && f == "true";
        var session = BiliSession.Load();
        if (!force && session.HasLogin)
        {
            var (ok, uname) = await new BiliClient(session).CheckLoginAsync();
            if (ok)
            {
                Console.WriteLine("已登录: " + uname + "（无需重复扫码；如需重新登录请加 --force true）");
                return 0;
            }
        }

        var outPath = opts.TryGetValue("out", out var o) ? o : "bili-login-qr.png";
        var keyFile = opts.TryGetValue("key-file", out var k) ? k : null;
        var pollKeyFile = opts.TryGetValue("poll-key-file", out var pk) ? pk : null;
        var container = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = container,
            AllowAutoRedirect = false
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://passport.bilibili.com") };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ChromeUa);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

        string qrcodeKey;
        var pollingExistingKey = !string.IsNullOrWhiteSpace(pollKeyFile) && File.Exists(pollKeyFile);
        if (pollingExistingKey)
        {
            qrcodeKey = (await File.ReadAllTextAsync(pollKeyFile)).Trim();
        }
        else
        {
            Console.WriteLine("正在生成登录二维码…");
            qrcodeKey = await GenerateQrAsync(http, outPath);
            if (!string.IsNullOrWhiteSpace(keyFile))
            {
                await File.WriteAllTextAsync(keyFile, qrcodeKey);
            }
            if (opts.TryGetValue("generate-only", out var go) && go == "true")
            {
                Console.WriteLine("二维码已生成: " + Path.GetFullPath(outPath));
                Console.WriteLine("扫码完成后运行: bili login --poll-key-file " + keyFile);
                return 0;
            }
        }

        if (pollingExistingKey)
        {
            Console.WriteLine("等待手机扫码确认…（二维码已在前一步生成）");
        }
        else
        {
            Console.WriteLine("二维码已保存: " + Path.GetFullPath(outPath));
            Console.WriteLine("请用 B站手机 App 扫码并确认登录…");
        }

        var deadline = DateTime.Now.AddMinutes(2);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(2000);
            string pollJson;
            using (var request = new HttpRequestMessage(
                       HttpMethod.Get,
                       "/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(qrcodeKey)))
            {
                using var response = await http.SendAsync(request);
                pollJson = await response.Content.ReadAsStringAsync();
            }

            using var pollDoc = JsonDocument.Parse(pollJson);
            var root = pollDoc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code == 0)
            {
                break;
            }

            if (code == 86038)
            {
                Console.WriteLine("二维码已过期，请重新运行 bili login");
                return 1;
            }
        }

        var cookies = container.GetCookies(new Uri("https://passport.bilibili.com"));
        var newSession = new BiliSession();
        foreach (Cookie cookie in cookies)
        {
            newSession.Cookies[cookie.Name] = cookie.Value;
        }

        if (!newSession.HasLogin)
        {
            throw new InvalidOperationException("登录成功但未获取到 SESSDATA，请重试");
        }

        newSession.Save();
        var (okFinal, unameFinal) = await new BiliClient(newSession).CheckLoginAsync();
        Console.WriteLine(okFinal ? "✓ 登录成功: " + unameFinal : "✓ 已保存登录状态");
        return 0;
    }

    private static async Task<string> GenerateQrAsync(HttpClient http, string outPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x/passport-login/web/qrcode/generate");
        using var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("qrcode_key", out var keyEl) ||
            !data.TryGetProperty("url", out var urlEl))
        {
            throw new InvalidOperationException("获取二维码失败: " + json);
        }

        var qrcodeKey = keyEl.GetString() ?? "";
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(urlEl.GetString() ?? "", QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData);
        File.WriteAllBytes(outPath, png.GetGraphic(6));
        return qrcodeKey;
    }

    private static async Task<int> LogoutAsync()
    {
        var session = BiliSession.Load();
        session.Clear();
        Console.WriteLine("已清除 B站登录状态");
        return 0;
    }

    private static async Task<int> ListAsync()
    {
        var session = RequireLogin();
        var list = await new BiliClient(session).ListMyArticlesAsync();
        if (list.Count == 0)
        {
            Console.WriteLine("还没有发布过专栏文章");
            return 0;
        }

        foreach (var article in list)
        {
            Console.WriteLine($"cv{article.Id}  {article.Title}");
        }
        return 0;
    }

    private static async Task<int> PublishAsync(Dictionary<string, string> opts, bool update)
    {
        var session = RequireLogin();
        var client = new BiliClient(session);

        var title = opts.GetValueOrDefault("title", "").Trim();
        var contentPath = opts.GetValueOrDefault("content", "");
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
            aid = ParseCv(opts.GetValueOrDefault("aid", ""));
            if (aid <= 0)
            {
                throw new InvalidOperationException("更新需要 --aid（cv 编号，例如 cv123456 或 123456）");
            }
        }

        var categoryId = await ResolveCategoryAsync(client, opts.GetValueOrDefault("category", ""));
        var contentDir = Path.GetDirectoryName(Path.GetFullPath(contentPath)) ?? ".";
        var markdown = await File.ReadAllTextAsync(contentPath);
        var draft = new BiliDraft
        {
            Title = title,
            Category = categoryId,
            Summary = opts.GetValueOrDefault("summary", "").Trim(),
            Tags = opts.GetValueOrDefault("tags", "").Trim(),
            ContentHtml = await MarkdownToHtml.ConvertAsync(
                markdown,
                src => ResolveImageAsync(client, src, contentDir))
        };

        var articleId = update
            ? await client.UpdateArticleAsync(aid, draft)
            : await client.CreateArticleAsync(draft);

        session.ArticleId = articleId;
        session.LastTitle = draft.Title;
        session.Save();

        Console.WriteLine("✓ " + (update ? "文章更新成功" : "文章发布成功") + ": cv" + articleId);
        Console.WriteLine("https://www.bilibili.com/read/cv" + articleId);
        return 0;
    }

    private static BiliSession RequireLogin()
    {
        var session = BiliSession.Load();
        if (!session.HasLogin)
        {
            throw new InvalidOperationException("未登录 B站，请先运行 bili login 扫码登录");
        }
        return session;
    }

    private static async Task<long> ResolveCategoryAsync(BiliClient client, string wanted)
    {
        var categories = await client.GetCategoriesAsync();
        if (categories.Count == 0)
        {
            throw new InvalidOperationException("获取 B站专栏分类失败");
        }

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            if (long.TryParse(wanted, out var id))
            {
                var byId = categories.FirstOrDefault(c => c.Id == id);
                if (byId != null)
                {
                    return byId.Id;
                }
            }

            var byName = categories.FirstOrDefault(c =>
                c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                c.ParentName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName.Id;
            }
        }

        var preferred = categories.FirstOrDefault(c => c.ParentName == "科技") ?? categories.FirstOrDefault();
        if (preferred == null)
        {
            throw new InvalidOperationException("没有可用的专栏分类");
        }
        Console.WriteLine("使用分类: " + preferred);
        return preferred.Id;
    }

    private static async Task<string> ResolveImageAsync(BiliClient client, string src, string baseDir)
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

    private static long ParseCv(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        if (value.StartsWith("cv", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        return long.TryParse(value, out var id) ? id : 0;
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
            B站发帖命令行:
              bili check                         检查登录状态
              bili login [--out qr.png] [--force true] [--generate-only --key-file key.txt | --poll-key-file key.txt]   生成二维码登录
              bili logout                        清除登录状态
              bili list                          列出已发布文章
              bili publish --title <标题> --content <正文.md> [--category <科技|id>] [--tags <a,b>] [--summary <摘要>]
              bili update --aid <cv编号> --title <标题> --content <正文.md> [--category ...] [--tags ...] [--summary ...]
            """);
    }
}
