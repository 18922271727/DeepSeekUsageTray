using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeepSeekUsageTray;

/// <summary>CSDN 登录会话：保存网页登录后的 Cookie，用于后续直接调接口发布。</summary>
public sealed class CsdnSession
{
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.Ordinal);
    public string UserName { get; set; } = string.Empty;
    public string UserNick { get; set; } = string.Empty;
    public long LastArticleId { get; set; }
    public string LastTitle { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }

    public bool HasLogin =>
        !string.IsNullOrWhiteSpace(UserName) ||
        Cookies.TryGetValue("UserName", out var un) && !string.IsNullOrWhiteSpace(un) ||
        Cookies.TryGetValue("UserInfo", out var ui) && !string.IsNullOrWhiteSpace(ui) ||
        Cookies.TryGetValue("UserToken", out var ut) && !string.IsNullOrWhiteSpace(ut);

    public string CookieHeader => string.Join("; ", Cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepSeekUsageTray");

    private static string FilePath => Path.Combine(Dir, "csdn-session.json");

    public static CsdnSession Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var session = JsonSerializer.Deserialize<CsdnSession>(File.ReadAllText(FilePath));
                if (session != null)
                {
                    return session;
                }
            }
        }
        catch
        {
            // 文件损坏时从空会话开始
        }
        return new CsdnSession();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            SavedAt = DateTime.Now;
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不阻塞主流程，下次登录会再尝试
        }
    }

    public void Clear()
    {
        Cookies.Clear();
        UserName = string.Empty;
        UserNick = string.Empty;
        LastArticleId = 0;
        LastTitle = string.Empty;
        Save();
    }
}
