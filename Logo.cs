using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;

namespace Windmill;

/// <summary>The app logo from Windmill.svg: one petal shape drawn four times, rotated a quarter turn each.</summary>
internal static class Logo
{
    // Each petal's colour and its transform from the SVG (matrix a, b, c, d, e, f), on a 1024-unit canvas.
    static readonly (SD.Color Color, float[] M)[] Petals =
    {
        (SD.Color.FromArgb(121, 188, 120), new float[] { 1, 0, 0, 1, 0, 0 }),         // green, top right
        (SD.Color.FromArgb(73, 173, 255), new float[] { -1, 0, 0, -1, 1024, 1024 }),  // blue, bottom left
        (SD.Color.FromArgb(255, 202, 70), new float[] { 0, 1, -1, 0, 1024, 0 }),      // yellow, bottom right
        (SD.Color.FromArgb(255, 124, 124), new float[] { 0, -1, 1, 0, 0, 1024 }),     // red, top left
    };

    static SD2.GraphicsPath Petal()
    {
        var p = new SD2.GraphicsPath();
        p.AddBezier(512, 46.877f, 512, 34.112f, 517.719f, 22.019f, 527.586f, 13.92f);
        p.AddBezier(527.586f, 13.92f, 537.453f, 5.822f, 550.43f, 2.571f, 562.95f, 5.06f);
        p.AddBezier(562.95f, 5.06f, 679.864f, 28.727f, 768, 132.152f, 768, 256);
        p.AddBezier(768, 256, 768, 397.29f, 653.29f, 512, 512, 512);
        p.CloseFigure(); // straight edge back down to the start, like the SVG's final "L"
        return p;
    }

    /// <summary>The logo as a square bitmap; <paramref name="mono"/> paints every petal one colour (e.g. white for dark taskbars).</summary>
    public static SD.Bitmap Render(int size, SD.Color? mono = null)
    {
        var bmp = new SD.Bitmap(size, size);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = SD2.PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 1024f, size / 1024f);
        foreach (var (color, m) in Petals)
        {
            using var path = Petal();
            using var transform = new SD2.Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
            path.Transform(transform);
            using var brush = new SD.SolidBrush(mono ?? color);
            g.FillPath(brush, path);
        }
        return bmp;
    }
}
