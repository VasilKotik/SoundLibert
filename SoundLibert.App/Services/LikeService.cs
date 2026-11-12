using System.Diagnostics;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Liking is LOCAL ONLY, by design. The YouTube Music account is the read-only
/// source of truth for the library (likes removed on YouTube disappear here on
/// the next library refresh), but the app never writes likes back to YouTube —
/// a like added here simply lives in the local library.
/// <para>
/// The likes are also kept in memory, by id and by song (<see cref="SongIdentity.Key(SongItem)"/>),
/// so every list can ask "is this liked?" at once and get the same answer for the
/// album's track, the deluxe edition's and the music video an album page lists.
/// </para>
/// </summary>
internal sealed class LikeService : ILikeService
{
    private readonly IMusicDatabase _database;
    private readonly IAccountService _account;
    private readonly object _gate = new();

    private HashSet<string> _ids = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> _idsBySong = new(StringComparer.Ordinal);
    private Dictionary<string, string> _songById = new(StringComparer.Ordinal);
    private Task? _firstLoad;

    public event EventHandler<LikeChangedEventArgs>? LikeChanged;
    public event EventHandler? LikesReloaded;

    public LikeService(IMusicDatabase database, IAccountService account)
    {
        _database = database;
        _account = account;
    }

    // ---- Asking ----

    public bool IsLiked(SongItem song)
    {
        lock (_gate)
        {
            if (_ids.Contains(song.Id)) return true;
            if (!string.IsNullOrEmpty(song.SongVersionId) && _ids.Contains(song.SongVersionId)) return true;
            if (!string.IsNullOrEmpty(song.VideoVersionId) && _ids.Contains(song.VideoVersionId)) return true;
            if (_idsBySong.Count == 0) return false;
        }
        var key = SongIdentity.Key(song);
        if (key.Length == 0) return false;
        lock (_gate) return _idsBySong.ContainsKey(key);
    }

    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        Task load;
        // Started on the pool: nothing of the database work runs under the lock.
        lock (_gate) load = _firstLoad ??= Task.Run(FirstLoadAsync, CancellationToken.None);
        return ct.CanBeCanceled ? load.WaitAsync(ct) : load;
    }

    private async Task FirstLoadAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Likes] first load failed: {ex.Message}");
            lock (_gate) _firstLoad = null;
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var rows = await _database.GetLikedSongsAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var idsBySong = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var songById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ids.Add(row.Id);
            var key = SongIdentity.Key(row.ToSongItem());
            if (key.Length == 0) continue;
            songById[row.Id] = key;
            if (!idsBySong.TryGetValue(key, out var versions)) idsBySong[key] = versions = new(StringComparer.Ordinal);
            versions.Add(row.Id);
        }
        lock (_gate)
        {
            _ids = ids;
            _idsBySong = idsBySong;
            _songById = songById;
            _firstLoad ??= Task.CompletedTask;
        }
        LikesReloaded?.Invoke(this, EventArgs.Empty);
    }

    // ---- Changing ----

    public async Task<bool> ToggleAsync(SongItem song, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return await SetLikedAsync(song, !IsLiked(song), ct).ConfigureAwait(false);
    }

    public async Task<bool> SetLikedAsync(SongItem song, bool liked, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var scope = _account.AccountScopeKey;
        // The ids whose like actually changed (each now in the state asked for).
        var changed = new List<string>();

        if (liked)
        {
            // Likes added from catalog/search rows may not exist in the database yet.
            // Insert metadata only for a new row; never upsert a stale like bit over
            // an existing row before toggling it.
            var row = await _database.GetSongAsync(song.Id, ct).ConfigureAwait(false);
            if (row is null)
            {
                var entity = song.ToEntity();
                entity.IsLiked = false;
                entity.IsYouTubeLiked = false;
                entity.IsSoundLibertLiked = false;
                await _database.UpsertSongAsync(entity, ct).ConfigureAwait(false);
            }
            if (row?.IsLiked == true || await _database.ToggleLikeAsync(song.Id, scope, ct).ConfigureAwait(false))
            {
                Remember(song.Id, SongIdentity.Key(song));
                if (row?.IsLiked != true) changed.Add(song.Id);
            }
        }
        else
        {
            // Every version the song is liked in: unliking one copy while another
            // stays liked would leave the heart full.
            foreach (var id in LikedVersionsOf(song))
            {
                var row = await _database.GetSongAsync(id, ct).ConfigureAwait(false);
                if (row?.IsLiked == true)
                {
                    // The toggle answers the new state: still liked means it did not take.
                    if (await _database.ToggleLikeAsync(id, scope, ct).ConfigureAwait(false)) continue;
                    changed.Add(id);
                }
                Forget(id);
            }
        }

        var updated = await _database.GetSongAsync(song.Id, ct).ConfigureAwait(false);
        if (updated != null)
        {
            song.IsYouTubeLiked = updated.IsYouTubeLiked;
            song.IsSoundLibertLiked = updated.IsSoundLibertLiked;
        }
        var isLiked = IsLiked(song);
        song.IsLiked = isLiked;

        foreach (var id in changed)
            LikeChanged?.Invoke(this, new LikeChangedEventArgs(id, liked));
        // Nothing changed (already so): the lists still hear the song's state.
        if (changed.Count == 0)
            LikeChanged?.Invoke(this, new LikeChangedEventArgs(song.Id, isLiked));
        return isLiked;
    }

    private List<string> LikedVersionsOf(SongItem song)
    {
        var key = SongIdentity.Key(song);
        lock (_gate)
        {
            // The row's own id always: the memory may be a moment behind the library.
            var ids = new List<string> { song.Id };
            foreach (var id in new[] { song.SongVersionId, song.VideoVersionId })
                if (!string.IsNullOrEmpty(id) && _ids.Contains(id) && !ids.Contains(id)) ids.Add(id);
            if (key.Length > 0 && _idsBySong.TryGetValue(key, out var versions))
                foreach (var id in versions)
                    if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }
    }

    private void Remember(string id, string key)
    {
        lock (_gate)
        {
            _ids.Add(id);
            if (key.Length == 0) return;
            _songById[id] = key;
            if (!_idsBySong.TryGetValue(key, out var versions)) _idsBySong[key] = versions = new(StringComparer.Ordinal);
            versions.Add(id);
        }
    }

    private void Forget(string id)
    {
        lock (_gate)
        {
            _ids.Remove(id);
            if (!_songById.Remove(id, out var key)) return;
            if (_idsBySong.TryGetValue(key, out var versions) && versions.Remove(id) && versions.Count == 0)
                _idsBySong.Remove(key);
        }
    }
}
