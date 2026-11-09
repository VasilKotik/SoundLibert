using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Liking is LOCAL ONLY, by design. The YouTube Music account is the read-only
/// source of truth for the library (likes removed on YouTube disappear here on
/// the next library refresh), but the app never writes likes back to YouTube —
/// a like added here simply lives in the local library.
/// </summary>
internal sealed class LikeService : ILikeService
{
    private readonly IMusicDatabase _database;
    private readonly IAccountService _account;

    public event EventHandler<LikeChangedEventArgs>? LikeChanged;

    public LikeService(IMusicDatabase database, IAccountService account)
    {
        _database = database;
        _account = account;
    }

    public async Task<bool> ToggleAsync(SongItem song, CancellationToken ct = default)
    {
        // Likes added from catalog/search rows may not exist in the database yet.
        // Insert metadata only for a new row; never upsert a stale like bit over
        // an existing row before toggling it.
        if (await _database.GetSongAsync(song.Id, ct) is null)
        {
            var entity = song.ToEntity();
            entity.IsLiked = false;
            entity.IsYouTubeLiked = false;
            entity.IsSoundLibertLiked = false;
            await _database.UpsertSongAsync(entity, ct);
        }

        var isLiked = await _database.ToggleLikeAsync(song.Id, _account.AccountScopeKey, ct);
        var updated = await _database.GetSongAsync(song.Id, ct);
        if (updated != null)
        {
            song.IsYouTubeLiked = updated.IsYouTubeLiked;
            song.IsSoundLibertLiked = updated.IsSoundLibertLiked;
            song.IsLiked = updated.IsLiked;
        }
        LikeChanged?.Invoke(this, new LikeChangedEventArgs(song.Id, isLiked));
        return isLiked;
    }
}
