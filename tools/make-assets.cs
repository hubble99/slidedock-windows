// Generates SlideDock's brand assets: the app icon (.ico), Microsoft Store logos and the README banner.
// Build & run:  powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class MakeAssets
{
    static readonly Color Violet = Color.FromRgb(0x7B, 0x3F, 0xE4);
    static readonly Color Blue = Color.FromRgb(0x2F, 0x6B, 0xFF);
    static readonly Color Teal = Color.FromRgb(0x19, 0xC3, 0xB0);

    static Brush Aurora()
    {
        LinearGradientBrush g = new LinearGradientBrush();
        g.StartPoint = new Point(0, 0);
        g.EndPoint = new Point(1, 1);
        g.GradientStops.Add(new GradientStop(Violet, 0));
        g.GradientStops.Add(new GradientStop(Blue, 0.55));
        g.GradientStops.Add(new GradientStop(Teal, 1));
        return g;
    }

    // the logo: rounded square, a small pill (the notch) above a wide pill (the dock) with three app dots
    static void DrawLogo(DrawingContext dc, double x, double y, double s)
    {
        Rect box = new Rect(x, y, s, s);
        dc.DrawRoundedRectangle(Aurora(), null, box, s * 0.23, s * 0.23);
        LinearGradientBrush gloss = new LinearGradientBrush(Color.FromArgb(0x40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        dc.DrawRoundedRectangle(gloss, null, new Rect(x, y, s, s * 0.55), s * 0.23, s * 0.23);

        double nh = s * 0.085, nw = s * 0.30;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), null,
            new Rect(x + (s - nw) / 2, y + s * 0.30, nw, nh), nh / 2, nh / 2);

        double dh = s * 0.22, dw = s * 0.70;
        Rect dock = new Rect(x + (s - dw) / 2, y + s * 0.56, dw, dh);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xF5, 255, 255, 255)), null, dock, dh / 2, dh / 2);
        double d = dh * 0.52, gap = d * 0.55;
        double total = d * 3 + gap * 2, sx = dock.X + (dock.Width - total) / 2, sy = dock.Y + (dh - d) / 2;
        Color[] dots = { Violet, Blue, Teal };
        for (int i = 0; i < 3; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(dots[i]), null, new Rect(sx + i * (d + gap), sy, d, d), d * 0.3, d * 0.3);
    }

    static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        DrawingVisual v = new DrawingVisual();
        using (DrawingContext dc = v.RenderOpen()) draw(dc);
        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    static byte[] Png(BitmapSource b)
    {
        PngBitmapEncoder e = new PngBitmapEncoder();
        e.Frames.Add(BitmapFrame.Create(b));
        using (MemoryStream ms = new MemoryStream()) { e.Save(ms); return ms.ToArray(); }
    }

    static void SavePng(BitmapSource b, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, Png(b));
        Console.WriteLine(path);
    }

    static BitmapSource Logo(int size, double pad)
    {
        return Render(size, size, delegate(DrawingContext dc) { DrawLogo(dc, size * pad, size * pad, size * (1 - 2 * pad)); });
    }

    // .ico with PNG-compressed images (supported since Windows Vista)
    static void SaveIco(string path, int[] sizes)
    {
        List<byte[]> imgs = new List<byte[]>();
        foreach (int s in sizes) imgs.Add(Png(Logo(s, s <= 32 ? 0.0 : 0.03)));
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(imgs[i].Length); w.Write(offset);
                offset += imgs[i].Length;
            }
            foreach (byte[] b in imgs) w.Write(b);
        }
        Console.WriteLine(path);
    }

    static FormattedText Text(string s, double size, FontWeight weight, Brush brush)
    {
        Typeface tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush, 1.0);
    }

    [STAThread]
    static void Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);

        SaveIco(Path.Combine(assets, "SlideDock.ico"), new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
        SavePng(Logo(512, 0.0), Path.Combine(assets, "logo-512.png"));

        // Microsoft Store / MSIX logos (transparent padding like other Store apps)
        string store = Path.Combine(assets, "store");
        SavePng(Logo(44, 0.04), Path.Combine(store, "Square44x44Logo.png"));
        SavePng(Logo(88, 0.04), Path.Combine(store, "Square44x44Logo.scale-200.png"));
        SavePng(Logo(256, 0.0), Path.Combine(store, "Square44x44Logo.targetsize-256_altform-unplated.png"));
        SavePng(Logo(48, 0.0), Path.Combine(store, "Square44x44Logo.targetsize-48_altform-unplated.png"));
        SavePng(Logo(24, 0.0), Path.Combine(store, "Square44x44Logo.targetsize-24_altform-unplated.png"));
        SavePng(Logo(16, 0.0), Path.Combine(store, "Square44x44Logo.targetsize-16_altform-unplated.png"));
        SavePng(Logo(150, 0.2), Path.Combine(store, "Square150x150Logo.png"));
        SavePng(Logo(300, 0.2), Path.Combine(store, "Square150x150Logo.scale-200.png"));
        SavePng(Logo(50, 0.0), Path.Combine(store, "StoreLogo.png"));
        SavePng(Logo(100, 0.0), Path.Combine(store, "StoreLogo.scale-200.png"));
        foreach (int k in new int[] { 1, 2 })
        {
            int w = 310 * k, h = 150 * k;
            SavePng(Render(w, h, delegate(DrawingContext dc) { DrawLogo(dc, (w - h * 0.6) / 2, h * 0.2, h * 0.6); }),
                Path.Combine(store, k == 1 ? "Wide310x150Logo.png" : "Wide310x150Logo.scale-200.png"));
        }

        // README / store listing banner
        SavePng(Render(1280, 400, delegate(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C)), null, new Rect(0, 0, 1280, 400));
            RadialGradientBrush glow = new RadialGradientBrush(Color.FromArgb(0x70, 0x5B, 0x3F, 0xE4), Color.FromArgb(0, 0x14, 0x14, 0x1C));
            glow.Center = new Point(0.25, 0.5); glow.GradientOrigin = glow.Center; glow.RadiusX = 0.5; glow.RadiusY = 1.2;
            dc.DrawRectangle(glow, null, new Rect(0, 0, 1280, 400));
            DrawLogo(dc, 150, 90, 220);
            dc.DrawText(Text("SlideDock", 96, FontWeights.SemiBold, Brushes.White), new Point(430, 108));
            dc.DrawText(Text("A modern dock that replaces the Windows taskbar", 30, FontWeights.Normal, new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255))), new Point(436, 236));
        }), Path.Combine(assets, "banner.png"));
    }
}
