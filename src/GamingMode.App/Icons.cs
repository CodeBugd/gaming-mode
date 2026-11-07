using System.Drawing.Imaging;

namespace GamingMode.App;

internal static class Icons
{
    private static Icon? _on, _off;

    public static Icon On => _on ??= Load();
    public static Icon Off => _off ??= Greyscale(On);

    private static Icon Load()
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream("GamingMode.ico")!;
        return new Icon(s, SystemInformation.SmallIconSize);
    }

    /// <summary>Same controller badge, greyed out - shown while Gaming Mode is off.</summary>
    private static Icon Greyscale(Icon source)
    {
        using var src = source.ToBitmap();
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            var m = new ColorMatrix(new[]
            {
                new[] { .30f, .30f, .30f, 0f, 0f },
                new[] { .59f, .59f, .59f, 0f, 0f },
                new[] { .11f, .11f, .11f, 0f, 0f },
                new[] { 0f, 0f, 0f, .85f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(m);
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return Icon.FromHandle(dst.GetHicon());
    }
}
