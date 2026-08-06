using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

/// <summary>
/// 蓝奏云命令行模式：供发布脚本调用，完成登录 / 检查 / 退出。
/// 用法：DeepSeekUsageTray.exe lanzou <login|check|logout> [--force true]
/// </summary>
internal static class LanzouCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            switch (args[0])
            {
                case "login":
                    return Login(args);
                case "check":
                    return Check(args);
                case "logout":
                    return Logout(args);
                default:
                    Console.Error.WriteLine("未知命令: " + args[0]);
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

    private static int Login(string[] args)
    {
        var force = args.Contains("--force") || args.Contains("--force=true");
        var session = LanzouSession.Load();
        if (!force && session.HasLogin)
        {
            Console.WriteLine("已登录（如需重新登录请加 --force true）");
            return 0;
        }

        using var form = new LanzouLoginForm();
        Application.Run(form);
        if (form.Saved)
        {
            Console.WriteLine("登录状态已保存: " + GetSessionPath());
            return 0;
        }

        Console.Error.WriteLine("未保存登录状态");
        return 1;
    }

    private static string GetSessionPath()
    {
        var envPath = Environment.GetEnvironmentVariable("DEEPSEEK_LANZOU_SESSION");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeepSeekUsageTray",
            "lanzou-session.json");
    }

    private static int Check(string[] args)
    {
        var session = LanzouSession.Load();
        Console.WriteLine(session.HasLogin ? "已登录" : "未登录");
        return session.HasLogin ? 0 : 1;
    }

    private static int Logout(string[] args)
    {
        LanzouSession.Load().Clear();
        Console.WriteLine("已清除蓝奏云登录状态");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("蓝奏云命令行:");
        Console.WriteLine("  lanzou login [--force true]   打开网页登录并保存 Cookie");
        Console.WriteLine("  lanzou check                  检查登录状态");
        Console.WriteLine("  lanzou logout                 清除登录状态");
    }
}
