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
    // CSDN 编辑器当前使用的图片上传链路：先取 OSS 上传签名，再直传华为云图床。
    // 以下密钥来自官方前端脚本（resource-api direct upload 流程）。
    private const string UploadSignatureUrl = "https://bizapi.csdn.net/resource-api/v1/image/direct/upload/signature";
    private const string UploadAppKey = "260196572";
    private const string UploadAppSecret = "t5PaqxVQpWoHgLGt7XPIvd5ipJcwJTU7";
    private const string OssHost = "https://csdn-img-blog.obs.cn-north-4.myhuaweicloud.com";
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
        if (_session.Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _session.CookieHeader);
        }
    }

    /// <summary>计算 CSDN 接口签名（x-ca-signature）。</summary>
    private static string Sign(string path, string nonce)
    {
        return SignCore("POST", AcceptJson, "application/json;", "", nonce, null, XCaKey, EKey, path, "");
    }

    /// <summary>
    /// 计算带 x-ca-timestamp 的新网关签名。
    /// StringToSign = METHOD\nACCEPT\n\nCONTENT_TYPE\n\nx-ca-key:..\nx-ca-nonce:..\n[x-ca-timestamp:..\n]PATH[?QUERY]
    /// </summary>
    private static string SignNew(
        string method,
        string accept,
        string contentType,
        string path,
        string query,
        string nonce,
        string? timestamp,
        string key,
        string secret)
    {
        return SignCore(method, accept, contentType, "", nonce, timestamp, key, secret, path, query);
    }

    private static string SignCore(
        string method,
        string accept,
        string contentType,
        string date,
        string nonce,
        string? timestamp,
        string key,
        string secret,
        string path,
        string query)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append('\n');
        sb.Append(accept).Append('\n');
        sb.Append('\n');
        sb.Append(contentType).Append('\n');
        sb.Append(date).Append('\n');
        sb.Append("x-ca-key:").Append(key).Append('\n');
        sb.Append("x-ca-nonce:").Append(nonce).Append('\n');
        if (!string.IsNullOrEmpty(timestamp))
        {
            sb.Append("x-ca-timestamp:").Append(timestamp).Append('\n');
        }
        sb.Append(path);
        if (!string.IsNullOrEmpty(query))
        {
            sb.Append('?').Append(query);
        }

        var toSign = sb.ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>检查登录状态，返回 (是否登录, 账号名, 昵称)。</summary>
    public async Task<(bool Ok, string UserName, string Nick)> CheckLoginAsync()
    {
        // 登录 Cookie 里直接带账号信息，先兜底，避免依赖可能下线的用户接口
        var (okLocal, nameLocal, nickLocal) = CheckLoginLocal();

        // 尝试在线校验（接口若下线则退回本地判断）
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
        return (okLocal, nameLocal, nickLocal);
    }

    /// <summary>从登录 Cookie 读取账号信息（不需要额外接口）。</summary>
    public (bool Ok, string UserName, string Nick) CheckLoginLocal()
    {
        var userName = _session.Cookies.TryGetValue("UserName", out var un) ? un : "";
        var nick = _session.Cookies.TryGetValue("UserNick", out var nn) ? nn : "";
        if (!string.IsNullOrWhiteSpace(nick) && nick.Contains('%'))
        {
            try
            {
                nick = Uri.UnescapeDataString(nick);
            }
            catch
            {
                // 解码失败保留原值
            }
        }
        return (!string.IsNullOrWhiteSpace(userName), userName, nick);
    }

    /// <summary>上传一张图片到 CSDN 图床，返回 CDN 链接。</summary>
    public async Task<string> UploadImageAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = "png";
        }

        // 第一步：向 CSDN 请求 OSS 直传签名
        var sign = await GetUploadSignatureAsync(ext);

        var host = string.IsNullOrWhiteSpace(sign.Host) ? OssHost : sign.Host;
        if (string.IsNullOrWhiteSpace(sign.AccessId) ||
            string.IsNullOrWhiteSpace(sign.Policy) ||
            string.IsNullOrWhiteSpace(sign.Signature) ||
            string.IsNullOrWhiteSpace(sign.FilePath))
        {
            throw new CsdnApiException(0, "获取上传签名失败：返回字段不完整。");
        }

        // 第二步：把图片直传华为云 OBS 图床。
        // 手写标准 multipart/form-data（不用 MultipartFormDataContent），
        // 因为 .NET 会给文件部分附加 filename*=utf-8'' 扩展头，华为云 OBS 不识别，
        // 会报 “POST requires exactly one file upload per request”。
        var fields = new Dictionary<string, string>
        {
            ["key"] = sign.FilePath,
            ["policy"] = sign.Policy,
            ["signature"] = sign.Signature,
            ["callbackBody"] = sign.CallbackBody ?? "",
            ["callbackBodyType"] = sign.CallbackBodyType ?? "",
            ["callbackUrl"] = sign.CallbackUrl ?? "",
            ["AccessKeyId"] = sign.AccessId
        };
        foreach (var (k, v) in sign.CustomParams)
        {
            fields["x:" + k] = v;
        }

        var bytes = await File.ReadAllBytesAsync(filePath);
        var (body, boundary) = BuildObsMultipart(fields, "file", "image." + ext, GuessMime(filePath), bytes);
        if (IsDebug())
        {
            DumpDebug("multipart-body", Encoding.UTF8.GetString(body));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, host);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
        request.Headers.TryAddWithoutValidation("origin", "https://editor.csdn.net");
        request.Headers.TryAddWithoutValidation("referer", "https://editor.csdn.net/");
        request.Headers.TryAddWithoutValidation("accept", "*/*");
        ApplyCookies(request);

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (IsDebug())
        {
            DumpDebug("oss-response", text);
        }
        JsonElement root;
        try
        {
            root = ParseResponse(text);
        }
        catch (JsonException)
        {
            throw new CsdnApiException(
                (int)response.StatusCode,
                "图片上传到图床失败，返回内容不是 JSON：" + Truncate(text, 300));
        }

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String && data.GetString() is { Length: > 0 } direct)
            {
                return direct;
            }
            foreach (var key in new[] { "imageUrl", "url" })
            {
                if (data.TryGetProperty(key, out var url) &&
                    url.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        throw new CsdnApiException(0, "图片上传到图床后返回数据里没有图片链接：" + Truncate(text, 300));
    }

    /// <summary>构造浏览器同款 multipart/form-data 请求体，供华为云 OBS 直传。</summary>
    private static (byte[] Body, string Boundary) BuildObsMultipart(
        IReadOnlyDictionary<string, string> fields,
        string fileFieldName,
        string fileName,
        string contentType,
        byte[] fileBytes)
    {
        var boundary = "----CsdnObs" + Guid.NewGuid().ToString("N")[..20];
        var sb = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
            sb.Append(value).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"").Append(fileFieldName)
            .Append("\"; filename=\"").Append(fileName).Append("\"\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n\r\n");

        var head = Encoding.UTF8.GetBytes(sb.ToString());
        var tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
        var body = new byte[head.Length + fileBytes.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(fileBytes, 0, body, head.Length, fileBytes.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + fileBytes.Length, tail.Length);
        return (body, boundary);
    }

    private async Task<UploadSignature> GetUploadSignatureAsync(string ext)
    {
        // 依次尝试（新密钥+时间戳）→（旧密钥+时间戳）→（旧密钥）→（新密钥），
        // 以兼容不同时期的网关配置。
        var candidates = new[]
        {
            (Key: UploadAppKey, Secret: UploadAppSecret, UseTimestamp: true),
            (Key: XCaKey, Secret: EKey, UseTimestamp: true),
            (Key: XCaKey, Secret: EKey, UseTimestamp: false),
            (Key: UploadAppKey, Secret: UploadAppSecret, UseTimestamp: false)
        };

        Exception? lastError = null;
        foreach (var (key, secret, useTimestamp) in candidates)
        {
            try
            {
                return await TryGetUploadSignatureAsync(ext, key, secret, useTimestamp);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new CsdnApiException(0, "获取上传签名失败：" + lastError?.Message);
    }

    private async Task<UploadSignature> TryGetUploadSignatureAsync(
        string ext,
        string key,
        string secret,
        bool useTimestamp)
    {
        var nonce = Guid.NewGuid().ToString("D");
        var timestamp = useTimestamp ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() : null;
        var contentType = "application/json;charset=UTF-8";
        var uri = new Uri(UploadSignatureUrl);
        var signature = SignNew(
            "POST",
            AcceptJson,
            contentType,
            uri.AbsolutePath,
            "",
            nonce,
            timestamp,
            key,
            secret);

        var payload = new Dictionary<string, object?>
        {
            ["imageTemplate"] = "standard",
            ["appName"] = "direct_blog_markdown",
            ["imageSuffix"] = ext
        };
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadSignatureUrl);
        request.Headers.TryAddWithoutValidation("accept", AcceptJson);
        request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("origin", "https://editor.csdn.net");
        request.Headers.TryAddWithoutValidation("referer", "https://editor.csdn.net/");
        request.Headers.TryAddWithoutValidation("user-agent", ChromeUa);
        request.Headers.TryAddWithoutValidation("x-ca-key", key);
        request.Headers.TryAddWithoutValidation("x-ca-nonce", nonce);
        if (useTimestamp)
        {
            request.Headers.TryAddWithoutValidation("x-ca-timestamp", timestamp);
        }
        request.Headers.TryAddWithoutValidation("x-ca-signature", signature);
        request.Headers.TryAddWithoutValidation(
            "x-ca-signature-headers",
            useTimestamp ? "x-ca-key,x-ca-nonce,x-ca-timestamp" : "x-ca-key,x-ca-nonce");
        ApplyCookies(request);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await _http.SendAsync(request);
        var root = ParseResponse(await response.Content.ReadAsStringAsync());
        if (IsDebug())
        {
            DumpDebug("sign-response", root.GetRawText());
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new CsdnApiException(0, "返回数据里没有 data。");
        }

        var result = new UploadSignature
        {
            AccessId = GetString(data, "accessId"),
            Policy = GetString(data, "policy"),
            Signature = GetString(data, "signature"),
            Host = GetString(data, "host"),
            FilePath = GetString(data, "filePath"),
            CallbackUrl = GetString(data, "callbackUrl"),
            CallbackBody = GetString(data, "callbackBody"),
            CallbackBodyType = GetString(data, "callbackBodyType")
        };
        if (data.TryGetProperty("customParam", out var custom) && custom.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in custom.EnumerateObject())
            {
                result.CustomParams[prop.Name] =
                    prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
            }
        }
        return result;
    }

    private sealed class UploadSignature
    {
        public string AccessId { get; set; } = "";
        public string Policy { get; set; } = "";
        public string Signature { get; set; } = "";
        public string Host { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string CallbackUrl { get; set; } = "";
        public string CallbackBody { get; set; } = "";
        public string CallbackBodyType { get; set; } = "";
        public Dictionary<string, string> CustomParams { get; } = new();
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

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static bool IsDebug() =>
        string.Equals(Environment.GetEnvironmentVariable("CSDN_DEBUG"), "1", StringComparison.Ordinal);

    private static void DumpDebug(string name, string text)
    {
        if (!IsDebug())
        {
            return;
        }
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "csdn-debug.log"),
                $"[{name}]\n{text}\n\n");
        }
        catch
        {
            // 调试日志写失败不影响主流程
        }
    }
}
