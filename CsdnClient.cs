using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeekUsageTray;

/// <summary>一次 CSDN 发布/更新的文章内容。</summary>
public sealed class CsdnDraft
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string ContentHtml { get; set; } = string.Empty;
}

public sealed class CsdnArticle
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class CsdnApiException : Exception
{
    public int Code { get; }

    public CsdnApiException(int code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// CSDN 网页版创作后台接口客户端：登录检查、传图、发文、更新、列文章。
/// 签名算法逆向自 CSDN 前端，详见 docs/csdn-publish.md。
/// </summary>
internal sealed class CsdnClient
{
    private const string XCaKey = "203803574";
    private const string EKey = "9znpamsyl2c7cdrr9sas0le9vbc3r6ba";
    private const string AcceptJson = "application/json, text/plain, */*";
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    private readonly CsdnSession _session;
    private readonly HttpClient _http = new();

    public CsdnClient(CsdnSession session)
    {
        _session = session;
    }

    private void ApplyCookies(HttpRequestMessage request)
    {
        if (_session.HasLogin)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _session.CookieHeader);
        }
    }

    /// <summary>计算 CSDN 接口签名（x-ca-signature）。</summary>
    private static string Sign(string path, string nonce)
    {
        var toSign = "POST\n" + AcceptJson + "\n\napplication/json;\n\n" +
                     "x-ca-key:" + XCaKey + "\n" +
                     "x-ca-nonce:" + nonce + "\n" +
                     path;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(EKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>检查登录状态，返回 (是否登录, 账号名, 昵称)。</summary>
    public async Task<(bool Ok, string UserName, string Nick)> CheckLoginAsync()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            try
            {
                using var request = new HttpRequestMessage(method, "https://me.csdn.net/api/user/show");
                ApplyCookies(request);
                request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
                request.Headers.TryAddWithoutValidation("referer", "https://me.csdn.net/");
                request.Headers.TryAddWithoutValidation("accept", AcceptJson);
                if (method == HttpMethod.Post)
                {
                    request.Content = new StringContent("{}", Encoding.UTF8);
                }
                using var response = await _http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var userName = GetString(data, "user_name", "username", "userName");
                    if (string.IsNullOrWhiteSpace(userName))
                    {
                        continue;
                    }
                    var nick = GetString(data, "user_nick", "nickname", "nickName");
                    return (true, userName, nick);
                }
            }
            catch
            {
                // 换一种方法再试
            }
        }
        return (false, string.Empty, string.Empty);
    }

    /// <summary>上传一张图片到 CSDN 图床，返回 CDN 链接。</summary>
    public async Task<string> UploadImageAsync(string filePath)
    {
        using var content = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(filePath));
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://blog-console-api.csdn.net/v1/upload/img?shuiyin=2")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
        request.Headers.TryAddWithoutValidation("origin", "https://editor.csdn.net");
        request.Headers.TryAddWithoutValidation("referer", "https://editor.csdn.net/");
        request.Headers.TryAddWithoutValidation("accept", "*/*");
        ApplyCookies(request);

        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("url", out var url) &&
            url.GetString() is { Length: > 0 } value)
        {
            return value;
        }
        throw new CsdnApiException(0, "图片上传失败：返回数据里没有图片链接。");
    }

    /// <summary>
    /// 保存/发布文章。草稿模式只调 saveArticle（status=2）；
    /// 正式发布再补一次 history-version/save 上线。
    /// </summary>
    public async Task<long> SaveArticleAsync(CsdnDraft draft, bool draftMode, long articleId = 0)
    {
        var payload = new Dictionary<string, object?>
        {
            ["article_id"] = articleId > 0 ? articleId.ToString() : "",
            ["title"] = draft.Title,
            ["description"] = draft.Description,
            ["content"] = draft.ContentHtml,
            ["tags"] = draft.Tags,
            ["categories"] = "",
            ["type"] = "original",
            ["status"] = draftMode ? 2 : 0,
            ["read_type"] = "public",
            ["reason"] = "",
            ["original_link"] = "",
            ["authorized_status"] = false,
            ["check_original"] = false,
            ["source"] = "pc_postedit",
            ["not_auto_saved"] = 1,
            ["creator_activity_id"] = "",
            ["cover_images"] = Array.Empty<string>(),
            ["cover_type"] = 1,
            ["vote_id"] = 0,
            ["resource_id"] = "",
            ["scheduled_time"] = 0,
            ["is_new"] = articleId > 0 ? 0 : 1
        };

        var root = await PostSignedJsonAsync(
            "https://bizapi.csdn.net/blog-console-api/v1/postedit/saveArticle",
            payload);
        var id = ExtractArticleId(root);

        if (!draftMode && id > 0)
        {
            var publishPayload = new Dictionary<string, object?>
            {
                ["articleId"] = id,
                ["title"] = draft.Title,
                ["content"] = draft.ContentHtml,
                ["type"] = 3
            };
            await PostSignedJsonAsync(
                "https://bizapi.csdn.net/blog/phoenix/console/v1/history-version/save",
                publishPayload);
        }
        return id;
    }

    /// <summary>拉取我的文章列表（公开主页接口，无需签名）。</summary>
    public async Task<List<CsdnArticle>> ListArticlesAsync()
    {
        var (ok, userName, _) = await CheckLoginAsync();
        if (!ok || string.IsNullOrWhiteSpace(userName))
        {
            throw new CsdnApiException(0, "无法获取账号名，登录可能已失效，请重新扫码登录。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://blog.csdn.net/community/home-api/v1/get-business-list" +
            "?page=1&size=20&businessType=blog&orderby=&noMore=false&username=" +
            Uri.EscapeDataString(userName));
        request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
        request.Headers.TryAddWithoutValidation("referer", "https://blog.csdn.net/" + Uri.EscapeDataString(userName));
        ApplyCookies(request);

        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());

        var result = new List<CsdnArticle>();
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in list.EnumerateArray())
        {
            long id = 0;
            foreach (var key in new[] { "articleId", "id" })
            {
                if (item.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    id = idEl.GetInt64();
                    break;
                }
            }
            var title = GetString(item, "title");
            var url = GetString(item, "url");
            if (id > 0)
            {
                result.Add(new CsdnArticle { Id = id, Title = title, Url = url });
            }
        }
        return result;
    }

    /// <summary>按 ID 读取公开文章标题（用于 csdn view）。</summary>
    public async Task<string> GetArticleTitleAsync(string userName, long articleId)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://blog.csdn.net/{Uri.EscapeDataString(userName)}/article/details/{articleId}");
            request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
            using var response = await _http.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();
            var match = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value
                    .Replace("_CSDN博客", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-CSDN博客", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }
        }
        catch
        {
            // 拿不到标题不影响主流程
        }
        return string.Empty;
    }

    private async Task<JsonElement> PostSignedJsonAsync(string url, object body)
    {
        var nonce = Guid.NewGuid().ToString("D");
        var uri = new Uri(url);
        var signature = Sign(uri.AbsolutePath, nonce);
        var json = JsonSerializer.Serialize(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("accept", AcceptJson);
        request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("origin", "https://mp.csdn.net");
        request.Headers.TryAddWithoutValidation("referer", "https://mp.csdn.net/mp_blog/creation/editor?not_checkout=1");
        request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
        request.Headers.TryAddWithoutValidation("x-ca-key", XCaKey);
        request.Headers.TryAddWithoutValidation("x-ca-nonce", nonce);
        request.Headers.TryAddWithoutValidation("x-ca-signature", signature);
        request.Headers.TryAddWithoutValidation("x-ca-signature-headers", "x-ca-key,x-ca-nonce");
        ApplyCookies(request);

        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json;");

        using var response = await _http.SendAsync(request);
        return ParseResponse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number)
        {
            var code = codeEl.GetInt32();
            if (code != 200)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? ""
                    : root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() ?? ""
                    : "";
                throw new CsdnApiException(code, $"CSDN 接口返回错误（{code}）：{msg}");
            }
        }
        return root;
    }

    private static long ExtractArticleId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        foreach (var key in new[] { "article_id", "id", "articleId" })
        {
            if (!data.TryGetProperty(key, out var el))
            {
                continue;
            }
            if (el.ValueKind == JsonValueKind.Number)
            {
                return el.GetInt64();
            }
            if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var value))
            {
                return value;
            }
        }
        return 0;
    }

    private static string GetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }
        return "";
    }

    private static string GuessMime(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
