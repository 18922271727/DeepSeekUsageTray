using System;
using System.Drawing;
using System.Reflection;

namespace DeepSeekUsageTray;

internal static class IconFactory
{
    private static Icon? _cached;

    public static Icon Create()
    {
        if (_cached != null)
        {
            return _cached;
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DeepSeekUsageTray.whale.ico");
            if (stream != null)
            {
                _cached = new Icon(stream);
                return _cached;
            }
        }
        catch
        {
            // fall back to a simple drawn icon below
        }

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(77, 107, 254));
            graphics.FillEllipse(background, 1, 1, 30, 30);
        }

        _cached = Icon.FromHandle(bitmap.GetHicon());
        return _cached;
    }
}
