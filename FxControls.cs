using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

/// <summary>
/// 底部状态栏：浅灰轨道 + 蓝色进度填充 + 居中文字。
/// 进度按 60 秒刷新周期从 0 填满到 1。
/// </summary>
internal sealed class ProgressStatusBar : Control
{
    private float _progress;

    public ProgressStatusBar()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        ForeColor = MainForm.TextGray;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Progress
    {
        get => _progress;
        set
        {
            _progress = Math.Clamp(value, 0f, 1f);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(MainForm.PanelColor);

        if (_progress > 0.001f)
        {
            var width = (int)(Width * _progress);
            using var brush = new SolidBrush(Color.FromArgb(150, MainForm.AccentBlue));
            g.FillRectangle(brush, 0, 0, width, Height);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// 总概页画布：在数据数字上方绘制“+N”绿色数字并向上飘升渐隐。
/// 画在面板自身表面上，不会遮挡子控件文字。
/// </summary>
internal sealed class FxPanel : Panel
{
    internal static readonly Color FxGreen = Color.FromArgb(0, 230, 118);

    private readonly List<FloatingFx> _fx = new();
    private readonly System.Windows.Forms.Timer _fxTimer;
    private readonly Font _fxFont = new("Segoe UI", 11f, FontStyle.Bold);

    public FxPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = MainForm.BgColor;
        _fxTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _fxTimer.Tick += (_, _) =>
        {
            if (_fx.Count > 0)
            {
                Invalidate();
            }
        };
    }

    /// <summary>从 start 位置向上飘出一个绿色 +N 数字。</summary>
    public void SpawnFloating(string text, Point start)
    {
        _fx.Add(new FloatingFx { Text = text, X = start.X, StartY = start.Y, Born = DateTime.Now });
        if (!_fxTimer.Enabled)
        {
            _fxTimer.Start();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_fx.Count == 0)
        {
            return;
        }

        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        for (var i = _fx.Count - 1; i >= 0; i--)
        {
            var fx = _fx[i];
            var elapsed = (DateTime.Now - fx.Born).TotalSeconds;
            if (elapsed >= 1.5)
            {
                _fx.RemoveAt(i);
                continue;
            }

            var p = (float)(elapsed / 1.5);
            var alpha = (int)(255 * (1 - p) * (1 - p));
            var y = (int)(fx.StartY - 30 * p);
            using var brush = new SolidBrush(Color.FromArgb(alpha, FxGreen));
            g.DrawString(fx.Text, _fxFont, brush, fx.X, y);
        }

        if (_fx.Count == 0)
        {
            _fxTimer.Stop();
        }
    }

    private sealed class FloatingFx
    {
        public string Text = string.Empty;
        public int X;
        public int StartY;
        public DateTime Born;
    }
}
