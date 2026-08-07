using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

/// <summary>
/// CSDN 发帖命令行模式：供自动化流程调用，完成扫码登录、发布、更新、查询。
/// 用法：DeepSeekUsageTray.exe csdn <check|login|logout|list|view|publish|update> [--key value ...]
/// </summary>
internal static class CsdnCli
{
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
                    return Login(opts);
                case "logout":
                    return Logout();
                case "list":
                    return await ListAsync();
                case "view":
                    return await ViewAsync(opts);
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

    private static int Login(Dictionary<string, string> opts)
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
        if (!session.HasLogin)
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
              csdn login [--force true]          打开网页扫码登录并保存 Cookie
              csdn logout                        清除登录状态
              csdn list                          列出已发布文章
              csdn view --aid <id或URL>           查看文章信息
              csdn publish --title <标题> --content <正文.md> [--tags <a,b>] [--description <摘要>] [--draft true]
              csdn update --aid <id或URL> --title <标题> --content <正文.md> [--tags ...] [--description ...]
            """);
    }
}
