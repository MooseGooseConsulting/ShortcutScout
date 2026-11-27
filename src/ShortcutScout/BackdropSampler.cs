using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShortcutScout.Core;

namespace ShortcutScout;

/// <summary>
/// Reads the pixels on screen around the overlay so the panel can adapt its
/// contrast to whatever is behind it.
///
/// Only the ring OUTSIDE the window's own rectangle is sampled: capturing the
/// interior would just measure the overlay against itself.
/// </summary>
public static class BackdropSampler
{
    private const int Pad = 48;
    private const int Step = 4;

    public static BackdropStats Sample(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var wr)) return BackdropStats.Empty;

        var w = wr.Right - wr.Left;
        var h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return BackdropStats.Empty;

        var x = wr.Left - Pad;
        var y = wr.Top - Pad;
        var cw = w + Pad * 2;
        var ch = h + Pad * 2;

        return SampleRegion(x, y, cw, ch,
            excludeInner: new Rectangle(wr.Left, wr.Top, w, h));
    }

    /// <summary>Sample a screen rectangle with no exclusion, for the --sample diagnostic.</summary>
    public static BackdropStats SampleRect(int x, int y, int w, int h) =>
        SampleRegion(x, y, w, h, excludeInner: null);

    private static BackdropStats SampleRegion(int x, int y, int cw, int ch, System.Drawing.Rectangle? excludeInner)
    {
        if (cw <= 0 || ch <= 0) return BackdropStats.Empty;

        try
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);

            var data = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var stride = data.Stride;
            var bytes = new byte[stride * ch];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);

            // Ring bounds in bitmap space (the window itself is excluded).
            var innerLeft = excludeInner is { } e ? e.Left - x : cw + 1;
            var innerTop = excludeInner is { } e2 ? e2.Top - y : ch + 1;
            var innerRight = excludeInner is { } e3 ? e3.Right - x : cw + 1;
            var innerBottom = excludeInner is { } e4 ? e4.Bottom - y : ch + 1;

            double sumR = 0, sumG = 0, sumB = 0, sumL = 0, sumL2 = 0;
            long n = 0;

            for (var py = 0; py < ch; py += Step)
            {
                for (var px = 0; px < cw; px += Step)
                {
                    var insideWindow = px >= innerLeft && px < innerRight && py >= innerTop && py < innerBottom;
                    if (insideWindow) continue;

                    var i = py * stride + px * 4;
                    var b = bytes[i];
                    var g2 = bytes[i + 1];
                    var r = bytes[i + 2];

                    var l = 0.2126 * r + 0.7152 * g2 + 0.0722 * b;
                    sumR += r; sumG += g2; sumB += b;
                    sumL += l; sumL2 += l * l;
                    n++;
                }
            }

            if (n == 0) return BackdropStats.Empty;

            var meanR = sumR / n;
            var meanG = sumG / n;
            var meanB = sumB / n;
            var meanL = sumL / n;
            var variance = Math.Max(0, sumL2 / n - meanL * meanL);

            return new BackdropStats(meanR, meanG, meanB, Math.Sqrt(variance));
        }
        catch
        {
            // Never let sampling break the overlay; keep the previous style.
            return BackdropStats.Empty;
        }
    }
}
