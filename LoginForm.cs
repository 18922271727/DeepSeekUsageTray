using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekUsageTray;

internal sealed class LoginForm : Form
{
    private readonly AppConfig _config;
    private readonly WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 2000 };
    private readonly HashSet<string> _trying = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private Label _status = new();
    private bool _closed;

    /// <summary>是否已成功拿到并保存 Token。</summary>
    public bool GotToken { get; private set; }

    public LoginForm(AppConfig config)
    {
        _config = config;
        AutoScaleMode = AutoScaleMode.None;
        BuildUi();
        Load += async (_, _) => await InitializeWebAsync();
    }

    private void BuildUi()
    {
        Text = "DeepSeek 网页登录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 720);
        MinimumSize = new Size(430, 580);
        Font = new Font("Microsoft YaHei UI", 9f);

        _status.Text = "正在打开登录页面…";
        _status.Dock = DockStyle.Top;
        _status.Height = 42;
        _status.Padding = new Padding(12, 10, 12, 0);
        _status.ForeColor = Color.Gray;

        _web.Dock = DockStyle.Fill;

        Controls.Add(_web);
        Controls.Add(_status);

        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekUsageTray",
                "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _web.EnsureCoreWebView2Async(env);

            try
            {
                // 清掉上次的登录记录，确保每次都是全新登录
                await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllSite);
            }
            catch
            {
                // 清理失败不阻塞登录，继续正常流程
            }

            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // 观察网页向 DeepSeek 后台发出的请求，直接读取它携带的登录凭证
            _web.CoreWebView2.AddWebResourceRequestedFilter(
                "https://platform.deepseek.com/api/v0/*",
                CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.WebResourceResponseReceived += (_, args) =>
            {
                var auth = args.Request.Headers.GetHeader("Authorization");
                if (!string.IsNullOrWhiteSpace(auth) &&
                    auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidate(auth.Substring("Bearer ".Length).Trim());
                }
            };

            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _ = PollAsync();
                }
            };

            _status.Text = "请在窗口里扫码登录 DeepSeek，登录成功后会自动保存 Token 并关闭本窗口";
            _web.Source = new Uri("https://platform.deepseek.com/");
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            _status.Text = "内嵌浏览器初始化失败：" + ex.Message;
            _pollTimer.Stop();
        }
    }

    private async Task PollAsync()
    {
        if (_closed || _web.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            var result = await _web.ExecuteScriptAsync(CollectTokenScript);
            if (!string.IsNullOrWhiteSpace(result))
            {
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AddCandidate(item.GetString());
                        }
                    }
                }
            }

            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://platform.deepseek.com");
            foreach (var cookie in cookies)
            {
                if (cookie.Name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrWhiteSpace(cookie.Value) &&
                    cookie.Value.Length >= 16)
                {
                    AddCandidate(cookie.Value);
                }
            }
        }
        catch
        {
            // 页面尚未就绪时忽略，下一轮再试
        }
    }

    private void AddCandidate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
        {
            return;
        }
        token = token.Trim();
        if (_trying.Contains(token))
        {
            return;
        }
        if (_attempts.TryGetValue(token, out var attempts) && attempts >= 3)
        {
            return;
        }
        _ = TryCandidateAsync(token);
    }

    private async Task TryCandidateAsync(string token)
    {
        _trying.Add(token);
        try
        {
            if (!await IsTokenValidAsync(token))
            {
                _attempts[token] = (_attempts.TryGetValue(token, out var n) ? n : 0) + 1;
                return;
            }

            _config.PlatformToken = token;
            ConfigStore.Save(_config);
            GotToken = true;
            _pollTimer.Stop();
            _closed = true;
            _status.Text = "登录成功，Token 已自动保存！";
            MessageBox.Show(
                this,
                "登录成功，Token 已自动保存！",
                "成功",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch
        {
            // 校验失败忽略，等待下一次机会
        }
        finally
        {
            _trying.Remove(token);
        }
    }

    private static async Task<bool> IsTokenValidAsync(string token)
    {
        try
        {
            var summary = await new DeepSeekClient().FetchAsync(token);
            return summary.BalanceConfigured || summary.UsageConfigured;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _closed = true;
        base.OnFormClosed(e);
    }

    /// <summary>删除内嵌浏览器保存的登录记录（注销时调用）。</summary>
    public static void ClearPersistentData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekUsageTray",
            "WebView2");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // 可能被浏览器进程短暂占用，稍后登录窗口会再清理一次
        }
    }

    private const string CollectTokenScript = @"
(function () {
    var keys = ['userToken', 'accessToken', 'access_token', 'user_token', 'token'];
    var out = [];
    function collect(store) {
        if (!store) return;
        for (var i = 0; i < keys.length; i++) {
            try {
                var raw = store.getItem(keys[i]);
                if (!raw) continue;
                try {
                    var obj = JSON.parse(raw);
                    if (obj && typeof obj === 'object') {
                        var v = obj.value || obj.token || obj.accessToken || obj.userToken;
                        if (typeof v === 'string' && v.length > 16) out.push(v);
                    }
                } catch (e) {}
                if (raw.length > 16 && /^[A-Za-z0-9\-_.]+$/.test(raw)) out.push(raw);
            } catch (e) {}
        }
    }
    collect(window.localStorage);
    collect(window.sessionStorage);
    return JSON.stringify(out);
})()";
}
