using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Welcome;

/// <summary>One artist to choose on the welcome screen.</summary>
public sealed partial class WelcomeArtistVm : ObservableObject
{
    public WelcomeArtistVm(string id, string name, string thumbnailUrl)
    {
        Id = id;
        Name = name;
        ThumbnailUrl = thumbnailUrl;
    }

    public string Id { get; }
    public string Name { get; }
    public string ThumbnailUrl { get; }

    [ObservableProperty] private bool _isChosen;
}

/// <summary>
/// What the welcome screen shows: covers for the wall behind it and artists to choose, both
/// read from YouTube Music's charts and new releases. Nothing here is needed to get past it:
/// offline, the wall stays empty and the choice can be skipped.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    /// <summary>How many artists are offered before anyone searches.</summary>
    private const int Offered = 30;

    private readonly IMusicProvider _music;
    private readonly IAppSettings? _settings;

    /// <summary>One entry per artist, so a choice made in a search is still made after it.</summary>
    private readonly Dictionary<string, WelcomeArtistVm> _known = new(StringComparer.Ordinal);
    private readonly List<WelcomeArtistVm> _offered = new();
    private readonly List<WelcomeArtistVm> _chosen = new();
    private CancellationTokenSource? _searchCts;

    public WelcomeViewModel(IMusicProvider music, IAppSettings? settings)
    {
        _music = music;
        _settings = settings;

        // Coming back to it: the artists chosen last time are there, chosen.
        foreach (var favourite in settings?.FavouriteArtists ?? Array.Empty<FavouriteArtist>())
        {
            if (string.IsNullOrWhiteSpace(favourite.Id) || _known.ContainsKey(favourite.Id)) continue;
            var artist = Know(favourite.Id, favourite.Name, favourite.ThumbnailUrl);
            artist.IsChosen = true;
            _chosen.Add(artist);
        }
        ChosenCount = _chosen.Count;
    }

    /// <summary>The artists on screen: the offer, or what a search found.</summary>
    public ObservableCollection<WelcomeArtistVm> Shown { get; } = new();

    /// <summary>Covers for the wall, as they become known.</summary>
    public event Action<IReadOnlyList<string>>? CoversFound;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private int _chosenCount;

    public bool HasChosen => ChosenCount > 0;

    /// <summary>Nothing could be read, and no search is showing its own answer.</summary>
    public bool ShowProblem => !string.IsNullOrEmpty(Problem) && !IsSearching;

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(ShowProblem));
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowProblem));

    /// <summary>Under the artists: how many are chosen, or what to do.</summary>
    public string ChosenText => ChosenCount switch
    {
        0 => "Choose as many as you like",
        1 => "1 artist chosen",
        _ => $"{ChosenCount} artists chosen"
    };

    public string DoneText => HasChosen ? "Start listening" : "Skip for now";

    partial void OnChosenCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChosen));
        OnPropertyChanged(nameof(ChosenText));
        OnPropertyChanged(nameof(DoneText));
    }

    /// <summary>Reads the charts and the new releases. Safe to call once; failures leave a message.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var country = ListenerCountry.Current;
        var exploreTask = Softly(() => _music.GetExplorePageAsync(ct));
        var localTask = Softly(() => _music.GetChartsAsync(country.Code, ct));
        // The world's chart is mostly India's: names from the biggest market abroad mix better.
        var abroad = country.Code == "US" ? "GB" : "US";
        var worldTask = Softly(() => _music.GetChartsAsync(abroad, ct));

        var explore = await exploreTask;
        var local = await localTask;
        var world = await worldTask;
        if (ct.IsCancellationRequested) return;

        // The wall: new albums and what is trending. The charts' own playlists are mostly
        // words on a picture, so they stay off it.
        var covers = new List<string>();
        void AddCovers(Shelf? shelf)
        {
            foreach (var item in shelf?.Items ?? Array.Empty<ShelfItem>())
                if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl) && !covers.Contains(item.ThumbnailUrl))
                    covers.Add(item.ThumbnailUrl);
        }
        AddCovers(explore?.NewReleases);
        AddCovers(explore?.Trending);

        // The offer: the country's chart, with the one abroad taking every third place.
        var near = ArtistsOf(local);
        var far = ArtistsOf(world);
        var offer = new List<ShelfItem>();
        int n = 0, f = 0;
        while (offer.Count < Offered && (n < near.Count || f < far.Count))
        {
            var fromFar = (offer.Count % 3 == 2 && f < far.Count) || n >= near.Count;
            var next = fromFar ? far[f++] : near[n++];
            if (offer.All(a => a.Id != next.Id)) offer.Add(next);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (covers.Count > 0) CoversFound?.Invoke(covers);
            _offered.Clear();
            foreach (var item in offer)
                _offered.Add(Know(item.Id, item.Title, item.ThumbnailUrl));
            IsLoading = false;
            Problem = _offered.Count == 0 ? "YouTube Music can't be reached right now. You can choose artists later in Settings, About." : null;
            if (!IsSearching) ShowOffer();
        });
    }

    private static bool IsArtists(Shelf shelf) => shelf.Title.Contains("artist", StringComparison.OrdinalIgnoreCase);

    private static List<ShelfItem> ArtistsOf(IReadOnlyList<Shelf>? shelves) =>
        (shelves ?? Array.Empty<Shelf>())
            .Where(IsArtists)
            .SelectMany(s => s.Items)
            .Where(i => i.Kind == ShelfItemKind.Artist && !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.Title))
            .ToList();

    private static async Task<T?> Softly<T>(Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Welcome] read failed: {ex.Message}");
            return null;
        }
    }

    private WelcomeArtistVm Know(string id, string name, string thumbnail)
    {
        if (_known.TryGetValue(id, out var known)) return known;
        var artist = new WelcomeArtistVm(id, name, thumbnail);
        _known[id] = artist;
        return artist;
    }

    /// <summary>The offer, with those chosen from a search in front of it.</summary>
    private void ShowOffer()
    {
        Shown.Clear();
        foreach (var artist in _chosen.Where(c => !_offered.Contains(c))) Shown.Add(artist);
        foreach (var artist in _offered) Shown.Add(artist);
    }

    public void Toggle(WelcomeArtistVm artist)
    {
        artist.IsChosen = !artist.IsChosen;
        if (artist.IsChosen)
        {
            if (!_chosen.Contains(artist)) _chosen.Add(artist);
        }
        else _chosen.Remove(artist);
        ChosenCount = _chosen.Count;
    }

    /// <summary>Somebody not on the charts: a search among artists.</summary>
    public async Task SearchAsync(string? text)
    {
        _searchCts?.Cancel();
        var query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            IsSearching = false;
            if (!IsLoading) ShowOffer();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        try
        {
            var found = await _music.SearchAsync(query, SearchFilter.Artists, cts.Token);
            if (cts.IsCancellationRequested) return;
            Shown.Clear();
            foreach (var item in found.Artists.Where(a => !string.IsNullOrWhiteSpace(a.Id)).Take(Offered))
                Shown.Add(Know(item.Id, item.Name, item.ThumbnailUrl));
        }
        catch (OperationCanceledException)
        {
            // a newer search owns the list
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Welcome] search failed: {ex.Message}");
        }
    }

    /// <summary>Keeps the choice and marks the welcome as seen.</summary>
    public void Commit()
    {
        if (_settings == null) return;
        _settings.FavouriteArtists = _chosen
            .Select(a => new FavouriteArtist { Id = a.Id, Name = a.Name, ThumbnailUrl = a.ThumbnailUrl })
            .ToList();
        _settings.WelcomeSeen = true;
        _settings.Save();
    }
}
