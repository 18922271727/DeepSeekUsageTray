using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeekUsageTray;

public sealed record ArticleCategory(long Id, string Name, string ParentName)
{
    public override string ToString() => $"{ParentName} / {Name}";
}

public sealed class BiliDraft
{
    public string Title { get; set; } = string.Empty;
    public long Category { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string ContentHtml { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
}

public sealed class MyArticle
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public sealed class BiliApiException : Exception
{
    public int Code { get; }

    public BiliApiException(int code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>B站网页版接口客户端：检查登录、传图、发文、更新、列文章。</summary>
internal sealed class BiliClient
{
    private readonly BiliSession _session;
    private readonly HttpClient _http;

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    public BiliClient(BiliSession session)
    {
        _session = session;
        _http = new HttpClient { BaseAddress = new Uri("https://api.bilibili.com") };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ChromeUa);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private void ApplyCookies(HttpRequestMessage request)
    {
        if (_session.HasLogin)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _session.CookieHeader);
        }
    }

    /// <summary>检查登录状态，返回 (是否已登录, 昵称)。</summary>
    public async Task<(bool Ok, string Uname)> CheckLoginAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x/web-interface/nav");
        ApplyCookies(request);
        using var response = await _http.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("isLogin", out var isLogin) && isLogin.GetBoolean())
        {
            var uname = data.TryGetProperty("uname", out var u) ? u.GetString() ?? "" : "";
            return (true, uname);
        }
        return (false, string.Empty);
    }

    /// <summary>拉取专栏分类，返回叶子分类列表。</summary>
    public async Task<List<ArticleCategory>> GetCategoriesAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x/article/categories");
        ApplyCookies(request);
        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());

        var result = new List<ArticleCategory>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var parent in data.EnumerateArray())
        {
            var parentName = parent.TryGetProperty("name", out var p) ? p.GetString() ?? "" : "";
            if (parent.TryGetProperty("children", out var children) &&
                children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    var id = child.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                    var name = child.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (id > 0)
                    {
                        result.Add(new ArticleCategory(id, name, parentName));
                    }
                }
            }
            else
            {
                var id = parent.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                if (id > 0)
                {
                    result.Add(new ArticleCategory(id, parentName, ""));
                }
            }
        }
        return result;
    }

    /// <summary>上传一张图片到 B站，返回站内图片 URL。</summary>
    public async Task<string> UploadImageAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(_session.Csrf))
        {
            throw new InvalidOperationException("缺少登录凭证（bili_jct），请重新登录 B站。");
        }

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_session.Csrf), "csrf");
        var bytes = await File.ReadAllBytesAsync(filePath);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(filePath));
        content.Add(fileContent, "binary", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/x/article/creative/article/upcover")
        {
            Content = content
        };
        ApplyCookies(request);

        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("url", out var url))
        {
            return url.GetString() ?? throw new BiliApiException(0, "图片上传成功但未返回链接。");
        }
        throw new BiliApiException(0, "图片上传失败：返回数据里没有图片链接。");
    }

    /// <summary>发布新文章，成功返回文章 id。</summary>
    public async Task<long> CreateArticleAsync(BiliDraft draft)
    {
        var form = BuildForm(draft);
        var root = await PostFormAsync("/x/article/creative/article/submit", form);
        var id = ExtractArticleId(root);
        if (id > 0)
        {
            return id;
        }

        // 部分版本不返回编号，用列表反查最新一篇同标题文章
        var mine = await ListMyArticlesAsync();
        return mine
            .Where(a => !string.IsNullOrEmpty(draft.Title) &&
                        (a.Title == draft.Title || a.Title.StartsWith(draft.Title, StringComparison.Ordinal)))
            .Select(a => a.Id)
            .FirstOrDefault();
    }

    /// <summary>更新已有文章，成功返回文章 id。</summary>
    public async Task<long> UpdateArticleAsync(long articleId, BiliDraft draft)
    {
        var form = BuildForm(draft, articleId);
        var root = await PostFormAsync("/x/article/creative/article/update", form);
        return ExtractArticleId(root);
    }

    /// <summary>拉取我的专栏文章列表。</summary>
    public async Task<List<MyArticle>> ListMyArticlesAsync()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/x/article/creative/article/list?pn=1&ps=20");
        ApplyCookies(request);
        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());

        var result = new List<MyArticle>();
        if (!root.TryGetProperty("data", out var data))
        {
            return result;
        }

        // 兼容不同字段名：data.list / data.articles / data.items
        var arr = default(JsonElement);
        foreach (var key in new[] { "list", "articles", "items" })
        {
            if (data.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                arr = candidate;
                break;
            }
        }
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arr.EnumerateArray())
        {
            long id = 0;
            foreach (var key in new[] { "id", "article_id", "art_id", "aid" })
            {
                if (item.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    id = idEl.GetInt64();
                    break;
                }
            }
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (id > 0)
            {
                result.Add(new MyArticle { Id = id, Title = title });
            }
        }
        return result;
    }

    private Dictionary<string, string> BuildForm(BiliDraft draft, long articleId = 0)
    {
        var form = new Dictionary<string, string>
        {
            ["title"] = draft.Title,
            ["content"] = draft.ContentHtml,
            ["category"] = draft.Category.ToString(),
            ["reprint"] = "0",
            ["csrf"] = _session.Csrf
        };
        if (!string.IsNullOrWhiteSpace(draft.Summary))
        {
            form["summary"] = draft.Summary;
        }
        if (!string.IsNullOrWhiteSpace(draft.Tags))
        {
            form["tags"] = draft.Tags;
        }
        if (!string.IsNullOrWhiteSpace(draft.Cover))
        {
            form["cover"] = draft.Cover;
        }
        if (articleId > 0)
        {
            form["aid"] = articleId.ToString();
        }
        return form;
    }

    private async Task<JsonElement> PostFormAsync(string path, Dictionary<string, string> form)
    {
        if (string.IsNullOrWhiteSpace(_session.Csrf))
        {
            throw new InvalidOperationException("缺少登录凭证（bili_jct），请重新登录 B站。");
        }
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        ApplyCookies(request);
        using var response = await _http.SendAsync(request);
        return ParseResponse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("code", out var code))
        {
            var codeValue = code.GetInt32();
            if (codeValue != 0)
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                throw new BiliApiException(codeValue, $"B站接口返回错误（{codeValue}）：{message}");
            }
        }
        return root;
    }

    private static long ExtractArticleId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return 0;
        }
        foreach (var key in new[] { "id", "article_id", "aid" })
        {
            if (data.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                return el.GetInt64();
            }
        }
        return 0;
    }

    private static string GuessMime(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
