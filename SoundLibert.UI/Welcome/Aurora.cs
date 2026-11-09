using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SoundLibert.UI.Welcome;

/// <summary>
/// A small picture of warm light, made on the spot, for the colour field behind the welcome
/// to flow with: the accent's tangerine, a rose, a violet and a night blue over a dark ground.
/// The field turns and enlarges it, so its edges never show.
/// </summary>
internal static class Aurora
{
    private const int Size = 160;

    private static readonly (double X, double Y, double Reach, double R, double G, double B)[] Lights =
    {
        (0.26, 0.30, 0.42, 255, 122, 51),
        (0.80, 0.24, 0.36, 236, 64, 110),
        (0.70, 0.80, 0.44, 96, 46, 150),
        (0.16, 0.84, 0.34, 34, 58, 140),
        (0.52, 0.54, 0.22, 255, 178, 110)
    };

    public static unsafe WriteableBitmap Make()
    {
        var bitmap = new WriteableBitmap(new PixelSize(Size, Size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var frame = bitmap.Lock();
        for (var y = 0; y < Size; y++)
        {
            var px = (byte*)frame.Address + y * frame.RowBytes;
            for (var x = 0; x < Size; x++, px += 4)
            {
                double r = 16, g = 12, b = 26;
                foreach (var light in Lights)
                {
                    var dx = x / (double)Size - light.X;
                    var dy = y / (double)Size - light.Y;
                    var weight = Math.Exp(-(dx * dx + dy * dy) / (light.Reach * light.Reach) * 2.0);
                    r += (light.R - r) * weight;
                    g += (light.G - g) * weight;
                    b += (light.B - b) * weight;
                }
                px[0] = (byte)Math.Clamp(b, 0, 255);
                px[1] = (byte)Math.Clamp(g, 0, 255);
                px[2] = (byte)Math.Clamp(r, 0, 255);
                px[3] = 255;
            }
        }
        return bitmap;
    }
}
