using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinSpotlight;

/// <summary>A fully transparent window backdrop, so the XAML content can draw its own
/// rounded glass shape and shadow instead of filling the whole window rectangle.</summary>
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        Native.EnsureSystemDispatcherQueue();
        _compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }
}

/// <summary>Frosted glass: blurs and saturates a capture of the desktop behind the window.</summary>
public static class Frost
{
    /// <summary>Returns BGRA pixels (opaque) and their size. Large blurs work on a downscaled
    /// copy, which is both faster and smoother once XAML scales it back up.</summary>
    public static (byte[] Px, int W, int H) Process(byte[] src, int w, int h, double blurPx, double saturation)
    {
        int f = blurPx < 4 ? 1 : Math.Clamp((int)(blurPx / 4), 1, 8);
        int sw = Math.Max(1, w / f), sh = Math.Max(1, h / f);
        var px = Downscale(src, w, h, f, sw, sh);
        int r = blurPx < 4 ? 0 : Math.Max(1, (int)Math.Round(blurPx / f / 2));
        if (r > 0)
        {
            var tmp = new byte[px.Length];
            for (int pass = 0; pass < 3; pass++) // three box blurs ≈ a gaussian
            {
                BoxBlur(px, tmp, sw, sh, r, true);
                BoxBlur(tmp, px, sw, sh, r, false);
            }
        }
        for (int i = 0; i < px.Length; i += 4)
        {
            if (saturation != 1)
            {
                double b = px[i], g = px[i + 1], rr = px[i + 2];
                double l = 0.114 * b + 0.587 * g + 0.299 * rr;
                px[i] = Clamp(l + (b - l) * saturation);
                px[i + 1] = Clamp(l + (g - l) * saturation);
                px[i + 2] = Clamp(l + (rr - l) * saturation);
            }
            px[i + 3] = 255;
        }
        return (px, sw, sh);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v + 0.5, 0, 255);

    private static byte[] Downscale(byte[] src, int w, int h, int f, int sw, int sh)
    {
        if (f == 1) return (byte[])src.Clone();
        var dst = new byte[sw * sh * 4];
        int n = f * f;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int yy = 0; yy < f; yy++)
                {
                    int row = ((y * f + yy) * w + x * f) * 4;
                    for (int xx = 0; xx < f; xx++, row += 4) { b += src[row]; g += src[row + 1]; r += src[row + 2]; }
                }
                int o = (y * sw + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n);
            }
        return dst;
    }

    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h, win = 2 * r + 1;
        for (int line = 0; line < lines; line++)
        {
            int Idx(int i) => (horizontal ? line * w + Math.Clamp(i, 0, len - 1) : Math.Clamp(i, 0, len - 1) * w + line) * 4;
            int b = 0, g = 0, rr = 0;
            for (int i = -r; i <= r; i++) { int k = Idx(i); b += src[k]; g += src[k + 1]; rr += src[k + 2]; }
            for (int i = 0; i < len; i++)
            {
                int o = Idx(i);
                dst[o] = (byte)(b / win); dst[o + 1] = (byte)(g / win); dst[o + 2] = (byte)(rr / win);
                int add = Idx(i + r + 1), sub = Idx(i - r);
                b += src[add] - src[sub]; g += src[add + 1] - src[sub + 1]; rr += src[add + 2] - src[sub + 2];
            }
        }
    }
}
