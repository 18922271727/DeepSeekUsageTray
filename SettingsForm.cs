using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

internal sealed class SettingsForm : Form
{
    private static readonly Color BgColor = Color.FromArgb(27, 27, 28);
    private static readonly Color PanelColor = Color.FromArgb(42, 42, 45);
    private static readonly Color BorderColor = Color.FromArgb(58, 58, 62);
    private static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
    private static readonly Color TextWhite = Color.FromArgb(243, 244, 246);
    private static readonly Color TextGray = Color.FromArgb(156, 163, 175);

    private readonly AppConfig _config;
    private readonly TextBox _token = new();
    private CheckBox _showToken = new();

    public bool Saved { get; private set; }

    public SettingsForm(AppConfig config)
    {
        _config = config;
        AutoScaleMode = AutoScaleMode.None;
        BuildUi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void EnableDarkTitleBar()
    {
        try
        {
            int useDark = 1;
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
        }
        catch
        {
            // 旧系统不支持时忽略
        }
    }

    private void BuildUi()
    {
        SuspendLayout();
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 400);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = BgColor;
        ForeColor = TextWhite;

        var tokenCaption = new Label
        {
            Text = "网页登录 Token（余额、今日/本月用量都靠它）",
            AutoSize = true,
            Location = new Point(14, 14),
            ForeColor = TextGray
        };
        _token.Location = new Point(14, 38);
        _token.Size = new Size(420, 26);
        _token.PasswordChar = '●';
        _token.Text = _config.PlatformToken;
        _token.BackColor = PanelColor;
        _token.ForeColor = TextWhite;
        _token.BorderStyle = BorderStyle.FixedSingle;
        _showToken.Text = "显示";
        _showToken.AutoSize = true;
        _showToken.Location = new Point(442, 42);
        _showToken.ForeColor = TextGray;
        _showToken.CheckedChanged += (_, _) => _token.PasswordChar = _showToken.Checked ? '\0' : '●';

        var link = new LinkLabel
        {
            Text = "打开 DeepSeek 用量网页并登录（获取 Token）",
            AutoSize = true,
            Location = new Point(14, 112),
            LinkColor = AccentBlue
        };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://platform.deepseek.com") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开网页失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var loginButton = new Button
        {
            Text = "内嵌浏览器扫码登录（自动获取）",
            Size = new Size(250, 30),
            Location = new Point(14, 72),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = BorderColor },
            UseVisualStyleBackColor = false,
            BackColor = PanelColor,
            ForeColor = TextWhite
        };
        loginButton.Click += (_, _) =>
        {
            using var login = new LoginForm(_config);
            login.ShowDialog(this);
            if (login.GotToken)
            {
                _token.Text = _config.PlatformToken;
            }
        };

        var hint = new Label
        {
            Text = "Token 获取步骤（只需一次，过期后重复）：\r\n" +
                   "1. 点击上方链接，登录 DeepSeek 用量平台\r\n" +
                   "2. 在已登录页面按 F12，切到 Console（控制台）\r\n" +
                   "3. 输入下面这行代码后回车：\r\n" +
                   "   JSON.parse(localStorage.getItem(\"userToken\")).value\r\n" +
                   "4. 复制输出的一长串字符，粘贴到上面的输入框\r\n\r\n" +
                   "Token 只保存在本机，不会上传到任何地方。",
            AutoSize = false,
            Location = new Point(14, 140),
            Size = new Size(492, 145),
            ForeColor = TextGray,
            Font = new Font("Microsoft YaHei UI", 8.5f)
        };

        var save = new Button
        {
            Text = "保存",
            Size = new Size(88, 30),
            Location = new Point(310, 330),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = BorderColor },
            UseVisualStyleBackColor = false,
            BackColor = PanelColor,
            ForeColor = TextWhite
        };
        var cancel = new Button
        {
            Text = "取消",
            Size = new Size(88, 30),
            Location = new Point(410, 330),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = BorderColor },
            UseVisualStyleBackColor = false,
            BackColor = PanelColor,
            ForeColor = TextWhite
        };
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            tokenCaption,
            _token,
            _showToken,
            loginButton,
            link,
            hint,
            save,
            cancel
        });
        ResumeLayout();
    }

    private void SaveAndClose()
    {
        _config.PlatformToken = _token.Text.Trim();
        ConfigStore.Save(_config);
        Saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
