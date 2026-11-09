using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// A page of shelves: a mood, a genre, or the whole of a shelf behind its "Show all". It
/// grows as it scrolls, because every card on it is a cover to fetch.
/// </summary>
public partial class ShelfPageViewModel : ViewModelBase
{
    private const int ShelvesAtOnce = 5;
    private const int CardsAtOnce = 42;

    private readonly IMusicProvider _provider;
    private readonly string _browseId;
    private readonly string? _params;
    private readonly Queue<Shelf> _waitingShelves = new();
    private readonly Queue<ShelfItem> _waitingCards = new();
    private GridShelfVm? _grid;

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _emptyMessage = string.Empty;

    /// <summary>"MOOD", "GENRE" or nothing.</summary>
    public string? Eyebrow { get; init; }

    /// <summary>The colour the page opens in, when it has its own.</summary>
    public uint? Color { get; init; }

    public bool HasEyebrow => !string.IsNullOrWhiteSpace(Eyebrow);

    public IBrush Wash
    {
        get
        {
            var color = Avalonia.Media.Color.FromUInt32(Color ?? 0xFF3A3F52);
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Avalonia.Media.Color.FromArgb(0x8C, color.R, color.G, color.B), 0),
                    new GradientStop(Avalonia.Media.Color.FromArgb(0x00, color.R, color.G, color.B), 1)
                }
            };
        }
    }

    public ObservableCollection<ShelfVm> Shelves { get; } = new();

    public bool ShowEmpty => !IsLoading && Shelves.Count == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    public ShelfPageViewModel(IMusicProvider provider, string title, string browseId, string? @params)
    {
        _provider = provider;
        _title = title;
        _browseId = browseId;
        _params = @params;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var page = await _provider.GetShelfPageAsync(_browseId, _params);
            if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(page.Title)) Title = page.Title;

            // One shelf and nothing else is a list to fill the page with.
            if (page.Shelves.Count == 1 && page.Shelves[0].Layout == ShelfLayout.Cards)
            {
                _grid = new GridShelfVm();
                foreach (var item in page.Shelves[0].Items) _waitingCards.Enqueue(item);
                Shelves.Add(_grid);
            }
            else
            {
                foreach (var shelf in page.Shelves) _waitingShelves.Enqueue(shelf);
            }

            ShowMore();
            EmptyMessage = "Nothing here right now.";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShelfPage] \"{Title}\" failed: {ex.Message}");
            EmptyMessage = "Could not load this page. Check your connection.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    /// <summary>The page was scrolled near its end.</summary>
    public void ShowMore()
    {
        if (_grid != null)
        {
            var cards = new List<ShelfCard>();
            while (cards.Count < CardsAtOnce && _waitingCards.TryDequeue(out var item))
                cards.Add(new ShelfCard(item));
            _grid.Add(cards);
            return;
        }

        for (var i = 0; i < ShelvesAtOnce && _waitingShelves.TryDequeue(out var shelf); i++)
            Shelves.Add(ShelfVm.From(shelf));
    }
}
