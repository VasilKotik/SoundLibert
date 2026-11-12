using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The SoundLibert mark, the sound wave that becomes a bird, as vectors: traced from the original
/// drawing (98 % of its pixels) and smoothed, so it stays sharp at any size. The master files are
/// in <c>docs/brand</c>.
/// </summary>
public static class BrandArt
{
    /// <summary>The drawing alone, 508 by 357 units, even-odd fill.</summary>
    public const string MarkData =
        "M261.05 0.22 C265.44 4.13 267.94 9.86 271.18 14.7 C278.56 25.73 288.77 35.66 299.56 43.31 C325.61 " +
        "61.78 358.18 68.5 383.46 87.82 C397.75 98.74 396.35 104.72 405.51 115.64 C419.52 115.61 427.98 99.15 " +
        "439.51 92.86 C458.19 82.68 470.49 89.01 486.74 98.97 C496.29 104.82 504.1 107.8 507.68 119.57 C496.6 " +
        "124.08 484.97 124.3 476.13 133.69 C458.3 152.65 459.41 190.09 430.92 216.37 C402.86 242.24 364.42 " +
        "241.95 332.42 259.93 C316.61 268.82 303.64 281.8 295.1 297.88 C293.2 301.44 292.16 305.65 289.37 " +
        "308.6 C284.24 291.05 297.44 271 309.13 259.2 C340.35 227.69 391.86 234.98 422.77 203.44 C440.86 " +
        "184.98 444.32 158.84 455.89 136.78 C462.26 124.62 468.8 121.06 478.06 112.27 C452.99 87.63 440.09 " +
        "115.34 422.2 126.53 C418.07 129.12 413.53 129.95 408.97 131.44 C408.03 139.41 409.01 147.29 407.18 " +
        "155.24 C403.14 172.81 385.09 200.9 367.38 207.11 C370.2 194.32 391.68 176.64 393.67 144.58 C396.74 " +
        "94.82 344.18 86.11 309.95 67.67 C295.09 59.65 283.84 48.24 270.34 38.52 C269.01 62.05 297.6 85.42 " +
        "315.35 96.74 C325.33 103.1 340.03 111.31 342.76 123.92 C336.93 122.64 289.64 104.59 280.58 101.1 " +
        "C275.85 99.28 271.35 96.25 266.35 95.33 C275.16 115.88 298.4 125.19 318.33 131.02 C330.17 134.48 " +
        "347.71 138.69 353.55 151.05 C336.08 150.21 326.92 146.8 307.63 152.36 C324.46 162.67 348.55 159.26 " +
        "357.97 180.5 C375.49 220 315.14 246.31 281.61 235.3 C253.57 226.09 262.78 194.96 250.21 185.39 " +
        "C242.76 195.35 242.96 211.79 241.75 223.71 C238.84 252.31 237.31 281.04 233.97 309.6 C232.57 321.54 " +
        "230 346.11 219.94 353.69 C197.33 370.73 192.01 320.06 190.6 308.48 C185.65 268.07 183.51 227.26 " +
        "181.15 186.63 C180.22 170.58 179.7 137.53 172.39 123.99 C165.44 135.62 164.89 158.81 163.91 172.38 " +
        "C161.71 203.01 160.59 234.7 154.61 264.87 C152.33 276.33 148.44 296.44 132.89 295.34 C128.36 295.02 " +
        "124.25 292.72 121.37 289.23 C109.77 275.21 108.17 231.58 105.39 212.77 C104.14 204.27 103.65 191.77 " +
        "97.65 185.17 C87.34 195.5 90.37 215.06 78.74 226.73 C63.68 241.84 39.59 237.83 20.18 237.77 C14.1 " +
        "237.75 -2.68 239.13 1.02 227.94 C3.35 220.93 12.13 222.35 17.89 222.36 C29.94 222.38 53.69 225.3 " +
        "63.81 219.05 C77.42 210.65 79.59 154.68 105.31 170.19 C114.59 175.78 117.13 189.43 118.96 199.21 " +
        "C122.11 216.03 124.61 266.21 133.58 277.43 C140.41 268.85 141.86 248.68 142.93 237.8 C145.85 208.04 " +
        "145.98 177.7 150.6 148.15 C152.49 136.08 154.63 111.66 166.15 104.86 C187.57 92.2 192.71 138.76 " +
        "193.85 150.65 C198.05 194.68 199.89 238.95 203.5 283.03 C204.41 294.21 206.04 327.54 212.6 335.37 " +
        "C223.19 306.61 223.1 249.67 226.61 217.2 C228.17 202.78 229.14 179.19 242.92 170.46 C269.48 153.62 " +
        "270.05 212.05 284.2 219.51 C302.08 228.93 347.09 216.87 344.94 192.05 C343.78 178.69 328.29 174.71 " +
        "317.43 171.89 C299.15 167.16 272.32 156.83 268.63 135.12 C275.13 135.2 281.21 137.84 287.75 136.6 " +
        "C283.32 132.75 277.89 130.04 273.22 126.45 C257.64 114.5 246.92 97.05 243.66 77.64 C242.63 71.55 " +
        "241.26 63.73 243.52 57.86 C251.81 64.56 259.8 76.5 270.78 78.82 C267.94 72.5 263.36 67.11 260.47 " +
        "60.77 C252.37 42.97 249.87 17.36 261.05 0.22 Z ";

    public const double MarkWidth = 508;
    public const double MarkHeight = 357;

    /// <summary>The icon's colours: a warm orange that leans lighter at the top, and a warm black.</summary>
    public static readonly Color Top = Color.FromRgb(0xFF, 0x8C, 0x3A);
    public static readonly Color Bottom = Color.FromRgb(0xFF, 0x5C, 0x10);
    public static readonly Color Ink = Color.FromRgb(0x14, 0x0A, 0x05);

    private static Geometry? _mark;

    /// <summary>For a <c>Path</c> (empty covers, the mini player).</summary>
    public static Geometry Mark => _mark ??= StreamGeometry.Parse("F0 " + MarkData);
}

/// <summary>
/// The app icon drawn live: the orange rounded square with the bird. Small sizes get the same
/// slightly bolder line as the small sizes of <c>app.ico</c>, so the bird does not fade.
/// </summary>
public sealed class BrandIcon : Control
{
    private static readonly IBrush InkBrush = new ImmutableSolidColorBrush(BrandArt.Ink);

    static BrandIcon() => AffectsRender<BrandIcon>(BoundsProperty);

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var square = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        var radius = side * 0.235;

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.35, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(BrandArt.Top, 0), new GradientStop(BrandArt.Bottom, 1) }
        };
        context.DrawRectangle(fill, null, square, radius, radius);

        // On screen pixels decide how bold the line has to be.
        var pixels = side * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var share = pixels <= 24 ? 0.86 : pixels <= 32 ? 0.84 : pixels <= 48 ? 0.81 : pixels <= 64 ? 0.78 : 0.76;
        var bold = pixels <= 24 ? 0.2 : pixels <= 40 ? 0.24 : pixels <= 64 ? 0.15 : 0;

        var scale = side * share / BrandArt.MarkWidth;
        var x = square.Center.X - BrandArt.MarkWidth / 2 * scale;
        var y = square.Center.Y - BrandArt.MarkHeight / 2 * scale;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
        {
            var pen = bold > 0
                ? new ImmutablePen(InkBrush.ToImmutable(), bold * side / pixels / scale, lineJoin: PenLineJoin.Round)
                : null;
            context.DrawGeometry(InkBrush, pen, BrandArt.Mark);
        }
    }
}
