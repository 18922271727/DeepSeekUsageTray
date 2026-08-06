using System;
using System.IO;
using System.Text.Json;

namespace DeepSeekUsageTray;

public sealed class AppConfig
{
    public string PlatformToken { get; set; } = string.Empty;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
}

internal static class ConfigStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepSeekUsageTray");

    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch
        {
            // 配置文件损坏时从空白配置开始
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
