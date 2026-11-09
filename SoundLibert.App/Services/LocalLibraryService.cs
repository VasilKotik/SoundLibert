using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Scans folders for local audio files and stores them as ordinary songs with
/// a <see cref="SongEntity.LocalPath"/>. Title/artist are inferred from the file
/// name ("Artist - Title"), the album from the containing folder — no tag
/// library dependency, and nothing is lost because the path is what plays.
/// </summary>
internal sealed class LocalLibraryService : ILocalLibrary
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };

    private readonly IMusicDatabase _database;
    private readonly IMediaProbe _probe;
    private readonly IAccountService _account;

    public LocalLibraryService(IMusicDatabase database, IMediaProbe probe, IAccountService account)
    {
        _database = database;
        _probe = probe;
        _account = account;
    }

    public async Task<int> ImportFolderAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        // Build all entities first, then persist them in ONE transaction: this
        // is what makes scanning a large library fast instead of minutes.
        // Files are stamped with the current account scope (per-account files).
        // Probing every file takes a while, so none of it runs on the caller's thread.
        var scope = _account.AccountScopeKey ?? "";
        var entities = await Task.Run(() => ScanFolder(folder, scope, ct), ct);

        if (entities.Count > 0)
            await _database.UpsertSongsAsync(entities, ct);

        Trace.WriteLine($"[LocalImport] imported {entities.Count} files");
        return entities.Count;
    }

    private List<SongEntity> ScanFolder(string folder, string scope, CancellationToken ct)
    {
        // A protected subfolder (System Volume Information on a drive root, a
        // junction like "My Music") must not abort the whole import.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var entities = new List<SongEntity>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(Path.GetExtension(file))) continue;

            try
            {
                var entity = BuildEntity(file);
                entity.AccountKey = scope;
                entities.Add(entity);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalImport] {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return entities;
    }

    private SongEntity BuildEntity(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var (artist, title) = SplitArtistTitle(name);

        return new SongEntity
        {
            Id = LocalId(path),
            Title = title,
            ArtistsJson = System.Text.Json.JsonSerializer.Serialize(new[] { artist }),
            AlbumTitle = Path.GetFileName(Path.GetDirectoryName(path)),
            DurationSeconds = _probe.GetDurationSeconds(path),
            ThumbnailUrl = string.Empty,
            LocalPath = path
        };
    }

    private static (string Artist, string Title) SplitArtistTitle(string fileName)
    {
        // "Artist - Title" is the common convention; otherwise the file name is the title.
        var index = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (index > 0)
        {
            var artist = fileName[..index].Trim();
            var title = fileName[(index + 3)..].Trim();
            if (artist.Length > 0 && title.Length > 0) return (artist, title);
        }
        return ("Unknown artist", fileName.Trim());
    }

    private static string LocalId(string path)
    {
        var normalized = Path.GetFullPath(path).ToLowerInvariant();

        // Hash straight from the string span into a stack buffer — no throwaway
        // byte[] allocation per file (matters when indexing thousands of files).
        var maxBytes = Encoding.UTF8.GetMaxByteCount(normalized.Length);
        Span<byte> utf8 = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        var written = Encoding.UTF8.GetBytes(normalized.AsSpan(), utf8);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(utf8[..written], hash);
        return "local:" + Convert.ToHexString(hash);
    }
}
