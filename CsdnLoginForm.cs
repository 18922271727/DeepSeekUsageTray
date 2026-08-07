using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekUsageTray;

/// <summary>
/// CSDN 网页登录窗体：内嵌 passport.csdn.net 登录页（扫码 / 手机号），
/// 登录成功后自动抓取 Cookie 保存（也可手动点按钮保存）。
/// </summary>
internal sealed class CsdnLoginForm : Form
{
    private static readonly string[] CookieHosts =
    {
        "https://passport.csdn.net",
        "https://www.csdn.net",
        "https://blog.csdn.net",
        "https://editor.csdn.net",
        "https://mp.csdn.net",
        "https://me.csdn.net",
        "https://bizapi.csdn.net",
        "https://blog-console-api.csdn.net"
    };

    private readonly WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 2000 };
    private Label _status = new();
    private bool _closed;
    private bool _saved;
    private int _loginStreak;

    /// <summary>是否已成功保存登录状态。</summary>
    public bool Saved => _saved;

    public CsdnLoginForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        BuildUi();
        Load += async (_, _) => await InitializeWebAsync();
    }

    private void BuildUi()
    {
        Text = "CSDN 登录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 720);
        MinimumSize = new Size(460, 600);
        Font = new Font("Microsoft YaHei UI", 9f);

        _status.Text = "正在打开登录页面…";
        _status.Dock = DockStyle.Top;
        _status.Height = 46;
        _status.Padding = new Padding(12, 10, 12, 0);
        _status.ForeColor = Color.Gray;

        var saveButton = new Button
        {
            Text = "完成登录并保存",
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Color.FromArgb(56, 96, 218),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        saveButton.Click += (_, _) => SaveAndCloseAsync();

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        Controls.Add(saveButton);
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
                "WebView2Csdn");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _status.Text = "请在窗口里扫码或输入账号登录 CSDN，登录成功后会自动保存并关闭";
            _web.Source = new Uri("https://passport.csdn.net/login");
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
            var cookies = await CollectCookiesAsync();
            if (HasLoginCookies(cookies))
            {
                _loginStreak++;
                if (_loginStreak >= 2)
                {
                    await SaveSessionAsync(cookies);
                }
            }
            else
            {
                _loginStreak = 0;
            }
        }
        catch
        {
            // 页面尚未就绪时忽略，下一轮再试
        }
    }

    private static bool HasLoginCookies(Dictionary<string, string> cookies) =>
        cookies.TryGetValue("UserName", out var un) && !string.IsNullOrWhiteSpace(un) ||
        cookies.TryGetValue("UserInfo", out var ui) && !string.IsNullOrWhiteSpace(ui) ||
        cookies.TryGetValue("UserToken", out var ut) && !string.IsNullOrWhiteSpace(ut);

    private async Task<Dictionary<string, string>> CollectCookiesAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in CookieHosts)
        {
            try
            {
                var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(host);
                foreach (var cookie in cookies)
                {
                    result[cookie.Name] = cookie.Value;
                }
            }
            catch
            {
                // 单个域名失败不影响其他域名
            }
        }
        return result;
    }

    private async void SaveAndCloseAsync()
    {
        if (_web.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            var cookies = await CollectCookiesAsync();
            if (cookies.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "没有获取到任何登录信息，请先完成网页登录再点保存。",
                    "提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            await SaveSessionAsync(cookies);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveSessionAsync(Dictionary<string, string> cookies)
    {
        var session = new CsdnSession
        {
            Cookies = cookies,
            SavedAt = DateTime.Now
        };
        try
        {
            var (ok, userName, nick) = await new CsdnClient(session).CheckLoginAsync();
            if (ok)
            {
                session.UserName = userName;
                session.UserNick = nick;
            }
        }
        catch
        {
            // 校验失败不阻塞保存，登录态以 Cookie 为准
        }
        session.Save();
        _saved = true;
        _pollTimer.Stop();
        _closed = true;
        _status.Text = "登录状态已保存！";
        MessageBox.Show(
            this,
            "CSDN 登录状态已保存，可以关闭窗口。",
            "成功",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _closed = true;
        base.OnFormClosed(e);
    }
}
