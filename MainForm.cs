using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

internal sealed class MainForm : Form
{
    internal static readonly Color BgColor = Color.FromArgb(27, 27, 28);
    internal static readonly Color PanelColor = Color.FromArgb(42, 42, 45);
    private static readonly Color BorderColor = Color.FromArgb(58, 58, 62);
    internal static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
    internal static readonly Color TextWhite = Color.FromArgb(243, 244, 246);
    internal static readonly Color TextGray = Color.FromArgb(156, 163, 175);
    internal static readonly Color TextError = Color.FromArgb(248, 113, 113);
    private static readonly Color PricePeakColor = Color.FromArgb(239, 68, 68);
    private static readonly Color PriceOffPeakColor = Color.FromArgb(34, 197, 94);

    private readonly AppConfig _config;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _countdownTimer;
    private readonly System.Windows.Forms.Timer _progressTimer;
    private readonly Dictionary<Label, System.Windows.Forms.Timer> _flashTimers = new();
    private float _progress;
    private long _lastTodayTokens;
    private long _lastTodayRequests;
    private double _lastTodayCost;
    private bool _hasTodayBaseline;

    private Label _balanceLabel = new();
    private Label _todayTokens = new();
    private Label _todayRequests = new();
    private Label _todayCost = new();
    private Label _monthTokens = new();
    private Label _monthRequests = new();
    private Label _monthCost = new();
    private ProgressStatusBar _status = new();
    private Label _liveNote = new();
    private Label _priceNote = new();
    private Label _priceStatus = new();
    private Label _priceCountdown = new();
    private bool _refreshing;
    private bool _pinned = true;
    private readonly Dictionary<string, ModelTokenUsage> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _lastSample;
    private readonly Label[] _flash = new Label[8];
    private readonly Label[] _pro = new Label[8];
    private TabButton _tabOverview = null!;
    private TabButton _tabLive = null!;
    private TabButton _tabPrice = null!;
    private FxPanel _overviewPanel = null!;
    private Panel _livePanel = null!;
    private Panel _pricePanel = null!;
    private Panel _separatorLeft = null!;
    private Panel _separatorRight = null!;
    private static readonly int[] LiveCenters = { 82, 160, 238, 316 };

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? OnOpenSettings { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? OnNeedLogin { get; set; }

    public MainForm(AppConfig config)
    {
        _config = config;
        AutoScaleMode = AutoScaleMode.None;
        BuildUi();
        LoadPosition();

        Shown += (_, _) =>
        {
            RefreshNow();
            if (string.IsNullOrWhiteSpace(_config.PlatformToken))
            {
                SetStatus("首次使用：请先扫码登录 DeepSeek", true);
                OnNeedLogin?.Invoke();
            }
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SavePosition();
                Hide();
            }
        };

        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();

        // 底部进度条：每 1 秒前进 1/60，60 秒填满，刷新完成后归零重来
        _progressTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _progressTimer.Tick += (_, _) =>
        {
            _progress = Math.Min(1f, _progress + 1f / 60f);
            _status.Progress = _progress;
        };
        _progressTimer.Start();

        // 价格页状态倒计时：每秒更新当前高峰/低峰状态与剩余时间
        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _countdownTimer.Tick += (_, _) => UpdatePriceStatus();
        _countdownTimer.Start();

        // 启动后 10 秒做一次预热刷新，让实时页尽快有第一组数据
        var warmup = new System.Windows.Forms.Timer { Interval = 10_000 };
        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            RefreshNow();
        };
        warmup.Start();
    }

    private void BuildUi()
    {
        SuspendLayout();
        Text = "DeepSeek 用量";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(360, 364);
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9f);

        // 自绘标题栏：鲸鱼 + 名称 + 关闭按钮，整条可拖动
        var titleBar = new Panel
        {
            Bounds = new Rectangle(0, 0, 360, 36),
            BackColor = PanelColor
        };
        titleBar.MouseDown += TitleBar_MouseDown;

        var whale = new PictureBox
        {
            Image = LoadWhaleImage(),
            Size = new Size(20, 20),
            Location = new Point(10, 8),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        whale.MouseDown += TitleBar_MouseDown;

        var headerTitle = new Label
        {
            Text = "DeepSeek 用量",
            AutoSize = true,
            Location = new Point(36, 9),
            ForeColor = TextWhite,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };
        headerTitle.MouseDown += TitleBar_MouseDown;

        var closeButton = new Button
        {
            Text = "✕",
            Size = new Size(32, 26),
            Location = new Point(322, 5),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = BorderColor },
            UseVisualStyleBackColor = false,
            BackColor = Color.FromArgb(239, 68, 68),
            ForeColor = Color.White
        };
        closeButton.Click += (_, _) => Close();
        closeButton.MouseEnter += (_, _) =>
        {
            closeButton.BackColor = Color.FromArgb(220, 38, 38);
            closeButton.ForeColor = Color.White;
        };
        closeButton.MouseLeave += (_, _) =>
        {
            closeButton.BackColor = Color.FromArgb(239, 68, 68);
            closeButton.ForeColor = Color.White;
        };

        // 鲸鱼、标题、关闭按钮都放进标题栏面板里，保证显示在面板之上
        titleBar.Controls.Add(whale);
        titleBar.Controls.Add(headerTitle);
        titleBar.Controls.Add(closeButton);

        // 正文区域：带浅灰边框的面板，包裹按钮行和所有数据
        var bodyPanel = new BorderPanel
        {
            Bounds = new Rectangle(0, 40, ClientSize.Width, ClientSize.Height - 40),
            BackColor = BgColor
        };

        // 按钮行
        var refresh = MakeToolButton("刷新", 12, 4);
        var hide = MakeToolButton("隐藏", 76, 4);
        var pin = MakeToolButton("置顶", 140, 4);
        var logout = MakeToolButton("注销", 204, 4);
        refresh.Click += (_, _) => RefreshNow();
        hide.Click += (_, _) => Hide();
        pin.Click += (_, _) => TogglePin(pin);
        logout.Click += (_, _) => Logout();
        // 默认启动即打开置顶
        TopMost = _pinned;
        pin.BackColor = AccentBlue;
        pin.ForeColor = Color.White;

        // 标签页：总概 / 实时（咬合式：横线只在选中页签两侧延伸）
        _tabOverview = MakeTabButton("总概", 12);
        _tabLive = MakeTabButton("实时", 106);
        _tabPrice = MakeTabButton("价格", 200);
        _tabOverview.Selected = true;
        _tabOverview.BackColor = BgColor;
        _tabOverview.ForeColor = TextWhite;
        _tabLive.Selected = false;
        _tabLive.BackColor = PanelColor;
        _tabLive.ForeColor = TextGray;
        _tabPrice.Selected = false;
        _tabPrice.BackColor = PanelColor;
        _tabPrice.ForeColor = TextGray;

        _separatorLeft = new Panel
        {
            Bounds = new Rectangle(0, 65, 12, 1),
            BackColor = BorderColor
        };
        _separatorRight = new Panel
        {
            Bounds = new Rectangle(102, 65, ClientSize.Width - 102, 1),
            BackColor = BorderColor
        };

        _overviewPanel = new FxPanel
        {
            Bounds = new Rectangle(1, 66, ClientSize.Width - 2, 230),
            BackColor = BgColor
        };
        _livePanel = new Panel
        {
            Bounds = new Rectangle(1, 66, ClientSize.Width - 2, 230),
            BackColor = BgColor,
            Visible = false
        };
        _pricePanel = new PriceTablePanel
        {
            Bounds = new Rectangle(1, 66, ClientSize.Width - 2, 230),
            BackColor = BgColor,
            Visible = false
        };
        _tabOverview.Click += (_, _) => SelectTab(0);
        _tabLive.Click += (_, _) => SelectTab(1);
        _tabPrice.Click += (_, _) => SelectTab(2);

        // 总概页：账户余额 + 今日使用 + 本月使用
        var balanceCaption = MakeCaption("账户余额", 12, 0);
        _balanceLabel.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        _balanceLabel.ForeColor = TextWhite;
        _balanceLabel.AutoSize = true;
        _balanceLabel.Location = new Point(12, 16);
        _balanceLabel.Text = "--";

        var todayTitle = MakeTitle("今日使用", 12, 64);
        var monthTitle = MakeTitle("本月使用", 12, 150);

        CreateStat(_overviewPanel, 76, 94, ref _todayTokens, "Tokens");
        CreateStat(_overviewPanel, 180, 94, ref _todayRequests, "请求次数");
        CreateStat(_overviewPanel, 284, 94, ref _todayCost, "消费金额");

        CreateStat(_overviewPanel, 76, 180, ref _monthTokens, "Tokens");
        CreateStat(_overviewPanel, 180, 180, ref _monthRequests, "请求次数");
        CreateStat(_overviewPanel, 284, 180, ref _monthCost, "消费金额");

        _overviewPanel.Controls.AddRange(new Control[]
        {
            balanceCaption,
            _balanceLabel,
            todayTitle,
            monthTitle
        });

        // 实时页：每个模型 3 列（总/命中/未命中）× 2 行（最近 1 分钟的消耗与费用）
        CreateLiveSection(_livePanel, "DeepSeek-v4-Flash", 6, _flash);
        CreateLiveSection(_livePanel, "DeepSeek-v4-Pro", 112, _pro);

        // 价格页：顶部状态行（当前高峰/低峰价 + 倒计时）+ 官方价格表
        _priceStatus.AutoSize = true;
        _priceStatus.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        _priceStatus.Location = new Point(12, 10);
        _priceCountdown.AutoSize = true;
        _priceCountdown.Font = new Font("Microsoft YaHei UI", 8f);
        _priceCountdown.ForeColor = TextGray;
        _priceCountdown.Location = new Point(0, 10);
        _pricePanel.Controls.Add(_priceStatus);
        _pricePanel.Controls.Add(_priceCountdown);
        UpdatePriceStatus();

        var priceHeaderFont = new Font("Microsoft YaHei UI", 8f);
        _pricePanel.Controls.Add(new Label
        {
            Text = "计费项",
            AutoSize = true,
            ForeColor = TextGray,
            Font = priceHeaderFont,
            Location = new Point(12, 36)
        });
        AddPriceHeader(_pricePanel, "Flash", 190, priceHeaderFont, 36);
        AddPriceHeader(_pricePanel, "Pro", 285, priceHeaderFont, 36);
        AddPriceRow(_pricePanel, "输入（缓存命中）", "0.02元", "0.025元", 66);
        AddPriceRow(_pricePanel, "输入（缓存未命中）", "1元", "3元", 102);
        AddPriceRow(_pricePanel, "输出", "2元", "6元", 138);
        _pricePanel.Controls.Add(new Label
        {
            Text = "单价：元 / 百万 tokens",
            AutoSize = true,
            ForeColor = TextGray,
            Font = new Font("Microsoft YaHei UI", 7f),
            Location = new Point(12, 176)
        });

        _status.AutoSize = false;
        _status.Bounds = new Rectangle(1, bodyPanel.Height - 24, bodyPanel.Width - 2, 23);
        _status.ForeColor = TextGray;
        _status.Text = "准备就绪";

        // 实时页底部栏：最近 1 分钟
        _liveNote.AutoSize = false;
        _liveNote.Bounds = new Rectangle(1, bodyPanel.Height - 24, bodyPanel.Width - 2, 23);
        _liveNote.TextAlign = ContentAlignment.MiddleCenter;
        _liveNote.BackColor = PanelColor;
        _liveNote.ForeColor = TextGray;
        _liveNote.Text = "最近 1 分钟";
        _liveNote.Visible = false;

        // 价格页底部栏：峰谷说明
        _priceNote.AutoSize = false;
        _priceNote.Bounds = new Rectangle(1, bodyPanel.Height - 24, bodyPanel.Width - 2, 23);
        _priceNote.TextAlign = ContentAlignment.MiddleCenter;
        _priceNote.BackColor = PanelColor;
        _priceNote.ForeColor = TextGray;
        _priceNote.Font = new Font("Microsoft YaHei UI", 8f);
        _priceNote.Text = "高峰时段（9:00-12:00、14:00-18:00）价格 ×2";
        _priceNote.Visible = false;

        Controls.AddRange(new Control[]
        {
            titleBar,
            bodyPanel
        });
        bodyPanel.Controls.AddRange(new Control[]
        {
            refresh,
            hide,
            pin,
            logout,
            _tabOverview,
            _tabLive,
            _tabPrice,
            _separatorLeft,
            _separatorRight,
            _overviewPanel,
            _livePanel,
            _pricePanel,
            _status,
            _liveNote,
            _priceNote
        });
        ResumeLayout();
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
    }

    // 带 1px 浅灰边框的面板，边框颜色与“刷新”等按钮一致
    private sealed class BorderPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // 价格表面板：用点状虚线画表格分割线
    private sealed class PriceTablePanel : Panel
    {
        public PriceTablePanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor) { DashStyle = DashStyle.Dot };
            foreach (var y in new[] { 32, 58, 94, 130, 164 })
            {
                e.Graphics.DrawLine(pen, 1, y, Width - 2, y);
            }

            foreach (var x in new[] { 150, 240 })
            {
                e.Graphics.DrawLine(pen, x, 32, x, 164);
            }
        }
    }

    private static Button MakeToolButton(string text, int x, int y)
        => new()
        {
            Text = text,
            Size = new Size(58, 30),
            Location = new Point(x, y),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = BorderColor },
            UseVisualStyleBackColor = false,
            BackColor = PanelColor,
            ForeColor = TextWhite
        };

    private static TabButton MakeTabButton(string text, int x)
        => new()
        {
            Text = text,
            Size = new Size(90, 27),
            Location = new Point(x, 39),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            UseVisualStyleBackColor = false,
            BackColor = PanelColor,
            ForeColor = TextGray
        };

    // 页签按钮：选中时画上/左/右三边框（底部留空与内容页咬合），未选中时画完整边框
    private sealed class TabButton : Button
    {
        [DefaultValue(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(BackColor))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using var pen = new Pen(BorderColor);
            g.DrawLine(pen, 0, 0, Width - 1, 0);
            g.DrawLine(pen, 0, 0, 0, Height - 1);
            g.DrawLine(pen, Width - 1, 0, Width - 1, Height - 1);
            if (!Selected)
            {
                g.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
            }
        }
    }

    private void SelectTab(int index)
    {
        _overviewPanel.Visible = index == 0;
        _livePanel.Visible = index == 1;
        _pricePanel.Visible = index == 2;
        _status.Visible = index == 0;
        _liveNote.Visible = index == 1;
        _priceNote.Visible = index == 2;
        StyleTab(_tabOverview, index == 0, 12);
        StyleTab(_tabLive, index == 1, 106);
        StyleTab(_tabPrice, index == 2, 200);
    }

    private void StyleTab(TabButton tab, bool selected, int x)
    {
        tab.Selected = selected;
        tab.BackColor = selected ? BgColor : PanelColor;
        tab.ForeColor = selected ? TextWhite : TextGray;
        if (selected)
        {
            _separatorLeft.Bounds = new Rectangle(0, 65, x, 1);
            _separatorRight.Bounds = new Rectangle(x + 90, 65, ClientSize.Width - x - 90, 1);
        }
        tab.Invalidate();
    }

    private void CreateLiveSection(Control parent, string modelName, int y, Label[] cells)
    {
        var title = MakeTitle(modelName, 12, y);
        parent.Controls.Add(title);

        var headers = new[] { "总", "命中", "未命中", "输出" };
        var headerFont = new Font("Microsoft YaHei UI", 8f);
        for (var i = 0; i < 4; i++)
        {
            var width = TextRenderer.MeasureText(headers[i], headerFont).Width;
            parent.Controls.Add(new Label
            {
                Text = headers[i],
                AutoSize = true,
                ForeColor = TextGray,
                Font = headerFont,
                Location = new Point(LiveCenters[i] - width / 2, y + 24)
            });
        }

        var row1Y = y + 48;
        var row2Y = y + 74;
        var valueFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        var labelFont = new Font("Microsoft YaHei UI", 8f);
        var valueHeight = TextRenderer.MeasureText("0.0000", valueFont).Height;
        var labelHeight = TextRenderer.MeasureText("每秒消耗", labelFont).Height;
        var labelOffset = Math.Max(0, (valueHeight - labelHeight) / 2);

        AddRowLabel(parent, "消耗", "万", row1Y + labelOffset);
        AddRowLabel(parent, "费用", "元", row2Y + labelOffset);

        for (var i = 0; i < 4; i++)
        {
            cells[i] = MakeLiveValue(LiveCenters[i], row1Y);
            cells[i + 4] = MakeLiveValue(LiveCenters[i], row2Y);
            parent.Controls.Add(cells[i]);
            parent.Controls.Add(cells[i + 4]);
        }
    }

    // 行标签：主文字 + 右上角小角标单位（如“消耗”右上角“万”）
    private static void AddRowLabel(Control parent, string text, string unit, int y)
    {
        var mainFont = new Font("Microsoft YaHei UI", 8f);
        var unitFont = new Font("Microsoft YaHei UI", 6f);
        var mainSize = TextRenderer.MeasureText(text, mainFont);

        parent.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(12, y),
            ForeColor = TextGray,
            Font = mainFont
        });
        parent.Controls.Add(new Label
        {
            Text = unit,
            AutoSize = true,
            ForeColor = TextGray,
            Font = unitFont,
            Location = new Point(12 + mainSize.Width + 1, y)
        });
    }

    private static Label MakeLiveValue(int centerX, int y)
        => new()
        {
            Text = "--",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = TextWhite,
            AutoSize = true,
            Location = new Point(centerX - 20, y)
        };

    private void TogglePin(Button pin)
    {
        _pinned = !_pinned;
        TopMost = _pinned;
        if (_pinned)
        {
            pin.BackColor = AccentBlue;
            pin.ForeColor = Color.White;
        }
        else
        {
            pin.BackColor = PanelColor;
            pin.ForeColor = TextWhite;
        }
    }

    private void Logout()
    {
        var confirm = MessageBox.Show(
            this,
            "确定要退出登录吗？退出后需要重新扫码登录。",
            "退出登录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _config.PlatformToken = string.Empty;
        ConfigStore.Save(_config);
        LoginForm.ClearPersistentData();
        SetStatus("已退出登录，请重新扫码登录", false);
        OnNeedLogin?.Invoke();
    }

    private static Label MakeCaption(string text, int x, int y)
        => new()
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = TextGray
        };

    private static Label MakeTitle(string text, int x, int y)
        => new()
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = AccentBlue,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };

    private void CreateStat(Control parent, int centerX, int y, ref Label valueLabel, string caption)
    {
        valueLabel = new Label
        {
            Text = "--",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = TextWhite,
            AutoSize = true,
            Location = new Point(centerX - 20, y)
        };
        var captionLabel = new Label
        {
            Text = caption,
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = TextGray,
            AutoSize = true,
            Location = new Point(centerX - TextRenderer.MeasureText(caption, new Font("Microsoft YaHei UI", 8f)).Width / 2, y + 28)
        };
        parent.Controls.Add(valueLabel);
        parent.Controls.Add(captionLabel);
    }

    private static void CenterLabel(Label label, int centerX)
    {
        var width = TextRenderer.MeasureText(label.Text, label.Font).Width;
        label.Location = new Point(centerX - width / 2, label.Location.Y);
    }

    private static Image? LoadWhaleImage()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DeepSeekUsageTray.whale.png");
            if (stream == null)
            {
                return null;
            }
            using var temp = Image.FromStream(stream);
            return new Bitmap(temp);
        }
        catch
        {
            return null;
        }
    }

    private void LoadPosition()
    {
        if (_config.WindowX >= 0 && _config.WindowY >= 0)
        {
            Location = new Point(_config.WindowX, _config.WindowY);
        }
        else
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        }
    }

    public void SavePosition()
    {
        _config.WindowX = Location.X;
        _config.WindowY = Location.Y;
    }

    private Task? _refreshTask;

    public void RefreshNow()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshTask = DoRefreshAsync();
    }

    internal Task RefreshAndWaitAsync()
    {
        RefreshNow();
        return _refreshTask ?? Task.CompletedTask;
    }

    private async Task DoRefreshAsync()
    {
        _refreshing = true;
        SetStatus("正在刷新…", false);
        try
        {
            var client = new DeepSeekClient();
            var summary = await client.FetchAsync(_config.PlatformToken);
            Render(summary);
            UpdateLive(summary);
            SetStatus($"更新于 {summary.UpdatedAt:MM-dd HH:mm:ss} · 每 60 秒自动刷新", false);
        }
        catch (DeepSeekException ex)
        {
            SetStatus(ex.Message, true);
        }
        catch (Exception ex)
        {
            SetStatus("刷新失败：" + ex.Message, true);
        }
        finally
        {
            _refreshing = false;
            _progress = 0f;
            _status.Progress = 0f;
        }
    }

    // 截图模式：把三个标签页渲染成 PNG，供 README 与自动发帖使用
    internal async Task CaptureScreenshotsAsync(string outDir)
    {
        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        await RefreshAndWaitAsync(); // 第二次采样，让"实时"页有真实增量数据
        await Task.Delay(TimeSpan.FromSeconds(2));

        SaveScreenshot(0, Path.Combine(outDir, "overview.png"));
        SaveScreenshot(1, Path.Combine(outDir, "live.png"));
        await Task.Delay(TimeSpan.FromSeconds(1));
        SaveScreenshot(2, Path.Combine(outDir, "price.png"));
    }

    private void SaveScreenshot(int tabIndex, string path)
    {
        SelectTab(tabIndex);
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void Render(UsageSummary summary)
    {
        if (summary.BalanceConfigured)
        {
            _balanceLabel.Text = (summary.Currency == "CNY" ? "¥ " : "$ ") + summary.Balance.ToString("N2");
        }
        else
        {
            _balanceLabel.Text = "未设置";
        }

        if (summary.UsageConfigured)
        {
            var newTokens = summary.TodayTokens;
            var newRequests = summary.TodayRequests;
            var newCost = summary.TodayCost;

            _todayTokens.Text = FormatTokens(newTokens);
            _todayRequests.Text = newRequests.ToString("N0");
            _todayCost.Text = FormatCost(newCost, summary.Currency);
            _monthTokens.Text = FormatTokens(summary.MonthTokens);
            _monthRequests.Text = summary.MonthRequests.ToString("N0");
            _monthCost.Text = FormatCost(summary.MonthCost, summary.Currency);
            CenterLabel(_todayTokens, 76);
            CenterLabel(_todayRequests, 180);
            CenterLabel(_todayCost, 284);
            CenterLabel(_monthTokens, 76);
            CenterLabel(_monthRequests, 180);
            CenterLabel(_monthCost, 284);

            if (_hasTodayBaseline)
            {
                var diffTokens = newTokens - _lastTodayTokens;
                var diffRequests = newRequests - _lastTodayRequests;
                var diffCost = newCost - _lastTodayCost;

                if (diffTokens > 0)
                {
                    SpawnTodayFx(_todayTokens, "+" + FormatTokens(diffTokens));
                }
                if (diffRequests > 0)
                {
                    SpawnTodayFx(_todayRequests, "+" + diffRequests.ToString("N0"));
                }
                if (diffCost > 0)
                {
                    SpawnTodayFx(_todayCost, "+" + FormatCost(diffCost, summary.Currency).Replace(" ", ""));
                }

                if (diffTokens > 0)
                {
                    FlashGreen(_todayTokens);
                }
                if (diffRequests > 0)
                {
                    FlashGreen(_todayRequests);
                }
                if (diffCost > 0)
                {
                    FlashGreen(_todayCost);
                }
            }
            else
            {
                _hasTodayBaseline = true;
            }

            _lastTodayTokens = newTokens;
            _lastTodayRequests = newRequests;
            _lastTodayCost = newCost;
        }
        else
        {
            _hasTodayBaseline = false;
            _todayTokens.Text = "--";
            _todayRequests.Text = "--";
            _todayCost.Text = "--";
            _monthTokens.Text = "--";
            _monthRequests.Text = "--";
            _monthCost.Text = "--";
        }
    }

    private static bool IsPeakHour(DateTime time)
    {
        var hour = time.Hour;
        return (hour >= 9 && hour < 12) || (hour >= 14 && hour < 18);
    }

    private static void AddPriceHeader(Control parent, string text, int centerX, Font font, int y)
    {
        var width = TextRenderer.MeasureText(text, font).Width;
        parent.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = TextWhite,
            Font = font,
            Location = new Point(centerX - width / 2, y)
        });
    }

    private static void AddPriceRow(Control parent, string label, string flashPrice, string proPrice, int y)
    {
        var labelFont = new Font("Microsoft YaHei UI", 8f);
        var valueFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        parent.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = TextGray,
            Font = labelFont,
            Location = new Point(12, y)
        });
        foreach (var (price, centerX) in new[] { (flashPrice, 190), (proPrice, 285) })
        {
            var value = new Label
            {
                Text = price,
                AutoSize = true,
                ForeColor = TextWhite,
                Font = valueFont
            };
            parent.Controls.Add(value);
            value.Location = new Point(centerX - value.Width / 2, y);
        }
    }

    private void UpdatePriceStatus()
    {
        var now = DateTime.Now;
        var peak = IsPeakHour(now);
        _priceStatus.Text = peak ? "当前高峰价" : "当前低峰价";
        _priceStatus.ForeColor = peak ? PricePeakColor : PriceOffPeakColor;

        var remaining = GetPeriodEnd(now) - now;
        _priceCountdown.Text = $"（倒计时：{remaining:hh\\:mm\\:ss}）";
        var statusWidth = TextRenderer.MeasureText(_priceStatus.Text, _priceStatus.Font).Width;
        _priceCountdown.Location = new Point(12 + statusWidth + 4, 10);
    }

    private static DateTime GetPeriodEnd(DateTime now)
    {
        var today = now.Date;
        var hour = now.Hour;
        if (hour >= 9 && hour < 12)
        {
            return today.AddHours(12);
        }

        if (hour >= 14 && hour < 18)
        {
            return today.AddHours(18);
        }

        if (hour >= 12 && hour < 14)
        {
            return today.AddHours(14);
        }

        return today.AddDays(1).AddHours(9);
    }

    private void UpdateLive(UsageSummary summary)
    {
        var now = DateTime.Now;
        if (_lastSample is DateTime last)
        {
            var elapsed = (now - last).TotalSeconds;
            if (elapsed >= 5 && elapsed <= 600)
            {
                var tier = IsPeakHour(now) ? 2.0 : 1.0;
                ComputeLiveModel(summary, _flash, "deepseek-v4-flash", tier);
                ComputeLiveModel(summary, _pro, "deepseek-v4-pro", tier);
            }
            else if (elapsed > 600)
            {
                // 间隔过长（休眠等），视为基线失效，重置显示
                ResetLive("--");
            }
        }
        else
        {
            // 第一次采样只建立基线，实时数据要等下一分钟才出现
            ResetLive("--");
        }

        _lastSample = now;
        _previous.Clear();
        foreach (var item in summary.Models)
        {
            _previous[item.Key] = item.Value;
        }
    }

    private void ComputeLiveModel(UsageSummary summary, Label[] cells, string model, double tier)
    {
        if (!_previous.TryGetValue(model, out var prev) || !summary.Models.TryGetValue(model, out var cur))
        {
            return;
        }

        var hit = Math.Max(0, cur.HitTokens - prev.HitTokens);
        var miss = Math.Max(0, cur.MissTokens - prev.MissTokens);
        var response = Math.Max(0, cur.ResponseTokens - prev.ResponseTokens);

        // 平台统计有延迟：若本分钟差值全为 0，保留上一次非零数据，避免实时页归零
        if (hit == 0 && miss == 0 && response == 0)
        {
            return;
        }

        var isFlash = model.Contains("flash", StringComparison.OrdinalIgnoreCase);

        // 官方价格（元 / 百万 tokens），高峰 = 平时 × 2
        var hitPrice = tier * (isFlash ? 0.02 : 0.025);
        var missPrice = tier * (isFlash ? 1.0 : 3.0);
        var responsePrice = tier * (isFlash ? 2.0 : 6.0);

        // 价格按“每百万 token”计，先除以 100 万再乘单价
        var hitCost = hit / 1_000_000.0 * hitPrice;
        var missCost = miss / 1_000_000.0 * missPrice;
        var responseCost = response / 1_000_000.0 * responsePrice;

        cells[0].Text = FormatTokensWan((hit + miss + response) / 10_000.0);
        cells[1].Text = FormatTokensWan(hit / 10_000.0);
        cells[2].Text = FormatTokensWan(miss / 10_000.0);
        cells[3].Text = FormatTokensWan(response / 10_000.0);
        cells[4].Text = FormatCostAmount(hitCost + missCost + responseCost);
        cells[5].Text = FormatCostAmount(hitCost);
        cells[6].Text = FormatCostAmount(missCost);
        cells[7].Text = FormatCostAmount(responseCost);

        for (var i = 0; i < cells.Length; i++)
        {
            CenterLabel(cells[i], LiveCenters[i % 4]);
        }
    }

    private void ResetLive(string text)
    {
        foreach (var cell in _flash)
        {
            cell.Text = text;
        }
        foreach (var cell in _pro)
        {
            cell.Text = text;
        }
    }

    private static string FormatCostAmount(double cost)
    {
        if (cost >= 1)
        {
            return cost.ToString("0.000");
        }
        if (cost >= 0.0001)
        {
            return cost.ToString("0.0000");
        }
        if (cost > 0)
        {
            return cost.ToString("0.00000");
        }
        return "0.0000";
    }

    private static string FormatTokensWan(double wan)
    {
        if (wan >= 0.01)
        {
            return wan.ToString("0.00");
        }
        if (wan > 0)
        {
            return wan.ToString("0.0000");
        }
        return "0.00";
    }

    private void SetStatus(string text, bool isError)
    {
        _status.Text = text;
        _status.ForeColor = isError ? TextError : TextGray;
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 100_000_000)
        {
            return (tokens / 100_000_000.0).ToString("0.00") + " 亿";
        }
        if (tokens >= 10_000)
        {
            return (tokens / 10_000.0).ToString("0.#") + " 万";
        }
        return tokens.ToString("N0");
    }

    private static string FormatCost(double cost, string currency)
        => (currency == "CNY" ? "¥ " : "$ ") + cost.ToString("N2");

    private void SpawnTodayFx(Label label, string text)
        => _overviewPanel.SpawnFloating(text, new Point(label.Left, label.Top - 8));

    // 数字从亮绿色渐变回白色，模拟“打击反馈”
    private void FlashGreen(Label label)
    {
        if (_flashTimers.TryGetValue(label, out var old))
        {
            old.Stop();
            old.Dispose();
            _flashTimers.Remove(label);
        }

        var start = DateTime.Now;
        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        _flashTimers[label] = timer;
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.Now - start).TotalSeconds / 1.2;
            if (t >= 1)
            {
                timer.Stop();
                timer.Dispose();
                _flashTimers.Remove(label);
                label.ForeColor = TextWhite;
                return;
            }

            var eased = 1 - (1 - t) * (1 - t);
            label.ForeColor = Lerp(FxPanel.FxGreen, TextWhite, (float)eased);
        };
        timer.Start();
    }

    private static Color Lerp(Color from, Color to, float t) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * t),
        (int)(from.G + (to.G - from.G) * t),
        (int)(from.B + (to.B - from.B) * t));
}
