using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SoundLibert.UI.Controls;

/// <summary>
/// A playlist cover: a 2×2 mosaic of its first four distinct track covers
/// (Spotify-style) when there are at least four, otherwise the single cover.
/// Used by the Library tiles and the playlist page header so both agree.
/// </summary>
public sealed class CoverMosaic : ContentControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> UrlsProperty =
        AvaloniaProperty.Register<CoverMosaic, IReadOnlyList<string>?>(nameof(Urls));

    /// <summary>Fallback single cover when there is no mosaic.</summary>
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<CoverMosaic, string?>(nameof(Url));

    public IReadOnlyList<string>? Urls
    {
        get => GetValue(UrlsProperty);
        set => SetValue(UrlsProperty, value);
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    static CoverMosaic()
    {
        UrlsProperty.Changed.AddClassHandler<CoverMosaic>((c, _) => c.Build());
        UrlProperty.Changed.AddClassHandler<CoverMosaic>((c, _) => c.Build());
    }

    public CoverMosaic()
    {
        ClipToBounds = true;
        Build();
    }

    private void Build()
    {
        var urls = (Urls ?? new List<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .ToList();

        if (urls.Count >= 4)
        {
            var grid = new UniformGrid { Rows = 2, Columns = 2 };
            foreach (var url in urls.Take(4))
                grid.Children.Add(new RemoteImage { Url = url, Stretch = Stretch.UniformToFill });
            Content = grid;
            return;
        }

        var single = urls.FirstOrDefault() ?? Url;
        Content = string.IsNullOrWhiteSpace(single)
            ? null
            : new RemoteImage { Url = single, Stretch = Stretch.UniformToFill };
    }
}
