using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeepSeekUsageTray;

/// <summary>蓝奏云登录会话：保存网页登录后的 Cookie，供蓝奏云上传脚本使用。</summary>
public sealed class LanzouSession
{
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.Ordinal);
    public DateTime SavedAt { get; set; }

    public bool HasLogin => Cookies.TryGetValue("ylogin", out var v) && !string.IsNullOrWhiteSpace(v);

    /// <summary>
    /// 会话文件路径。优先取环境变量 DEEPSEEK_LANZOU_SESSION（供发布脚本/快捷登录脚本指定共享路径），
    /// 否则落在当前用户的 %APPDATA%\DeepSeekUsageTray 下。
    /// </summary>
    private static string FilePath =>
        Environment.GetEnvironmentVariable("DEEPSEEK_LANZOU_SESSION")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeepSeekUsageTray",
            "lanzou-session.json");

    public static LanzouSession Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var session = JsonSerializer.Deserialize<LanzouSession>(File.ReadAllText(FilePath));
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
        return new LanzouSession();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            SavedAt = DateTime.Now;
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不阻断主流程，下次登录会再尝试
        }
    }

    public void Clear()
    {
        Cookies.Clear();
        Save();
    }
}
