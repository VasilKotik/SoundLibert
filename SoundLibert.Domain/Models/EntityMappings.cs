using System.Text.Json;

namespace SoundLibert.Domain.Models;

/// <summary>
/// Conversions between the lightweight catalog items (returned by
/// <c>IMusicProvider</c>) and the persistent entity rows stored in SQLite.
/// </summary>
public static class EntityMappings
{
    /// <summary>
    /// Projects a catalog song into a persistable row. Artist names are stored
    /// as a JSON string array because SQLite has no array type and grouping by
    /// artists happens in C# (see SqliteMusicDatabase.GetPlayStatsAsync).
    /// </summary>
    public static SongEntity ToEntity(this SongItem song) => new()
    {
        Id = song.Id,
        Title = song.Title,
        ArtistsJson = JsonSerializer.Serialize(song.Artists.Select(a => a.Name)),
        AlbumTitle = song.AlbumTitle,
        AlbumId = song.AlbumId,
        DurationSeconds = song.DurationSeconds,
        ThumbnailUrl = song.ThumbnailUrl,
        IsExplicit = song.IsExplicit,
        IsLiked = song.IsLiked,
        IsYouTubeLiked = song.IsYouTubeLiked,
        IsSoundLibertLiked = song.IsSoundLibertLiked,
        IsLibrary = song.IsLibrary,
        LocalPath = song.LocalPath,
        DownloadPath = song.DownloadPath,
        DownloadedAt = song.DownloadedAt,
        VideoType = song.VideoType,
        SongVersionId = song.SongVersionId,
        VideoVersionId = song.VideoVersionId,
        ViewCount = song.ViewCount
    };

    /// <summary>Rebuilds a playable catalog item from a persisted row.</summary>
    public static SongItem ToSongItem(this SongEntity entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        Artists = ParseArtistNames(entity.ArtistsJson)
            .Select(n => new Artist { Name = n })
            .ToArray(),
        AlbumTitle = entity.AlbumTitle,
        AlbumId = entity.AlbumId,
        DurationSeconds = entity.DurationSeconds,
        ThumbnailUrl = entity.ThumbnailUrl,
        IsExplicit = entity.IsExplicit,
        IsLiked = entity.IsLiked,
        IsYouTubeLiked = entity.IsYouTubeLiked,
        IsSoundLibertLiked = entity.IsSoundLibertLiked,
        IsLibrary = entity.IsLibrary,
        LocalPath = entity.LocalPath,
        IsLocal = !string.IsNullOrEmpty(entity.LocalPath),
        DownloadPath = entity.DownloadPath,
        DownloadedAt = entity.DownloadedAt,
        IsDownloaded = !string.IsNullOrEmpty(entity.DownloadPath),
        VideoType = entity.VideoType,
        SongVersionId = entity.SongVersionId,
        VideoVersionId = entity.VideoVersionId,
        ViewCount = entity.ViewCount
    };

    /// <summary>Rebuilds a playable item from a history row.</summary>
    public static SongItem ToSongItem(this PlayHistoryItem item) => new()
    {
        Id = item.SongId,
        Title = item.Title,
        Artists = string.IsNullOrWhiteSpace(item.Artist)
            ? Array.Empty<Artist>()
            : new[] { new Artist { Name = item.Artist! } },
        DurationSeconds = item.DurationSeconds,
        ThumbnailUrl = item.ThumbnailUrl ?? string.Empty
    };

    private static string[] ParseArtistNames(string artistsJson)
    {
        if (string.IsNullOrWhiteSpace(artistsJson)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(artistsJson) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static ArtistEntity ToEntity(this ArtistItem artist) => new()
    {
        Id = artist.Id,
        Name = artist.Name,
        ThumbnailUrl = artist.ThumbnailUrl
    };

    public static ArtistItem ToArtistItem(this ArtistEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        ThumbnailUrl = entity.ThumbnailUrl,
        ChannelId = entity.ChannelId
    };

    public static AlbumItem ToAlbumItem(this AlbumEntity entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        PlaylistId = entity.PlaylistId,
        Artists = ParseArtistNames(entity.ArtistsJson).Select(n => new Artist { Name = n }).ToArray(),
        ThumbnailUrl = entity.ThumbnailUrl,
        Year = entity.Year,
        IsExplicit = entity.IsExplicit
    };

    public static AlbumEntity ToEntity(this AlbumItem album) => new()
    {
        Id = album.Id,
        Title = album.Title,
        PlaylistId = album.PlaylistId,
        ArtistsJson = JsonSerializer.Serialize(album.Artists.Select(a => a.Name)),
        ThumbnailUrl = album.ThumbnailUrl,
        Year = album.Year,
        IsExplicit = album.IsExplicit
    };

    public static PlaylistEntity ToEntity(this PlaylistItem playlist) => new()
    {
        Id = playlist.Id,
        Title = playlist.Title,
        BrowseId = playlist.Id,
        ThumbnailUrl = playlist.ThumbnailUrl,
        IsEditable = playlist.IsEditable
    };
}
