using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekUsageTray;

/// <summary>
/// B站图文发布器：内嵌网页扫码登录 → 自动生成文案并上传截图 → 发布/更新专栏文章。
/// </summary>
internal sealed partial class BiliPublishForm : Form
{
    private static readonly Color Bg = Color.FromArgb(24, 24, 28);
    private static readonly Color PanelBg = Color.FromArgb(30, 30, 36);
    private static readonly Color Border = Color.FromArgb(60, 60, 70);
    private static readonly Color TextColor = Color.FromArgb(230, 230, 230);
    private static readonly Color Gray = Color.FromArgb(150, 155, 165);
    private static readonly Color Blue = Color.FromArgb(76, 141, 255);
    private static readonly Color Green = Color.FromArgb(87, 197, 111);

    private readonly BiliSession _session = BiliSession.Load();
    private readonly BiliClient _client;
    private readonly WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 2000 };

    private readonly Panel _loginPanel = new();
    private readonly Panel _publishPanel = new();
    private readonly Label _loginStatus = new();
    private readonly Button _loginContinue = new();
    private readonly Label _accountLabel = new();
    private readonly TextBox _titleBox = new();
    private readonly ComboBox _categoryBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _tagsBox = new();
    private readonly TextBox _summaryBox = new();
    private readonly TextBox _contentBox = new() { Multiline = true };
    private readonly TextBox _aidBox = new();
    private readonly RichTextBox _logBox = new() { ReadOnly = true };
    private readonly Button _publishBtn = new();
    private readonly Button _updateBtn = new();
    private readonly Button _pickBtn = new();
    private readonly Button _reloginBtn = new();

    private bool _loggedIn;
    private bool _busy;
    private string? _readmeDir;

    public BiliPublishForm()
    {
        _client = new BiliClient(_session);
        AutoScaleMode = AutoScaleMode.None;
        Text = "B站图文发布器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 840);
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildLoginPanel();
        BuildPublishPanel();
        Controls.Add(_publishPanel);
        Controls.Add(_loginPanel);

        Load += async (_, _) => await LoadAsync();
        FormClosed += (_, _) => _pollTimer.Stop();
    }

    private async Task LoadAsync()
    {
        if (_session.HasLogin)
        {
            _loginStatus.Text = "正在检查已保存的 B站登录状态…";
            var (ok, uname) = await _client.CheckLoginAsync();
            if (ok)
            {
                _session.UserName = uname;
                _session.Save();
                _loggedIn = true;
                ShowPublishPanel();
                return;
            }
        }
        await InitializeWebAsync();
    }

    #region 登录页

    private void BuildLoginPanel()
    {
        _loginPanel.Dock = DockStyle.Fill;
        _loginPanel.BackColor = Bg;

        _loginStatus.Text = "正在打开 B站登录页…";
        _loginStatus.Dock = DockStyle.Top;
        _loginStatus.Height = 44;
        _loginStatus.Padding = new Padding(14, 12, 14, 0);
        _loginStatus.ForeColor = Gray;

        var tip = new Label
        {
            Text = "在下方窗口中扫码或输入账号登录 B站，登录成功后点“继续”即可进入发布页。",
            Dock = DockStyle.Bottom,
            Height = 40,
            ForeColor = Gray,
            Padding = new Padding(14, 4, 14, 8)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = Bg
        };
        _loginContinue.Text = "继续 →";
        _loginContinue.Enabled = false;
        _loginContinue.Width = 110;
        _loginContinue.Click += (_, _) => ShowPublishPanel();
        buttons.Controls.Add(_loginContinue);

        var recheck = new Button { Text = "重新检测", Width = 100 };
        recheck.Click += async (_, _) => await PollAsync(force: true);
        buttons.Controls.Add(recheck);

        _web.Dock = DockStyle.Fill;
        _web.BackColor = Color.White;

        _loginPanel.Controls.Add(_web);
        _loginPanel.Controls.Add(buttons);
        _loginPanel.Controls.Add(tip);
        _loginPanel.Controls.Add(_loginStatus);
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekUsageTray",
                "BiliWebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _web.CoreWebView2.Navigate(args.Uri);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    await PollAsync();
                }
            };

            _loginStatus.Text = "请在窗口里扫码登录 B站（可用手机 App 扫码）…";
            _loginStatus.ForeColor = Gray;
            _web.Source = new Uri("https://www.bilibili.com/");
            _pollTimer.Tick += async (_, _) => await PollAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            _loginStatus.Text = "内嵌浏览器初始化失败：" + ex.Message;
            _loginStatus.ForeColor = Color.FromArgb(240, 100, 100);
            _pollTimer.Stop();
        }
    }

    private async Task PollAsync(bool force = false)
    {
        if (_loggedIn || _busy || _web.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var host in new[] { "https://www.bilibili.com", "https://bilibili.com" })
            {
                var list = await _web.CoreWebView2.CookieManager.GetCookiesAsync(host);
                foreach (var cookie in list)
                {
                    cookies[cookie.Name] = cookie.Value;
                }
            }

            if (!cookies.TryGetValue("SESSDATA", out var sess) || string.IsNullOrWhiteSpace(sess))
            {
                return;
            }

            _session.Cookies = cookies;
            var (ok, uname) = await _client.CheckLoginAsync();
            if (ok)
            {
                _loggedIn = true;
                _session.UserName = uname;
                _session.Save();
                _pollTimer.Stop();
                _loginStatus.Text = "✓ 已登录：" + uname;
                _loginStatus.ForeColor = Green;
                _loginContinue.Enabled = true;
                _loginContinue.BackColor = Blue;
                _loginContinue.ForeColor = Color.White;
            }
        }
        catch
        {
            // 页面未就绪或网络抖动，下一轮再试
        }
    }

    #endregion

    #region 发布页

    private void BuildPublishPanel()
    {
        _publishPanel.Dock = DockStyle.Fill;
        _publishPanel.BackColor = Bg;
        _publishPanel.Padding = new Padding(14, 10, 14, 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Bg };
        var title = new Label
        {
            Text = "B站图文发布器",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(0, 8)
        };
        _accountLabel.Text = "";
        _accountLabel.AutoSize = true;
        _accountLabel.ForeColor = Green;
        _accountLabel.Location = new Point(140, 12);
        header.Controls.Add(title);
        header.Controls.Add(_accountLabel);

        _logBox.Dock = DockStyle.Bottom;
        _logBox.Height = 150;
        _logBox.BackColor = PanelBg;
        _logBox.ForeColor = Color.FromArgb(190, 195, 205);
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 9f);
        _logBox.DetectUrls = false;

        var formPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 6, 0, 6),
            AutoScroll = true,
            BackColor = Bg
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control, int row, string? hint = null)
        {
            var lab = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Gray
            };
            var cell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2) };
            control.Dock = DockStyle.Fill;
            cell.Controls.Add(control);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(lab, 0, row);
            grid.Controls.Add(cell, 1, row);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label
                {
                    Text = hint,
                    AutoSize = true,
                    ForeColor = Color.FromArgb(110, 115, 125),
                    Margin = new Padding(2, 0, 0, 0)
                };
                grid.Controls.Add(h, 1, row + 1);
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
        }

        _titleBox.Height = 30;
        _categoryBox.Height = 30;
        _tagsBox.Height = 30;
        _summaryBox.Height = 30;
        _contentBox.Height = 190;
        _aidBox.Height = 30;

        StyleBox(_titleBox);
        StyleBox(_categoryBox);
        StyleBox(_tagsBox);
        StyleBox(_summaryBox);
        StyleBox(_contentBox);
        StyleBox(_aidBox);

        AddRow("文章标题", _titleBox, 0);
        AddRow("分类", _categoryBox, 1);
        AddRow("标签", _tagsBox, 2, "多个标签用逗号分隔，最多 10 个");
        AddRow("摘要", _summaryBox, 4);
        AddRow("正文(Markdown)", _contentBox, 5);
        AddRow("更新目标", _aidBox, 7, "更新时填已有文章 cv 编号，例如 cv123456；新发布可留空");

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(0, 8, 0, 4),
            BackColor = Bg
        };

        _publishBtn.Text = "发布新文章";
        _publishBtn.Width = 120;
        _publishBtn.Click += async (_, _) => await PublishAsync(update: false);

        _updateBtn.Text = "更新已有文章";
        _updateBtn.Width = 120;
        _updateBtn.Click += async (_, _) => await PublishAsync(update: true);

        _pickBtn.Text = "从我的专栏选择…";
        _pickBtn.Width = 130;
        _pickBtn.Click += async (_, _) => await PickArticleAsync();

        _reloginBtn.Text = "重新登录";
        _reloginBtn.Width = 100;
        _reloginBtn.Click += (_, _) => Relogin();

        StyleButton(_publishBtn, primary: true);
        StyleButton(_updateBtn, primary: false);
        StyleButton(_pickBtn, primary: false);
        StyleButton(_reloginBtn, primary: false);

        buttons.Controls.Add(_publishBtn);
        buttons.Controls.Add(_updateBtn);
        buttons.Controls.Add(_pickBtn);
        buttons.Controls.Add(_reloginBtn);

        formPanel.Controls.Add(grid);
        formPanel.Controls.Add(buttons);

        _publishPanel.Controls.Add(formPanel);
        _publishPanel.Controls.Add(_logBox);
        _publishPanel.Controls.Add(header);
    }

    private static void StyleBox(Control control)
    {
        control.BackColor = PanelBg;
        control.ForeColor = TextColor;
        control.Font = new Font("Microsoft YaHei UI", 9f);
        if (control is TextBoxBase textBoxBase)
        {
            textBoxBase.BorderStyle = BorderStyle.FixedSingle;
        }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = primary ? Blue : PanelBg;
        button.ForeColor = primary ? Color.White : TextColor;
        button.Cursor = Cursors.Hand;
    }

    private void ShowPublishPanel()
    {
        _pollTimer.Stop();
        _accountLabel.Text = "账号：" + _session.UserName;
        _loginPanel.Visible = false;
        _publishPanel.Visible = true;
        PreparePublish();
    }

    private void Relogin()
    {
        _session.Clear();
        _loggedIn = false;
        _loginContinue.Enabled = false;
        _loginStatus.Text = "正在打开 B站登录页…";
        _loginStatus.ForeColor = Gray;
        _publishPanel.Visible = false;
        _loginPanel.Visible = true;
        _pollTimer.Start();
        try
        {
            if (_web.CoreWebView2 != null)
            {
                _ = _web.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllSite);
                _web.CoreWebView2.Navigate("https://www.bilibili.com/");
            }
        }
        catch
        {
            // 清理失败不阻塞，用户仍可在页面里手动退出
        }
    }

    private void PreparePublish()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";
            _titleBox.Text = $"DeepSeek 用量便签 v{version}：Windows 桌面实时用量小工具（免费开源）";

            var (markdown, dir) = LoadReadme();
            _readmeDir = dir;
            if (markdown != null)
            {
                _contentBox.Text = markdown;
                _summaryBox.Text = FirstSentence(markdown);
                Log("已自动载入 README 作为正文，可手动修改后发布。");
            }
            else
            {
                _contentBox.Text = BuildFallbackContent();
                _summaryBox.Text = "一个藏在系统托盘里的 DeepSeek 用量便签，扫码登录即可实时查看余额与用量。";
                Log("未找到 README，已使用内置文案。");
            }
            _tagsBox.Text = "DeepSeek,桌面工具,开源,Windows,效率工具";

            _aidBox.Text = _session.ArticleId > 0 ? "cv" + _session.ArticleId : "";
            if (_session.ArticleId > 0)
            {
                Log($"上次发布/更新的文章编号：cv{_session.ArticleId}（已填入更新目标）");
            }

            _ = LoadCategoriesAsync();
        }
        catch (Exception ex)
        {
            Log("准备发布页失败：" + ex.Message);
        }
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            _categoryBox.Items.Clear();
            var list = await _client.GetCategoriesAsync();
            _categoryBox.Items.AddRange(list.Cast<object>().ToArray());
            var preferred = list.FirstOrDefault(c => c.ParentName == "科技") ?? list.FirstOrDefault();
            if (preferred != null)
            {
                _categoryBox.SelectedItem = preferred;
            }
            Log($"已加载 {list.Count} 个专栏分类。");
        }
        catch (Exception ex)
        {
            Log("加载分类失败：" + ex.Message);
        }
    }

    private async Task PublishAsync(bool update)
    {
        if (_busy)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_titleBox.Text))
        {
            Log("请先填写标题。");
            return;
        }
        if (_categoryBox.SelectedItem is not ArticleCategory category)
        {
            Log("请先选择分类。");
            return;
        }
        if (string.IsNullOrWhiteSpace(_contentBox.Text))
        {
            Log("正文不能为空。");
            return;
        }

        long aid = 0;
        if (update)
        {
            aid = ParseCv(_aidBox.Text);
            if (aid <= 0)
            {
                Log("更新前请先在“更新目标”里填 cv 编号，例如 cv123456。");
                return;
            }
        }

        SetBusy(true);
        try
        {
            Log(update
                ? $"开始更新文章 cv{aid}…"
                : "开始发布新文章…");

            var draft = new BiliDraft
            {
                Title = _titleBox.Text.Trim(),
                Category = category.Id,
                Summary = _summaryBox.Text.Trim(),
                Tags = _tagsBox.Text.Trim(),
                ContentHtml = await MarkdownToHtml.ConvertAsync(
                    _contentBox.Text,
                    ResolveImageAsync)
            };

            var articleId = update
                ? await _client.UpdateArticleAsync(aid, draft)
                : await _client.CreateArticleAsync(draft);

            _session.ArticleId = articleId;
            _session.LastTitle = draft.Title;
            _session.Save();
            _aidBox.Text = "cv" + articleId;

            Log(update ? "✓ 文章更新成功！" : "✓ 文章发布成功！");
            Log("文章地址：https://www.bilibili.com/read/cv" + articleId);
        }
        catch (Exception ex)
        {
            Log("✗ 操作失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string> ResolveImageAsync(string src)
    {
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return src;
        }
        if (_readmeDir == null)
        {
            throw new InvalidOperationException("找不到 README 所在目录，无法上传图片：" + src);
        }

        var full = Path.GetFullPath(Path.Combine(_readmeDir, src.Trim().TrimStart('/')));
        if (!File.Exists(full))
        {
            throw new InvalidOperationException("找不到要上传的图片：" + full);
        }
        Log("正在上传图片：" + Path.GetFileName(full) + " …");
        var url = await _client.UploadImageAsync(full);
        Log("图片已上传：" + url);
        return url;
    }

    private async Task PickArticleAsync()
    {
        if (_busy)
        {
            return;
        }
        try
        {
            Log("正在拉取我的专栏列表…");
            var list = await _client.ListMyArticlesAsync();
            if (list.Count == 0)
            {
                Log("没有找到已发布的专栏文章，先点“发布新文章”创建一篇吧。");
                return;
            }

            using var dialog = new Form
            {
                Text = "选择要更新的文章",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(480, 360),
                BackColor = Bg,
                ForeColor = TextColor,
                Font = Font
            };
            var listBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = PanelBg,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            foreach (var item in list)
            {
                listBox.Items.Add(new ListItem(item.Id, item.Title));
            }
            var okBtn = new Button
            {
                Text = "选择",
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Blue,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            dialog.Controls.Add(listBox);
            dialog.Controls.Add(okBtn);

            if (dialog.ShowDialog(this) == DialogResult.OK && listBox.SelectedItem is ListItem selected)
            {
                _aidBox.Text = "cv" + selected.Id;
                Log($"已选择：cv{selected.Id} {selected.Title}");
            }
        }
        catch (Exception ex)
        {
            Log("拉取专栏列表失败：" + ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _publishBtn.Enabled = !busy;
        _updateBtn.Enabled = !busy;
        _pickBtn.Enabled = !busy;
        _reloginBtn.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private static long ParseCv(string text)
    {
        var match = CvRegex().Match(text);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    private static (string? Markdown, string? Dir) LoadReadme()
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        candidates.Add(Path.Combine(baseDir, "README.md"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "README.md"));
        for (var i = 0; i < 6 && current != null; i++)
        {
            candidates.Add(Path.Combine(current.FullName, "README.md"));
            current = current.Parent;
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return (text, Path.GetDirectoryName(path));
                    }
                }
                catch
                {
                    // 尝试下一个候选位置
                }
            }
        }
        return (null, null);
    }

    private static string FirstSentence(string markdown)
    {
        var line = markdown
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 20 && !l.StartsWith("#", StringComparison.Ordinal));
        line = Regex.Replace(line ?? "", @"[#*`>\[\]()]", "");
        return line.Length > 200 ? line[..200] : line;
    }

    private static string BuildFallbackContent()
    {
        return """
            # DeepSeek 用量便签

            一个藏在系统托盘里的 Windows 小工具：双击托盘图标弹出/隐藏便签卡片，实时显示 DeepSeek 网页版账户的余额、今日使用量与本月使用量。

            ## 功能

            - 托盘常驻：双击显示/隐藏，右键菜单支持刷新、设置、退出
            - 三个标签页：总概（余额、今日/本月用量）、实时（最近 1 分钟 Flash/Pro 消耗与费用）、价格（峰谷计价表）
            - 置顶开关、登录方式为内嵌网页扫码，无需手动配置 API Key

            ## 安装

            1. 到 GitHub Releases 页面下载最新版 DeepSeekUsageTray-win-x64.exe
            2. 双击运行，首次使用扫码登录 DeepSeek 网页版即可
            3. 之后通过右下角托盘图标随时打开/隐藏

            ## 隐私说明

            登录凭证仅保存在本机，不会上传到任何服务器。

            """;
    }

    private sealed record ListItem(long Id, string Title)
    {
        public override string ToString() => $"cv{Id}  {Title}";
    }

    [GeneratedRegex(@"cv(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CvRegex();

    #endregion
}
