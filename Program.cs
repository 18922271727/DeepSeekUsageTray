using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 截图模式：--screenshot <输出目录>
        // 供自动发帖/更新 README 时离线渲染三个标签页的界面图，不影响正常使用。
        var shotIndex = Array.IndexOf(args, "--screenshot");
        if (shotIndex >= 0)
        {
            var outDir = shotIndex + 1 < args.Length ? args[shotIndex + 1] : "docs/screenshots";
            return RunScreenshotMode(outDir);
        }

        // B绔欏彂甯栧懡浠よ妯″紡锛?bili <check|login|logout|list|publish|update> [...]
        var biliIndex = Array.IndexOf(args, "bili");
        if (biliIndex >= 0)
        {
            return BiliCli.RunAsync(args.Skip(biliIndex + 1).ToArray()).GetAwaiter().GetResult();
        }

        using var mutex = new Mutex(true, @"Local\DeepSeekUsageTray", out var createdNew);
        if (!createdNew)
        {
            return 0; // 程序已在运行，单实例
        }

        Application.Run(new TrayApp());
        return 0;
    }

    private static int RunScreenshotMode(string outDir)
    {
        var config = ConfigStore.Load();
        using var form = new MainForm(config);
        form.OnNeedLogin = null; // 截图模式不弹登录窗
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10000, -10000); // 屏幕外显示，不打扰用户，同时保证控件正常渲染

        // 显示后等首次刷新完成，再做第二次采样并截图
        var capture = new System.Windows.Forms.Timer { Interval = 14_000 };
        capture.Tick += async (_, _) =>
        {
            capture.Stop();
            try
            {
                await form.CaptureScreenshotsAsync(outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
            finally
            {
                Application.Exit();
            }
        };
        capture.Start();

        Application.Run(form);
        return 0;
    }
}
