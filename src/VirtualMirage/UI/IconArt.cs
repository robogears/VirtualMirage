using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VirtualMirage.UI;

/// <summary>
/// The VirtualMirage logo, drawn procedurally (no shipped binary assets) — a VR headset/visor on a
/// blue→violet rounded-square, used for the app icon (.ico), the installer, and the tray.
/// </summary>
public static class IconArt
{
    // Brand gradient (modern blue -> violet).
    private static readonly Color BrandA = Color.FromArgb(0x3B, 0x82, 0xF6); // blue
    private static readonly Color BrandB = Color.FromArgb(0x8B, 0x5C, 0xF6); // violet

    /// <summary>Render the base logo at <paramref name="size"/> px (32-bpp, transparent corners).</summary>
    public static Bitmap RenderLogo(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        // 1) Rounded-square background with the brand gradient + a subtle top sheen.
        var bg = new RectangleF(s * 0.055f, s * 0.055f, s * 0.89f, s * 0.89f);
        float bgR = s * 0.225f;
        using (var bgPath = Rounded(bg, bgR))
        using (var bgBrush = new LinearGradientBrush(bg, BrandA, BrandB, LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(bgBrush, bgPath);
            using var sheen = new LinearGradientBrush(
                new RectangleF(bg.X, bg.Y, bg.Width, bg.Height * 0.5f),
                Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
            var clip = g.Clip; g.SetClip(bgPath); g.FillRectangle(sheen, bg.X, bg.Y, bg.Width, bg.Height * 0.5f); g.Clip = clip;
        }

        // 2) VR visor: a wide rounded rectangle (white faceplate) with a nose notch + two lens windows.
        float vw = s * 0.66f, vh = s * 0.40f;
        float vx = (s - vw) / 2f, vy = s * 0.34f;
        var visor = new RectangleF(vx, vy, vw, vh);
        using (var visorPath = Rounded(visor, vh * 0.42f))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, visorPath);

        // Lens windows (cut out with the brand gradient so they read as goggles).
        using (var lensBrush = new LinearGradientBrush(visor, BrandA, BrandB, LinearGradientMode.ForwardDiagonal))
        {
            float lw = vw * 0.30f, lh = vh * 0.52f, ly = vy + vh * 0.20f, gap = vw * 0.08f;
            float l1x = vx + vw * 0.5f - gap / 2f - lw;
            float l2x = vx + vw * 0.5f + gap / 2f;
            using var lp1 = Rounded(new RectangleF(l1x, ly, lw, lh), lh * 0.45f);
            using var lp2 = Rounded(new RectangleF(l2x, ly, lw, lh), lh * 0.45f);
            g.FillPath(lensBrush, lp1);
            g.FillPath(lensBrush, lp2);
        }

        // Nose notch (small gradient triangle cut at the bottom-centre of the visor).
        using (var bgBrush = new LinearGradientBrush(bg, BrandA, BrandB, LinearGradientMode.ForwardDiagonal))
        {
            float nw = vw * 0.22f, nh = vh * 0.34f;
            float nx = vx + (vw - nw) / 2f, ny = vy + vh - nh + 0.5f;
            using var notch = new GraphicsPath();
            notch.AddPolygon(new[] { new PointF(nx, vy + vh + 1), new PointF(nx + nw / 2f, ny), new PointF(nx + nw, vy + vh + 1) });
            g.FillPath(bgBrush, notch);
        }

        return bmp;
    }

    /// <summary>The logo with an optional status dot in the bottom-right corner (for the tray).</summary>
    public static Icon RenderStatusIcon(int size, Color? statusDot)
    {
        var bmp = RenderLogo(size);
        if (statusDot is { } c)
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = size * 0.42f, x = size - d - size * 0.04f, y = size - d - size * 0.04f;
            using var ring = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillEllipse(ring, x - size * 0.045f, y - size * 0.045f, d + size * 0.09f, d + size * 0.09f);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, x, y, d, d);
        }
        return ToIcon(bmp);
    }

    /// <summary>Assemble a multi-resolution .ico (PNG-encoded entries; fine on Windows 10/11).</summary>
    public static byte[] BuildIco(int[] sizes)
    {
        var pngs = new List<byte[]>();
        foreach (var sz in sizes)
        {
            using var bmp = RenderLogo(sz);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var outMs = new MemoryStream();
        using var bw = new BinaryWriter(outMs);
        bw.Write((short)0);              // reserved
        bw.Write((short)1);              // type = icon
        bw.Write((short)sizes.Length);   // count
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // width (0 == 256)
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // height
            bw.Write((byte)0);  // palette
            bw.Write((byte)0);  // reserved
            bw.Write((short)1); // planes
            bw.Write((short)32); // bpp
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
        bw.Flush();
        return outMs.ToArray();
    }

    private static Icon? _appIcon;

    /// <summary>App icon as an <see cref="Icon"/> for forms (256→16 multi-size); cached.</summary>
    public static Icon AppIcon()
    {
        if (_appIcon is not null) return _appIcon;
        using var ms = new MemoryStream(BuildIco(new[] { 16, 32, 48, 64, 128, 256 }));
        return _appIcon = new Icon(ms);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); bmp.Dispose(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
