using Microsoft.Data.Sqlite;
using SoundLibert.Domain.Lyrics;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.SQLite;

namespace SoundLibert.Domain.Tests;

public sealed class LyricsCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogueCleanup_KeepsOwnedRecordingCachesAndRemovesOrphans(bool signOut)
    {
        var directory = Path.Combine(Path.GetTempPath(), "soundlibert-lyrics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteMusicDatabase(Path.Combine(directory, "cache.db"));
            const string owner = "owner:*?[]:песня";
            const string orphan = "orphan";
            // A download survives both pruning and sign-out. Its counterpart
            // deliberately has no songs row, as happens for resolved versions.
            await database.UpsertSongAsync(new SongEntity { Id = owner, DownloadPath = "kept.m4a" });
            await database.UpsertSongAsync(new SongEntity { Id = orphan });
            var ownedKeys = new[]
            {
                owner,
                LyricsPlaybackPolicy.CacheKey(owner, "counterpart", 267),
                LyricsPlaybackPolicy.CacheKey(owner, "counterpart", 341)
            };
            var orphanKeys = new[]
            {
                orphan,
                LyricsPlaybackPolicy.CacheKey(orphan, "counterpart", 267),
                LyricsPlaybackPolicy.CacheKey("never-stored", "counterpart", 267)
            };
            foreach (var key in ownedKeys.Concat(orphanKeys))
                await database.UpsertLyricsAsync(key, "[00:01.00]timed cache");

            if (signOut) await database.ClearAccountDataAsync();
            else await database.PruneOrphanCatalogueAsync();

            Assert.NotNull(await database.GetSongAsync(owner));
            Assert.Null(await database.GetSongAsync(orphan));
            foreach (var key in ownedKeys)
                Assert.Equal("[00:01.00]timed cache", await database.GetLyricsAsync(key));
            foreach (var key in orphanKeys)
                Assert.Null(await database.GetLyricsAsync(key));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
