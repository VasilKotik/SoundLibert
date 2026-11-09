using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using SoundLibert.UI.Controls;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// A full page of videos that keeps growing as you scroll: an artist's channel (clips,
/// live, sessions), the videos Home picked for you, what is new. Every card plays as a
/// video through <see cref="PlayVideoRequest"/>.
/// </summary>
public partial class VideoFeedViewModel : ViewModelBase
{
    // A card is a cover to download and decode: a page grows by what scrolling can see.
    private const int FirstBatch = 36;
    private const int NextBatch = 24;

    private readonly IQueueService _queue;
    private readonly IDownloadService _downloads;
    private readonly Func<IVideoFeed> _openFeed;
    private VideoFeedReader? _reader;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _hasVideos;
    [ObservableProperty] private bool _loadFailed;
    [ObservableProperty] private string _emptyMessage = "Nothing here yet.";
    [ObservableProperty] private string _countText = string.Empty;

    /// <summary>The page's cover when it has no picture of its own: its first videos.</summary>
    [ObservableProperty] private IReadOnlyList<string>? _coverUrls;

    public string Title { get; init; } = string.Empty;
    public string? ThumbnailUrl { get; init; }
    public bool IsRound { get; init; }
    public IconKind Glyph { get; init; } = IconKind.Video;

    public ObservableCollection<SongItem> Videos { get; } = new();

    /// <summary>The "nothing found" line: only once loading is over.</summary>
    public bool ShowEmpty => !IsLoading && !HasVideos;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnHasVideosChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>Set by the shell so cards play as videos.</summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideoRequest { get; set; }

    public VideoFeedViewModel(IQueueService queue, IDownloadService downloads, Func<IVideoFeed> openFeed)
    {
        _queue = queue;
        _downloads = downloads;
        _openFeed = openFeed;
    }

    public async Task LoadAsync()
    {
        var reader = new VideoFeedReader(_openFeed());
        _reader = reader;
        Videos.Clear();
        HasVideos = false;
        LoadFailed = false;
        CountText = string.Empty;
        CoverUrls = null;
        IsLoading = true;

        try
        {
            await ShowMoreAsync(reader, FirstBatch);
            EmptyMessage = "No videos found.";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoFeed] \"{Title}\" failed: {ex.Message}");
            EmptyMessage = "Could not load the videos. Check your connection.";
        }
        finally
        {
            if (reader == _reader) IsLoading = false;
        }
    }

    /// <summary>The page was scrolled near its end. Does nothing while a read is under way.</summary>
    public async Task LoadMoreAsync()
    {
        if (_reader is not { HasMore: true } reader || IsLoading || IsLoadingMore || LoadFailed) return;

        IsLoadingMore = true;
        try
        {
            await ShowMoreAsync(reader, NextBatch);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoFeed] \"{Title}\" next page failed: {ex.Message}");
            if (reader == _reader) LoadFailed = true;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task ShowMoreAsync(VideoFeedReader reader, int count)
    {
        var batch = await reader.ReadAsync(count);
        if (reader != _reader) return; // Refreshed meanwhile: the new list owns the page.

        foreach (var video in batch)
        {
            video.IsDownloaded = _downloads.IsDownloaded(video.Id);
            Videos.Add(video);
        }

        HasVideos = Videos.Count > 0;
        var known = reader.Known.Count;
        CountText = known == 0 ? string.Empty
            : reader.HasMore ? $"{known}+ videos"
            : known == 1 ? "1 video"
            : $"{known} videos";
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) && CoverUrls == null && HasVideos)
            CoverUrls = Videos.Take(4).Select(v => v.ThumbnailUrl).ToList();
    }

    [RelayCommand]
    private Task Retry()
    {
        LoadFailed = false;
        return LoadMoreAsync();
    }

    /// <summary>Starts over (picks up newly uploaded clips).</summary>
    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void PlayVideo(SongItem? video)
    {
        if (video != null) Play(ReadSoFar(), video);
    }

    [RelayCommand]
    private void PlayAll()
    {
        var videos = ReadSoFar();
        if (videos.Count > 0) Play(videos, videos[0]);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        var videos = ReadSoFar();
        if (videos.Count == 0) return;
        var rng = new Random();
        for (var i = videos.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (videos[i], videos[j]) = (videos[j], videos[i]);
        }
        Play(videos, videos[0]);
    }

    /// <summary>Everything read so far, including what has not reached the page yet.</summary>
    private List<SongItem> ReadSoFar() => _reader?.Known.ToList() ?? new List<SongItem>();

    private void Play(List<SongItem> videos, SongItem start)
    {
        if (PlayVideoRequest != null)
        {
            PlayVideoRequest(videos, start);
            return;
        }
        var index = videos.IndexOf(start);
        _queue.SetQueue(videos, index < 0 ? 0 : index);
    }
}
