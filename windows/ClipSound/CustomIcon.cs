using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipSound;

/// <summary>Eigenes App-Icon: liegt als PNG in %AppData%\ClipSound und ersetzt Fenster-, Taskleisten- und Infobereich-Symbol.</summary>
public static class CustomIcon
{
    private static string FilePath => Path.Combine(SoundLibrary.SupportFolder, "AppIcon.png");

    public static BitmapSource? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // Datei nicht sperren (sonst klappt Zurücksetzen nicht)
            bmp.UriSource = new Uri(FilePath);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Mittig quadratisch zuschneiden, auf 256 px bringen und speichern. false = kein lesbares Bild.</summary>
    public static bool Set(string source)
    {
        try
        {
            var src = BitmapFrame.Create(new Uri(Path.GetFullPath(source)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            int side = Math.Min(src.PixelWidth, src.PixelHeight);
            var square = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
            const int size = 256;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                dc.DrawImage(square, new Rect(0, 0, size, size));
            }
            var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = File.Create(FilePath);
            encoder.Save(file);
            return true;
        }
        catch { return false; }
    }

    public static void Reset()
    {
        try { File.Delete(FilePath); } catch { /* egal */ }
    }

    /// <summary>Für das Symbol im Infobereich (WinForms braucht ein System.Drawing.Icon)</summary>
    public static System.Drawing.Icon? TrayIcon()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var bmp = new System.Drawing.Bitmap(FilePath);
            using var small = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(32, 32));
            return System.Drawing.Icon.FromHandle(small.GetHicon());
        }
        catch { return null; }
    }
}
