using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Quizzler.App.Services;

/// <summary>Draws the tray icon: the app logo, with a red count badge when questions are due.</summary>
internal static class TrayIconRenderer
{
    private const int Size = 64;

    private static readonly BitmapSource Logo = LoadLogo();
    private static readonly Brush BadgeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
    private static readonly Pen BadgeOutline = Frozen(new Pen(Brushes.White, 4));
    private static readonly Brush SnoozeVeil = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface BadgeFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>
    /// The tray needs a Win32 icon, and H.NotifyIcon can't convert a rendered bitmap itself,
    /// so encode it as PNG and wrap it in a one-frame .ico in memory.
    /// </summary>
    public static System.Drawing.Icon RenderIcon(int dueCount, bool snoozed)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)Render(dueCount, snoozed)));
        using var png = new MemoryStream();
        encoder.Save(png);

        var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);   // ICONDIR: reserved, type=icon, count=1
            w.Write((byte)Size); w.Write((byte)Size); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);                       // planes, bits per pixel
            w.Write((uint)png.Length); w.Write((uint)22);                  // data size, offset (6 + 16)
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico, Size, Size);
    }

    public static ImageSource Render(int dueCount, bool snoozed)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(Logo, new Rect(0, 0, Size, Size));
            if (snoozed) dc.DrawRectangle(SnoozeVeil, null, new Rect(0, 0, Size, Size));

            if (dueCount > 0)
            {
                var text = dueCount > 99 ? "99+" : dueCount.ToString(CultureInfo.InvariantCulture);
                var height = 40.0;
                var width = text.Length switch { 1 => 40.0, 2 => 50.0, _ => 62.0 };
                var badge = new Rect(Size - width, 0, width, height);
                dc.DrawRoundedRectangle(BadgeBrush, BadgeOutline, badge, height / 2, height / 2);

                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    BadgeFont, text.Length > 2 ? 24 : 30, Brushes.White, pixelsPerDip: 1.0);
                dc.DrawText(label, new Point(badge.X + (badge.Width - label.Width) / 2, badge.Y + (badge.Height - label.Height) / 2));
            }
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadLogo()
    {
        var decoder = new IconBitmapDecoder(
            new Uri("pack://application:,,,/Quizzler.App;component/Assets/quizzler.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - Size)).First();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
