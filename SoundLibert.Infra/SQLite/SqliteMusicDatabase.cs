using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.Infra.SQLite;

/// <summary>
/// Real SQLite-backed store for likes, library, play history, search history
/// and cached lyrics. Chosen over EF Core because the surface is small
/// CRUD + a few aggregates: raw SQL keeps startup fast and avoids a
/// migration toolchain (see ARCHITECTURE_DECISIONS.md AD-15).
///
/// The DB file lives next to the log at
/// %LocalAppData%\SoundLibert\soundlibert.db. A single connection is opened
/// per operation (SQLite is fast at this) and WAL mode keeps reads from
/// blocking writes. Schema creation is lazy and guarded by a semaphore so the
/// DB is usable even if the caller never awaited InitializeAsync explicitly.
/// </summary>
public sealed class SqliteMusicDatabase : IMusicDatabase
{
    private const int SchemaVersion = 22;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteMusicDatabase(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundLibert");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "soundlibert.db");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: WAL then lets readers run during a write. Shared
            // cache locks whole tables and would block them instead.
            Cache = SqliteCacheMode.Private
        }.ToString();

        DbPath = dbPath;
    }

    public string DbPath { get; }

    // ---------------------------------------------------------------- schema

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            try
            {
                await CreateOrMigrateAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                // Without this the whole library silently stays empty on every
                // launch. The damaged file is kept next to the new one.
                System.Diagnostics.Trace.WriteLine($"[Database] {ex.Message} Setting the damaged database aside.");
                SetAsideDamagedDatabase();
                await CreateOrMigrateAsync(ct);
            }

            _initialized = true;
            System.Diagnostics.Trace.WriteLine($"[Database] Initialized schema v{SchemaVersion} at {DbPath}");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private void SetAsideDamagedDatabase()
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = DbPath + suffix;
            if (File.Exists(file)) File.Move(file, $"{DbPath}.damaged-{stamp}{suffix}");
        }
    }

    private async Task CreateOrMigrateAsync(CancellationToken ct)
    {
        {
            await using var conn = await OpenAsync(ct);
            int previousSchemaVersion;
            await using (var previousVersion = conn.CreateCommand())
            {
                previousVersion.CommandText = "PRAGMA user_version;";
                previousSchemaVersion = Convert.ToInt32(await previousVersion.ExecuteScalarAsync(ct) ?? 0);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;

                CREATE TABLE IF NOT EXISTS songs (
                    Id              TEXT PRIMARY KEY,
                    Title           TEXT NOT NULL DEFAULT '',
                    ArtistsJson     TEXT NOT NULL DEFAULT '[]',
                    AlbumTitle      TEXT,
                    AlbumId         TEXT,
                    DurationSeconds INTEGER NOT NULL DEFAULT 0,
                    ThumbnailUrl    TEXT NOT NULL DEFAULT '',
                    IsExplicit      INTEGER NOT NULL DEFAULT 0,
                    IsLiked         INTEGER NOT NULL DEFAULT 0,
                    IsYouTubeLiked  INTEGER NOT NULL DEFAULT 0,
                    IsSoundLibertLiked INTEGER NOT NULL DEFAULT 0,
                    IsLocalUnliked  INTEGER NOT NULL DEFAULT 0,
                    IsLibrary       INTEGER NOT NULL DEFAULT 0,
                    TotalPlayTimeMs INTEGER NOT NULL DEFAULT 0,
                    PlayCount       INTEGER NOT NULL DEFAULT 0,
                    LastPlayedAt    TEXT,
                    BookmarkedAt    TEXT,
                    CreatedAt       TEXT NOT NULL,
                    LocalPath       TEXT,
                    VideoType       TEXT,
                    SongVersionId   TEXT,
                    VideoVersionId  TEXT,
                    ClipMissAt      TEXT,
                    DownloadPath    TEXT,
                    DownloadedAt    TEXT,
                    ViewCount       INTEGER NOT NULL DEFAULT 0,
                    ViewCountAt     TEXT
                );

                CREATE TABLE IF NOT EXISTS artists (
                    Id           TEXT PRIMARY KEY,
                    Name         TEXT NOT NULL DEFAULT '',
                    ThumbnailUrl TEXT NOT NULL DEFAULT '',
                    ChannelId    TEXT,
                    LastUpdated  TEXT,
                    BookmarkedAt TEXT,
                    IsYouTubeSubscribed INTEGER NOT NULL DEFAULT 0,
                    IsLocalUnsubscribed INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS albums (
                    Id           TEXT PRIMARY KEY,
                    Title        TEXT NOT NULL DEFAULT '',
                    PlaylistId   TEXT,
                    ArtistsJson  TEXT NOT NULL DEFAULT '[]',
                    ThumbnailUrl TEXT NOT NULL DEFAULT '',
                    Year         TEXT,
                    IsExplicit   INTEGER NOT NULL DEFAULT 0,
                    BookmarkedAt TEXT
                );

                CREATE TABLE IF NOT EXISTS playlists (
                    Id           TEXT PRIMARY KEY,
                    Title        TEXT NOT NULL DEFAULT '',
                    BrowseId     TEXT,
                    ThumbnailUrl TEXT,
                    IsEditable   INTEGER NOT NULL DEFAULT 0,
                    BookmarkedAt TEXT,
                    LastUpdated  TEXT,
                    SortOrder    INTEGER NOT NULL DEFAULT 0,
                    IsLocalSaved INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS playlist_items (
                    PlaylistId TEXT NOT NULL,
                    SongId     TEXT NOT NULL,
                    Position   INTEGER NOT NULL DEFAULT 0,
                    AddedAt    TEXT NOT NULL,
                    PRIMARY KEY (PlaylistId, SongId)
                );
                CREATE INDEX IF NOT EXISTS ix_playlist_items_playlist ON playlist_items(PlaylistId);

                CREATE TABLE IF NOT EXISTS play_events (
                    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    SongId         TEXT NOT NULL,
                    Timestamp      TEXT NOT NULL,
                    PlayDurationMs INTEGER NOT NULL DEFAULT 0,
                    PlaylistId     TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_play_events_song ON play_events(SongId);
                CREATE INDEX IF NOT EXISTS ix_play_events_time ON play_events(Timestamp);

                CREATE TABLE IF NOT EXISTS search_history (
                    Query     TEXT PRIMARY KEY,
                    Timestamp TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS lyrics (
                    SongId    TEXT PRIMARY KEY,
                    Content   TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS artist_genres (
                    Artist    TEXT PRIMARY KEY,
                    Genre     TEXT,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS song_genres (
                    SongId    TEXT PRIMARY KEY,
                    Genre     TEXT,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);

            // v1 → v2: songs.LocalPath (local-music import).
            // v3 → v4: songs.VideoType (YouTube Music ATV/OMV/UGC classification).
            // v4 → v5: songs.SongVersionId / VideoVersionId (resolved official
            //           versions, cached so discovery is skipped after a restart).
            // v10 → v11: songs.ClipMissAt (negative clip cache with TTL).
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var columns = conn.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(songs);";
                await using (var r = await columns.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                        existingColumns.Add(r.GetString(1));
                }
            }
            foreach (var column in new[] { "LocalPath", "VideoType", "SongVersionId", "VideoVersionId", "ClipMissAt", "DownloadPath", "DownloadedAt" })
            {
                if (existingColumns.Contains(column)) continue;
                await using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE songs ADD COLUMN {column} TEXT;";
                await alter.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Migrated songs.{column}");
            }

            // v18 → v19: songs.ViewCount / ViewCountAt (global YouTube view counts,
            // cached like clip versions so lists show counts without a per-row call).
            if (!existingColumns.Contains("ViewCount"))
            {
                await using var addViewCount = conn.CreateCommand();
                addViewCount.CommandText = "ALTER TABLE songs ADD COLUMN ViewCount INTEGER NOT NULL DEFAULT 0;";
                await addViewCount.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated songs.ViewCount");
            }
            if (!existingColumns.Contains("ViewCountAt"))
            {
                await using var addViewCountAt = conn.CreateCommand();
                addViewCountAt.CommandText = "ALTER TABLE songs ADD COLUMN ViewCountAt TEXT;";
                await addViewCountAt.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated songs.ViewCountAt");
            }

            // v5 → v6: playlists.SortOrder (manual card order in the library).
            // v6 → v7: split account-imported YouTube likes from SoundLibert likes.
            // v8 → v9: songs.IsLocalUnliked (an explicit in-app unlike of an
            //          account-liked song survives account sync).
            var playlistColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var pc = conn.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(playlists);";
                await using (var r = await pc.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) playlistColumns.Add(r.GetString(1));
            }
            if (!playlistColumns.Contains("SortOrder"))
            {
                await using var alterOrder = conn.CreateCommand();
                alterOrder.CommandText = "ALTER TABLE playlists ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;";
                await alterOrder.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated playlists.SortOrder");
            }

            // v13 → v14: local playlists are scoped per account (each sign-in
            // sees its own). The v13 push-mapping column never shipped a
            // release — drop it — and add the scope key instead.
            if (playlistColumns.Contains("AccountPlaylistId"))
            {
                await using var dropPushed = conn.CreateCommand();
                dropPushed.CommandText = "ALTER TABLE playlists DROP COLUMN AccountPlaylistId;";
                await dropPushed.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Dropped playlists.AccountPlaylistId");
            }
            if (!playlistColumns.Contains("AccountKey"))
            {
                await using var alterKey = conn.CreateCommand();
                alterKey.CommandText = "ALTER TABLE playlists ADD COLUMN AccountKey TEXT;";
                await alterKey.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated playlists.AccountKey");
            }

            // v15 → v16: album like sources (same model as songs: imported
            // YouTube saves vs local hearts + a veto so unlikes stick).
            var albumCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var ac = conn.CreateCommand())
            {
                ac.CommandText = "PRAGMA table_info(albums);";
                await using var r = await ac.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) albumCols.Add(r.GetString(1));
            }
            foreach (var (col, ddl) in new[]
                     {
                         ("IsYouTubeLiked", "ALTER TABLE albums ADD COLUMN IsYouTubeLiked INTEGER NOT NULL DEFAULT 0;"),
                         ("IsLocalUnliked", "ALTER TABLE albums ADD COLUMN IsLocalUnliked INTEGER NOT NULL DEFAULT 0;")
                     })
            {
                if (albumCols.Contains(col)) continue;
                await using var alter = conn.CreateCommand();
                alter.CommandText = ddl;
                await alter.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Migrated albums.{col}");
            }

            // v20 → v21: artist subscription sources (same read-only model as
            // albums: imported YouTube subscriptions vs local follows + a veto),
            // and a local-only flag for saved community playlists.
            var artistCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var arc = conn.CreateCommand())
            {
                arc.CommandText = "PRAGMA table_info(artists);";
                await using var r = await arc.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) artistCols.Add(r.GetString(1));
            }
            foreach (var (col, ddl) in new[]
                     {
                         ("IsYouTubeSubscribed", "ALTER TABLE artists ADD COLUMN IsYouTubeSubscribed INTEGER NOT NULL DEFAULT 0;"),
                         ("IsLocalUnsubscribed", "ALTER TABLE artists ADD COLUMN IsLocalUnsubscribed INTEGER NOT NULL DEFAULT 0;")
                     })
            {
                if (artistCols.Contains(col)) continue;
                await using var alter = conn.CreateCommand();
                alter.CommandText = ddl;
                await alter.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Migrated artists.{col}");
            }
            if (!playlistColumns.Contains("IsLocalSaved"))
            {
                await using var alterSaved = conn.CreateCommand();
                alterSaved.CommandText = "ALTER TABLE playlists ADD COLUMN IsLocalSaved INTEGER NOT NULL DEFAULT 0;";
                await alterSaved.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated playlists.IsLocalSaved");
            }
            await using (var la = conn.CreateCommand())
            {
                la.CommandText = """
                    CREATE TABLE IF NOT EXISTS local_artists (
                        ArtistId TEXT NOT NULL,
                        AccountKey TEXT NOT NULL DEFAULT '',
                        BookmarkedAt TEXT,
                        PRIMARY KEY (ArtistId, AccountKey));
                    """;
                await la.ExecuteNonQueryAsync(ct);
            }

            // v14 → v15: per-account local data. Scope convention is '' for the
            // device-local scope (plain = comparisons, no NULL pitfalls).
            // local_likes is the truth for SoundLibert hearts; the songs flags
            // stay as a materialized current-scope view (ApplyLikeScopeAsync).
            await using (var ll = conn.CreateCommand())
            {
                ll.CommandText = """
                    CREATE TABLE IF NOT EXISTS local_likes (
                        SongId TEXT NOT NULL,
                        AccountKey TEXT NOT NULL DEFAULT '',
                        BookmarkedAt TEXT,
                        PRIMARY KEY (SongId, AccountKey));
                    """;
                await ll.ExecuteNonQueryAsync(ct);
            }
            foreach (var (table, col) in new[] { ("play_events", "AccountKey"), ("search_history", "AccountKey"), ("songs", "AccountKey") })
            {
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var pc = conn.CreateCommand())
                {
                    pc.CommandText = $"PRAGMA table_info({table});";
                    await using var r = await pc.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) cols.Add(r.GetString(1));
                }
                if (!cols.Contains(col))
                {
                    await using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {col} TEXT;";
                    await alter.ExecuteNonQueryAsync(ct);
                    System.Diagnostics.Trace.WriteLine($"[Database] Migrated {table}.{col}");
                }
            }
            // search_history needs a composite key (Query, scope): rebuild once.
            // Shaped-based guard (not column-based): an earlier pass may have
            // added the column without rebuilding the key.
            var shPk = new List<(int Seq, string Name)>();
            await using (var shc = conn.CreateCommand())
            {
                shc.CommandText = "PRAGMA table_info(search_history);";
                await using var r = await shc.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (!r.IsDBNull(5) && r.GetInt32(5) > 0)
                        shPk.Add((r.GetInt32(5), r.GetString(1)));
                }
            }
            var shPkNames = shPk.OrderBy(x => x.Seq).Select(x => x.Name).ToList();
            if (!shPkNames.SequenceEqual(new[] { "Query", "AccountKey" }, StringComparer.OrdinalIgnoreCase))
            {
                await using var rebuild = conn.CreateCommand();
                rebuild.CommandText = """
                    CREATE TABLE search_history_new (
                        Query TEXT NOT NULL,
                        AccountKey TEXT NOT NULL DEFAULT '',
                        Timestamp TEXT NOT NULL,
                        PRIMARY KEY (Query, AccountKey));
                    INSERT OR IGNORE INTO search_history_new (Query, AccountKey, Timestamp)
                        SELECT Query, '', Timestamp FROM search_history;
                    DROP TABLE search_history;
                    ALTER TABLE search_history_new RENAME TO search_history;
                    """;
                await rebuild.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Rebuilt search_history with scope key");
            }

            await using (var norm = conn.CreateCommand())
            {
                // One-time: pre-scope rows join the device-local scope; existing
                // local hearts move into the side table (columns are re-derived
                // per scope from then on — see ApplyLikeScopeAsync). Idempotent.
                norm.CommandText = """
                    UPDATE playlists SET AccountKey='' WHERE AccountKey IS NULL;
                    UPDATE play_events SET AccountKey='' WHERE AccountKey IS NULL;
                    UPDATE songs SET AccountKey='' WHERE AccountKey IS NULL;
                    INSERT OR IGNORE INTO local_likes (SongId, AccountKey, BookmarkedAt)
                        SELECT Id, '', BookmarkedAt FROM songs WHERE IsSoundLibertLiked=1;
                    """;
                await norm.ExecuteNonQueryAsync(ct);
            }

            if (previousSchemaVersion < 8)
            {
                // Drop the old combined like snapshot once. The next account
                // reconciliation imports YouTube likes into the single unified
                // liked collection; future unlikes then persist as local overrides.
                await using var resetLikes = conn.CreateCommand();
                resetLikes.CommandText = """
                    UPDATE songs
                       SET IsLiked=0, IsYouTubeLiked=0, IsSoundLibertLiked=0, BookmarkedAt=NULL;
                    """;
                await resetLikes.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Reset legacy combined liked-song state");
            }

            if (previousSchemaVersion < 10)
            {
                // Drop stale GUESSED version pins (a wrong clip pinned by the
                // old search-only heuristics played "a different song" forever,
                // because cached pins are never re-resolved). User-confirmed
                // pins survive: clips pinned to their own id (PlayAsVideo /
                // seed-is-video) have VideoVersionId == Id and are untouched.
                // Cleared pins re-discover lazily with the hardened logic.
                await using var resetVersions = conn.CreateCommand();
                resetVersions.CommandText = """
                    UPDATE songs
                       SET SongVersionId=NULL, VideoVersionId=NULL
                     WHERE VideoVersionId IS NOT NULL AND VideoVersionId != Id;
                    """;
                var cleared = await resetVersions.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Cleared {cleared} stale guessed version pins");
            }

            if (previousSchemaVersion < 12)
            {
                // Lyrics matcher got stricter (±2s hard rule, Velune parity):
                // texts cached by the old loose fallback may carry another
                // recording's timings and would desync forever (the cache has
                // no TTL). They refetch automatically on next play.
                await using var resetLyrics = conn.CreateCommand();
                resetLyrics.CommandText = "DELETE FROM lyrics;";
                var clearedLyrics = await resetLyrics.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Cleared {clearedLyrics} cached lyrics (matcher strictness upgrade)");
            }

            if (previousSchemaVersion < 17)
            {
                // Clip gates got materially stricter since the v10 purge
                // (topic-channel reject, strict channel check for UGC rows,
                // channel-authoritative discovery first): pins guessed under
                // the old gates can point at fan uploads with clean titles
                // that the row-less background check can never catch, so drop
                // them once. User-confirmed pins (VideoVersionId == Id) and
                // the negative clip cache are untouched; cleared pins
                // re-discover lazily with the hardened logic.
                await using var resetVersions17 = conn.CreateCommand();
                resetVersions17.CommandText = """
                    UPDATE songs
                       SET SongVersionId=NULL, VideoVersionId=NULL
                     WHERE VideoVersionId IS NOT NULL AND VideoVersionId != Id;
                    """;
                var cleared17 = await resetVersions17.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Cleared {cleared17} stale guessed version pins (v17 gates)");
            }

            if (previousSchemaVersion < 20)
            {
                // The "official non-clip" fallback tier was removed: pins that
                // point at an Official Lyric/Audio/Live upload (no real video
                // stream) made the Canvas/Video player stall and polluted the
                // "Music videos" shelf. Drop cross-id pins once so they
                // re-discover as genuine music videos only. Self-pins
                // (VideoVersionId == Id, real clip rows) survive.
                await using var resetVersions20 = conn.CreateCommand();
                resetVersions20.CommandText = """
                    UPDATE songs
                       SET SongVersionId=NULL, VideoVersionId=NULL
                     WHERE VideoVersionId IS NOT NULL AND VideoVersionId != Id;
                    """;
                var cleared20 = await resetVersions20.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine($"[Database] Cleared {cleared20} non-clip version pins (v20)");
            }

            if (previousSchemaVersion < 21)
            {
                // Artist follows were previously written through to YouTube, so
                // every existing bookmark is account-origin: mark it imported
                // (read-only). The next sync reconciles it against the account
                // (kept if still subscribed, dropped otherwise). Local follows
                // created afterwards use the local_artists side table.
                await using var migrateArtistSubs = conn.CreateCommand();
                migrateArtistSubs.CommandText =
                    "UPDATE artists SET IsYouTubeSubscribed = 1 " +
                    "WHERE BookmarkedAt IS NOT NULL AND IsYouTubeSubscribed = 0;";
                var migratedSubs = await migrateArtistSubs.ExecuteNonQueryAsync(ct);
                if (migratedSubs > 0)
                    System.Diagnostics.Trace.WriteLine($"[Database] Marked {migratedSubs} artist bookmarks as YouTube-subscribed (v21)");

                // Clip discovery used to stamp a "no clip" negative cache even
                // when every search failed offline, which suppressed clips for
                // 7 days once the user was back online (most visible on
                // downloaded songs). Discovery no longer does that; clear the
                // poisoned entries once so affected tracks re-discover now.
                await using var clearClipMiss = conn.CreateCommand();
                clearClipMiss.CommandText = "UPDATE songs SET ClipMissAt = NULL WHERE ClipMissAt IS NOT NULL;";
                var clearedMiss = await clearClipMiss.ExecuteNonQueryAsync(ct);
                if (clearedMiss > 0)
                    System.Diagnostics.Trace.WriteLine($"[Database] Cleared {clearedMiss} poisoned clip-miss records (v21)");
            }

            if (previousSchemaVersion < 22)
            {
                // Clear the clip-miss negative cache once more. The v21 pass
                // could run before a stale build re-stamped false misses
                // (offline discovery used to cache "no clip" for 7 days, which
                // silently removed the Canvas from songs that DO have an
                // official video — most visible on downloaded tracks). A fresh
                // stamp with the corrected discovery is trustworthy, so this is
                // a one-time reset, not a permanent disable.
                await using var clearClipMiss22 = conn.CreateCommand();
                clearClipMiss22.CommandText = "UPDATE songs SET ClipMissAt = NULL WHERE ClipMissAt IS NOT NULL;";
                var clearedMiss22 = await clearClipMiss22.ExecuteNonQueryAsync(ct);
                if (clearedMiss22 > 0)
                    System.Diagnostics.Trace.WriteLine($"[Database] Cleared {clearedMiss22} clip-miss records (v22)");
            }

            var hadLikeSourceColumns = existingColumns.Contains("IsYouTubeLiked") ||
                                       existingColumns.Contains("IsSoundLibertLiked");
            if (!existingColumns.Contains("IsYouTubeLiked"))
            {
                await using var addYouTubeLiked = conn.CreateCommand();
                addYouTubeLiked.CommandText = "ALTER TABLE songs ADD COLUMN IsYouTubeLiked INTEGER NOT NULL DEFAULT 0;";
                await addYouTubeLiked.ExecuteNonQueryAsync(ct);
            }
            if (!existingColumns.Contains("IsSoundLibertLiked"))
            {
                await using var addSoundLibertLiked = conn.CreateCommand();
                addSoundLibertLiked.CommandText = "ALTER TABLE songs ADD COLUMN IsSoundLibertLiked INTEGER NOT NULL DEFAULT 0;";
                await addSoundLibertLiked.ExecuteNonQueryAsync(ct);
            }
            // v8 → v9: an explicit in-app unlike of an account-liked song is
            // remembered, so account sync cannot resurrect it.
            if (!existingColumns.Contains("IsLocalUnliked"))
            {
                await using var addLocalUnliked = conn.CreateCommand();
                addLocalUnliked.CommandText = "ALTER TABLE songs ADD COLUMN IsLocalUnliked INTEGER NOT NULL DEFAULT 0;";
                await addLocalUnliked.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated songs.IsLocalUnliked");
            }
            if (!hadLikeSourceColumns)
            {
                // v6 had one shared bit. Treat its existing likes as imported
                // account likes; the next successful account sync reconciles it.
                await using var migrateLikes = conn.CreateCommand();
                migrateLikes.CommandText = "UPDATE songs SET IsYouTubeLiked=IsLiked, IsSoundLibertLiked=0;";
                await migrateLikes.ExecuteNonQueryAsync(ct);
                System.Diagnostics.Trace.WriteLine("[Database] Migrated likes into separate sources");
            }

            await using (var versionCmd = conn.CreateCommand())
            {
                versionCmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
                await versionCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        // Avoid transient SQLITE_BUSY when a background history write overlaps a
        // library reload (each operation uses its own short-lived connection).
        await using (var pragma = conn.CreateCommand())
        {
            // NORMAL is safe with WAL (no corruption on a crash) and much faster
            // than the default for the many small writes this app makes.
            pragma.CommandText = "PRAGMA busy_timeout=3000; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct);
        }
        return conn;
    }

    // Microsoft.Data.Sqlite's async methods run synchronously, so without
    // Task.Run every query would block the caller, usually the UI thread (and
    // for up to the whole busy timeout when the database is busy).
    private Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
        => Task.Run(async () =>
        {
            await InitializeAsync(ct);
            await using var conn = await OpenAsync(ct);
            return await work(conn);
        }, ct);

    private Task WithConnectionAsync(Func<SqliteConnection, Task> work, CancellationToken ct)
        => Task.Run(async () =>
        {
            await InitializeAsync(ct);
            await using var conn = await OpenAsync(ct);
            await work(conn);
        }, ct);

    /// <summary>
    /// Loads ids into a temp table for a set-based query: any number of them,
    /// where a giant IN (...) would run into SQLite's bound-variable limit.
    /// </summary>
    private static async Task FillTempIdsAsync(SqliteConnection conn, string table, IEnumerable<string> ids, CancellationToken ct)
    {
        await using (var prepare = conn.CreateCommand())
        {
            prepare.CommandText = $"CREATE TEMP TABLE IF NOT EXISTS {table} (Id TEXT PRIMARY KEY); DELETE FROM {table};";
            await prepare.ExecuteNonQueryAsync(ct);
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = $"INSERT OR IGNORE INTO {table} (Id) VALUES ($id);";
            var parameter = insert.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                parameter.Value = id;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------- songs

    public Task<SongEntity?> GetSongAsync(string id, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM songs WHERE Id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? MapSong(reader) : null;
        }, ct);

    // Rows reach this from many places and some know less than others (a search
    // row without a length, a restored queue entry without a cover), so an empty
    // value never replaces a known one.
    private const string UpsertSongSql = """
        INSERT INTO songs (Id, Title, ArtistsJson, AlbumTitle, AlbumId, DurationSeconds,
                           ThumbnailUrl, IsExplicit, IsLiked, IsYouTubeLiked, IsSoundLibertLiked, IsLibrary, TotalPlayTimeMs,
                           PlayCount, LastPlayedAt, BookmarkedAt, CreatedAt, LocalPath, VideoType,
                           SongVersionId, VideoVersionId, AccountKey, DownloadPath, DownloadedAt,
                           ViewCount, ViewCountAt)
        VALUES ($id,$title,$artists,$albumTitle,$albumId,$dur,$thumb,$explicit,$liked,$youtubeLiked,$soundLibertLiked,
                $library,$totalPlay,$playCount,$lastPlayed,$bookmarked,$created,$localPath,$videoType,
                $songVersion,$videoVersion,$accountKey,$downloadPath,$downloadedAt,
                $viewCount,$viewCountAt)
        ON CONFLICT(Id) DO UPDATE SET
            Title=CASE WHEN excluded.Title<>'' THEN excluded.Title ELSE songs.Title END,
            ArtistsJson=CASE WHEN excluded.ArtistsJson NOT IN ('','[]') THEN excluded.ArtistsJson ELSE songs.ArtistsJson END,
            AlbumTitle=COALESCE(excluded.AlbumTitle, songs.AlbumTitle),
            AlbumId=COALESCE(excluded.AlbumId, songs.AlbumId),
            DurationSeconds=CASE WHEN excluded.DurationSeconds>0 THEN excluded.DurationSeconds ELSE songs.DurationSeconds END,
            ThumbnailUrl=CASE WHEN excluded.ThumbnailUrl<>'' THEN excluded.ThumbnailUrl ELSE songs.ThumbnailUrl END,
            IsExplicit=MAX(songs.IsExplicit, excluded.IsExplicit),
            IsLiked=songs.IsLiked,
            IsLibrary=MAX(songs.IsLibrary, excluded.IsLibrary),
            TotalPlayTimeMs=MAX(songs.TotalPlayTimeMs, excluded.TotalPlayTimeMs),
            PlayCount=MAX(songs.PlayCount, excluded.PlayCount),
            LastPlayedAt=COALESCE(excluded.LastPlayedAt, songs.LastPlayedAt),
            BookmarkedAt=COALESCE(excluded.BookmarkedAt, songs.BookmarkedAt),
            LocalPath=COALESCE(excluded.LocalPath, songs.LocalPath),
            VideoType=COALESCE(excluded.VideoType, songs.VideoType),
            SongVersionId=COALESCE(excluded.SongVersionId, songs.SongVersionId),
            VideoVersionId=COALESCE(excluded.VideoVersionId, songs.VideoVersionId),
            DownloadPath=COALESCE(excluded.DownloadPath, songs.DownloadPath),
            DownloadedAt=COALESCE(excluded.DownloadedAt, songs.DownloadedAt),
            ViewCount=MAX(songs.ViewCount, excluded.ViewCount),
            ViewCountAt=COALESCE(songs.ViewCountAt, excluded.ViewCountAt),
            AccountKey=COALESCE(NULLIF(excluded.AccountKey,''), songs.AccountKey);
        """;

    public Task UpsertSongAsync(SongEntity song, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = UpsertSongSql;
            BindSong(cmd, song);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task UpsertSongsAsync(IEnumerable<SongEntity> songs, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // ONE transaction for the whole batch: per-row commits during a folder
            // import are the main reason large libraries took minutes.
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertSongSql;

            foreach (var song in songs)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters.Clear();
                BindSong(cmd, song);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }, ct);

    public Task<IReadOnlyList<SongEntity>> GetLikedSongsAsync(CancellationToken ct = default)
        => QuerySongsAsync("SELECT * FROM songs WHERE IsLiked=1 ORDER BY BookmarkedAt DESC, Title;", ct);

    public Task<IReadOnlySet<string>> GetYouTubeLikedSongIdsAsync(CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlySet<string>>(async conn =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM songs WHERE IsYouTubeLiked=1;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
            return ids;
        }, ct);

    public Task<IReadOnlySet<string>> GetLikedSongIdsAsync(CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlySet<string>>(async conn =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM songs WHERE IsLiked=1;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
            return ids;
        }, ct);

    public Task ClearAccountDataAsync(CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // Sign-out drops everything stored IN the account, and only that:
            // account playlists, YouTube-sourced like flags (with their vetoes),
            // and orphaned catalogue rows they pulled in. Play history, local
            // files, local playlists and SoundLibert-local likes all stay (the
            // likes live per-scope in local_likes) — the signed-out Home feed
            // keeps working off that local data, same engine as signed in.
            await using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("$pnow", DateTime.UtcNow.ToString("O"));
            cmd.CommandText = $"""
                DELETE FROM playlist_items
                 WHERE PlaylistId NOT LIKE '{PlaylistEntity.LocalIdPrefix}%';
                -- A community playlist saved in the app is local, not the account's.
                DELETE FROM playlists
                 WHERE Id NOT LIKE '{PlaylistEntity.LocalIdPrefix}%' AND IsLocalSaved = 0;
                -- Subscriptions: drop the YouTube side, keep local follows and vetoes
                -- (the local_artists rows need their artist row to show up).
                UPDATE artists SET IsYouTubeSubscribed=0;
                DELETE FROM artists
                 WHERE IsLocalUnsubscribed = 0
                   AND Id NOT IN (SELECT ArtistId FROM local_artists);
                -- Saved albums: drop the YouTube side, keep local hearts and vetoes.
                UPDATE albums SET IsYouTubeLiked=0;

                UPDATE songs
                   SET IsYouTubeLiked=0,
                       IsLiked=IsSoundLibertLiked,
                       BookmarkedAt=CASE WHEN IsSoundLibertLiked=1 THEN BookmarkedAt ELSE NULL END
                 WHERE LocalPath IS NULL OR LocalPath='';
                -- NOTE: IsLocalUnliked (the "deleted by hand" veto) is deliberately
                -- KEPT across sign-out: clearing it resurrected in-app deletions
                -- on the next sign-in. Reconcile drops it only when YouTube
                -- itself drops the like.

                -- Device-scope like view after the flags above (same rules as
                -- ApplyLikeScopeAsync, inlined to avoid nesting connections).
                UPDATE songs
                   SET IsSoundLibertLiked = EXISTS(
                           SELECT 1 FROM local_likes l
                            WHERE l.SongId = songs.Id AND l.AccountKey = ''),
                       IsLiked = CASE
                           WHEN IsLocalUnliked = 1 AND IsYouTubeLiked = 1 THEN 0
                           WHEN IsYouTubeLiked = 1 THEN 1
                           WHEN EXISTS(SELECT 1 FROM local_likes l
                                        WHERE l.SongId = songs.Id AND l.AccountKey = '') THEN 1
                           ELSE 0 END,
                       BookmarkedAt = CASE
                           WHEN EXISTS(SELECT 1 FROM local_likes l
                                        WHERE l.SongId = songs.Id AND l.AccountKey = '') THEN COALESCE(BookmarkedAt, $pnow)
                           WHEN IsYouTubeLiked = 1 THEN BookmarkedAt
                           ELSE NULL END;

                DELETE FROM playlist_items
                 WHERE SongId IN (
                    SELECT s.Id FROM songs s
                     WHERE (s.LocalPath IS NULL OR s.LocalPath='')
                       AND (s.DownloadPath IS NULL OR s.DownloadPath='')
                       AND s.IsLiked=0 AND s.IsYouTubeLiked=0 AND s.IsSoundLibertLiked=0
                       AND s.IsLocalUnliked=0
                       AND s.Id NOT IN (SELECT SongId FROM local_likes)
                       AND s.Id NOT IN (SELECT SongId FROM play_events)
                       AND s.Id NOT IN (
                          SELECT pi.SongId FROM playlist_items pi
                            JOIN playlists p ON p.Id = pi.PlaylistId
                           WHERE p.IsEditable = 1)
                 );

                DELETE FROM songs
                 WHERE (LocalPath IS NULL OR LocalPath='')
                   AND (DownloadPath IS NULL OR DownloadPath='')
                   AND IsLiked=0 AND IsYouTubeLiked=0 AND IsSoundLibertLiked=0
                   AND IsLocalUnliked=0
                   AND Id NOT IN (SELECT SongId FROM local_likes)
                   AND Id NOT IN (SELECT SongId FROM play_events)
                   AND Id NOT IN (
                      SELECT pi.SongId FROM playlist_items pi
                        JOIN playlists p ON p.Id = pi.PlaylistId
                       WHERE p.IsEditable = 1);

                -- Recording caches belong to a catalogue row, even when the
                -- played counterpart ID has no row. Keep legacy ID keys too.
                DELETE FROM lyrics
                 WHERE SongId NOT IN (SELECT Id FROM songs)
                   AND NOT EXISTS (
                       SELECT 1 FROM songs
                        WHERE lyrics.SongId GLOB 'lyrics:v1:' || hex(songs.Id) || ':*');
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            System.Diagnostics.Trace.WriteLine("[Database] Cleared account data (playlists, YT likes, orphan catalogue)");
        }, ct);

    public Task ReconcileLikesAsync(IReadOnlyCollection<string> accountLikedIds, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await FillTempIdsAsync(conn, "_account_likes", accountLikedIds, ct);

            // STRATEGY A, with a local veto: the YouTube account is the single
            // source of truth for catalogue likes, EXCEPT when the user
            // explicitly unliked the song in this app (IsLocalUnliked) — then
            // it stays out of the Liked list across restarts and re-logins.
            // The veto is dropped only when YouTube itself no longer likes the
            // song (so a future YouTube like can sync normally again).
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE songs
                   SET IsYouTubeLiked=CASE WHEN Id IN (SELECT Id FROM _account_likes) THEN 1 ELSE 0 END,
                       IsLocalUnliked=CASE WHEN Id IN (SELECT Id FROM _account_likes) THEN IsLocalUnliked ELSE 0 END,
                       IsLiked=CASE
                           WHEN IsLocalUnliked=1 AND Id IN (SELECT Id FROM _account_likes) THEN 0
                           WHEN Id IN (SELECT Id FROM _account_likes) THEN 1
                           WHEN IsSoundLibertLiked=1 THEN 1
                           ELSE 0
                       END,
                       BookmarkedAt=CASE
                           WHEN IsLocalUnliked=1 AND Id IN (SELECT Id FROM _account_likes) THEN NULL
                           WHEN Id IN (SELECT Id FROM _account_likes) THEN COALESCE(BookmarkedAt, $now)
                           WHEN IsSoundLibertLiked=1 THEN BookmarkedAt
                           ELSE NULL
                       END
                 WHERE LocalPath IS NULL OR LocalPath='';
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            var affected = await cmd.ExecuteNonQueryAsync(ct);

            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM songs WHERE IsLiked=1 AND LocalPath IS NULL;";
                var liked = Convert.ToInt32(await check.ExecuteScalarAsync(ct) ?? 0);
                System.Diagnostics.Debug.WriteLine(
                    $"[Database] ReconcileLikes: account={accountLikedIds.Count}, rows={affected}, catalogueLikedNow={liked}");
            }
        }, ct);

    public Task ClearLocalUnlikesAsync(CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE songs SET IsLocalUnliked=0;";
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task ClearVersionPinsAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE songs
                   SET SongVersionId=NULL, VideoVersionId=NULL
                 WHERE Id=$id AND VideoVersionId IS NOT NULL AND VideoVersionId != Id;
                """;
            cmd.Parameters.AddWithValue("$id", songId);
            var cleared = await cmd.ExecuteNonQueryAsync(ct);
            if (cleared > 0)
                System.Diagnostics.Trace.WriteLine($"[Database] Cleared stale version pin for {songId}");
        }, ct);

    /// <summary>
    /// Negative clip cache: records when discovery found no clip (or clears
    /// the record when a clip is pinned). Rows may be absent (search rows are
    /// not always stored) — then the write is a no-op and discovery simply
    /// retries next time, bounded by the provider's in-memory cache.
    /// </summary>
    public Task SetClipMissAtAsync(string songId, DateTime? value, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE songs SET ClipMissAt=$v WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", songId);
            cmd.Parameters.AddWithValue("$v", value.HasValue
                ? value.Value.ToUniversalTime().ToString("O")
                : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    private static readonly TimeSpan ClipMissTtl = TimeSpan.FromDays(7);

    public Task SetDownloadAsync(string songId, string path, DateTime downloadedAt, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE songs SET DownloadPath=$p, DownloadedAt=$at WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", songId);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$at", downloadedAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task ClearDownloadAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE songs SET DownloadPath=NULL, DownloadedAt=NULL WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", songId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlySet<string>> GetDownloadedSongIdsAsync(CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlySet<string>>(async conn =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM songs WHERE DownloadPath IS NOT NULL AND DownloadPath<>'';";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
            return ids;
        }, ct);

    public Task<IReadOnlyList<SongEntity>> GetDownloadedSongsAsync(CancellationToken ct = default)
        => QuerySongsAsync(
            "SELECT * FROM songs WHERE DownloadPath IS NOT NULL AND DownloadPath<>'' ORDER BY DownloadedAt DESC;", ct);

    /// <summary>True when a recorded clip-miss is still fresh (skip discovery).</summary>
    public Task<string?> FindKnownClipForSongAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM songs WHERE SongVersionId=$id AND VideoVersionId=Id AND Id<>$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", songId);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }, ct);

    public Task<bool> HasFreshClipMissAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ClipMissAt FROM songs WHERE Id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", songId);
            var raw = await cmd.ExecuteScalarAsync(ct) as string;
            return !string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && DateTime.UtcNow - at.ToUniversalTime() < ClipMissTtl;
        }, ct);

    public Task<int> PruneOrphanCatalogueAsync(CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // A row is kept when it is: a local file, downloaded offline, liked
            // (either source), has play history, or sits in a user playlist.
            // Everything else is a stale cache entry that may no longer exist
            // on YouTube.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM playlist_items
                 WHERE SongId IN (
                    SELECT s.Id FROM songs s
                     WHERE (s.LocalPath IS NULL OR s.LocalPath='')
                       AND (s.DownloadPath IS NULL OR s.DownloadPath='')
                       AND s.IsLiked=0 AND s.IsYouTubeLiked=0 AND s.IsSoundLibertLiked=0
                       AND s.IsLocalUnliked=0
                       AND s.Id NOT IN (SELECT SongId FROM local_likes)
                       AND s.Id NOT IN (SELECT SongId FROM play_events)
                       AND s.Id NOT IN (
                          SELECT pi.SongId FROM playlist_items pi
                            JOIN playlists p ON p.Id = pi.PlaylistId
                           WHERE p.IsEditable = 1)
                 );

                DELETE FROM songs
                 WHERE (LocalPath IS NULL OR LocalPath='')
                   AND (DownloadPath IS NULL OR DownloadPath='')
                   AND IsLiked=0 AND IsYouTubeLiked=0 AND IsSoundLibertLiked=0
                   AND IsLocalUnliked=0
                   AND Id NOT IN (SELECT SongId FROM local_likes)
                   AND Id NOT IN (SELECT SongId FROM play_events)
                   AND Id NOT IN (
                      SELECT pi.SongId FROM playlist_items pi
                        JOIN playlists p ON p.Id = pi.PlaylistId
                       WHERE p.IsEditable = 1);

                -- Recording caches belong to a catalogue row, even when the
                -- played counterpart ID has no row. Keep legacy ID keys too.
                DELETE FROM lyrics
                 WHERE SongId NOT IN (SELECT Id FROM songs)
                   AND NOT EXISTS (
                       SELECT 1 FROM songs
                        WHERE lyrics.SongId GLOB 'lyrics:v1:' || hex(songs.Id) || ':*');
                """;
            var removed = await cmd.ExecuteNonQueryAsync(ct);
            if (removed > 0)
                System.Diagnostics.Trace.WriteLine($"[Database] Pruned {removed} orphan catalogue rows");
            return removed;
        }, ct);

    public Task<IReadOnlyList<SongEntity>> GetLibrarySongsAsync(CancellationToken ct = default)
        => QuerySongsAsync("SELECT * FROM songs WHERE IsLibrary=1 ORDER BY BookmarkedAt DESC, Title;", ct);

    public Task<IReadOnlyList<SongEntity>> GetRecentlyPlayedAsync(int limit = 50, string? accountKey = null, CancellationToken ct = default)
        => QuerySongsAsync(
            """
            SELECT s.* FROM songs s
             WHERE EXISTS (SELECT 1 FROM play_events e WHERE e.SongId = s.Id AND e.AccountKey = $key)
             ORDER BY (SELECT MAX(e.Timestamp) FROM play_events e WHERE e.SongId = s.Id AND e.AccountKey = $key) DESC
             LIMIT $limit;
            """,
            ct, c =>
            {
                c.Parameters.AddWithValue("$limit", limit);
                c.Parameters.AddWithValue("$key", accountKey ?? "");
            });

    public Task<IReadOnlyList<SongEntity>> GetLocalSongsAsync(string? accountKey = null, CancellationToken ct = default)
        => QuerySongsAsync(
            "SELECT * FROM songs WHERE LocalPath IS NOT NULL AND AccountKey = $key ORDER BY Title;",
            ct, c => c.Parameters.AddWithValue("$key", accountKey ?? ""));

    public Task<bool> ToggleLikeAsync(string songId, string? accountKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                UPDATE songs
                       SET IsLiked=CASE WHEN IsLiked=1 THEN 0 ELSE 1 END,
                           IsSoundLibertLiked=CASE WHEN IsLiked=1 THEN 0 ELSE 1 END,
                           IsLocalUnliked=CASE WHEN IsLiked=1 THEN 1 ELSE 0 END,
                           BookmarkedAt=CASE WHEN IsLiked=1 THEN NULL ELSE $now END
                     WHERE Id=$id;
                """;
                update.Parameters.AddWithValue("$id", songId);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await update.ExecuteNonQueryAsync(ct);
            }

            bool liked;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT IsLiked FROM songs WHERE Id=$id;";
                read.Parameters.AddWithValue("$id", songId);
                liked = Convert.ToInt32(await read.ExecuteScalarAsync(ct) ?? 0) != 0;
            }

            // Mirror into the per-account side table (the columns above stay
            // as the current-scope view; ApplyLikeScopeAsync re-derives them
            // on scope switches).
            await using (var mirror = conn.CreateCommand())
            {
                mirror.Transaction = tx;
                if (liked)
                {
                    mirror.CommandText = """
                        INSERT OR IGNORE INTO local_likes (SongId, AccountKey, BookmarkedAt)
                        VALUES ($id, $key, $now);
                        """;
                }
                else
                {
                    mirror.CommandText = "DELETE FROM local_likes WHERE SongId=$id AND AccountKey=$key;";
                }
                mirror.Parameters.AddWithValue("$id", songId);
                mirror.Parameters.AddWithValue("$key", accountKey ?? "");
                mirror.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await mirror.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return liked;
        }, ct);

    /// <summary>
    /// Records a listen: appends a play event and atomically bumps the song's
    /// PlayCount / TotalPlayTimeMs / LastPlayedAt in one connection.
    /// </summary>
    public Task RecordPlayAsync(string songId, int playedMs, string? playlistId = null, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE songs
                    SET PlayCount = PlayCount + 1,
                        TotalPlayTimeMs = TotalPlayTimeMs + $dur,
                        LastPlayedAt = $now
                    WHERE Id=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", songId);
                cmd.Parameters.AddWithValue("$dur", playedMs);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var evt = conn.CreateCommand())
            {
                evt.CommandText = """
                    INSERT INTO play_events (SongId, Timestamp, PlayDurationMs, PlaylistId, AccountKey)
                    VALUES ($id, $now, $dur, $playlist, $key);
                    """;
                evt.Parameters.AddWithValue("$id", songId);
                evt.Parameters.AddWithValue("$now", now);
                evt.Parameters.AddWithValue("$dur", playedMs);
                evt.Parameters.AddWithValue("$playlist", (object?)playlistId ?? DBNull.Value);
                evt.Parameters.AddWithValue("$key", accountKey ?? "");
                await evt.ExecuteNonQueryAsync(ct);
            }
        }, ct);

    public Task AddPlayEventAsync(PlayEvent playEvent, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO play_events (SongId, Timestamp, PlayDurationMs, PlaylistId)
                VALUES ($id,$ts,$dur,$playlist);
                """;
            cmd.Parameters.AddWithValue("$id", playEvent.SongId);
            cmd.Parameters.AddWithValue("$ts", playEvent.Timestamp.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$dur", playEvent.PlayDurationMs);
            cmd.Parameters.AddWithValue("$playlist", (object?)playEvent.PlaylistId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<PlayHistoryItem>> GetHistoryAsync(int limit = 200, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<PlayHistoryItem>>(async conn =>
        {
            var list = new List<PlayHistoryItem>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.SongId, s.Title, s.ArtistsJson, s.ThumbnailUrl, s.DurationSeconds, e.Timestamp
                FROM play_events e LEFT JOIN songs s ON s.Id = e.SongId
                WHERE e.AccountKey = $key
                ORDER BY e.Timestamp DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new PlayHistoryItem
                {
                    SongId = r.GetString(0),
                    Title = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                    Artist = r.IsDBNull(2) ? null : ParseArtistNames(r.GetString(2)).FirstOrDefault(),
                    ThumbnailUrl = r.IsDBNull(3) ? null : r.GetString(3),
                    DurationSeconds = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    Timestamp = ParseDate(r, "Timestamp") ?? DateTime.UtcNow
                });
            }
            return list;
        }, ct);

    public Task<IReadOnlyList<PlayEvent>> GetPlayHistoryAsync(int limit = 200, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<PlayEvent>>(async conn =>
        {
            var list = new List<PlayEvent>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM play_events ORDER BY Timestamp DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PlayEvent
                {
                    Id = reader.GetInt64(reader.GetOrdinal("Id")),
                    SongId = reader.GetString(reader.GetOrdinal("SongId")),
                    Timestamp = ParseDate(reader, "Timestamp") ?? DateTime.UtcNow,
                    PlayDurationMs = reader.GetInt32(reader.GetOrdinal("PlayDurationMs")),
                    PlaylistId = reader.IsDBNull(reader.GetOrdinal("PlaylistId"))
                        ? null : reader.GetString(reader.GetOrdinal("PlaylistId"))
                });
            }
            return list;
        }, ct);

    public Task<PlayStats> GetPlayStatsAsync(DateTime? since = null, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            var stats = new PlayStats();
            var sinceValue = since?.ToUniversalTime().ToString("O");
            var eventFilter = " WHERE AccountKey = $key" + (since.HasValue ? " AND Timestamp >= $since" : string.Empty);
            var joinedEventFilter = " AND e.AccountKey = $key" + (since.HasValue ? " AND e.Timestamp >= $since" : string.Empty);

            static void BindSince(SqliteCommand command, string? value)
            {
                if (value != null) command.Parameters.AddWithValue("$since", value);
            }
            static void BindScope(SqliteCommand command, string? key)
            {
                command.Parameters.AddWithValue("$key", key ?? "");
            }

            await using (var totals = conn.CreateCommand())
            {
                totals.CommandText = $"""
                    SELECT COUNT(*) AS plays, COALESCE(SUM(PlayDurationMs),0) AS ms
                    FROM play_events{eventFilter};
                    """;
                BindSince(totals, sinceValue); BindScope(totals, accountKey);
                await using var r = await totals.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    stats.TotalPlays = r.GetInt32(0);
                    stats.TotalListeningTime = TimeSpan.FromMilliseconds(r.GetInt64(1));
                }
            }

            long distinctSongs = 0;
            long totalPlays = stats.TotalPlays;
            await using (var distinct = conn.CreateCommand())
            {
                distinct.CommandText = $"SELECT COUNT(DISTINCT SongId) FROM play_events{eventFilter};";
                BindSince(distinct, sinceValue); BindScope(distinct, accountKey);
                var result = await distinct.ExecuteScalarAsync(ct);
                distinctSongs = result == null || result is DBNull ? 0 : Convert.ToInt64(result);
            }
            stats.DiscoveryRate = totalPlays > 0 ? (double)distinctSongs / totalPlays : 0;
            stats.UniqueTracks = (int)distinctSongs;

            await using (var avg = conn.CreateCommand())
            {
                avg.CommandText = $"""
                    SELECT COALESCE(AVG(s.DurationSeconds),0)
                    FROM songs s
                    WHERE s.DurationSeconds > 0
                      AND EXISTS (
                          SELECT 1 FROM play_events e
                          WHERE e.SongId = s.Id{joinedEventFilter}
                      );
                    """;
                BindSince(avg, sinceValue); BindScope(avg, accountKey);
                var result = await avg.ExecuteScalarAsync(ct);
                var seconds = result == null || result is DBNull ? 0 : Convert.ToDouble(result);
                stats.AverageTrackLength = TimeSpan.FromSeconds(seconds);
            }

            // Top artists: aggregate per-song plays then count artist names in C#
            // (artists live in a JSON array column, awkward to group in SQL).
            var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using (var top = conn.CreateCommand())
            {
                top.CommandText = $"""
                    SELECT s.ArtistsJson, COUNT(*) AS c
                    FROM play_events e JOIN songs s ON s.Id = e.SongId
                    WHERE 1=1{joinedEventFilter}
                    GROUP BY s.Id ORDER BY c DESC LIMIT 200;
                    """;
                BindSince(top, sinceValue); BindScope(top, accountKey);
                await using var r = await top.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var json = r.GetString(0);
                    var count = r.GetInt32(1);
                    foreach (var name in ParseArtistNames(json))
                        artistCounts[name] = artistCounts.GetValueOrDefault(name) + count;
                }
            }
            stats.TopArtists = artistCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new TopArtist { Name = kv.Key, PlayCount = kv.Value })
                .ToArray();

            // Top albums: count plays per album title (artists/thumb from songs).
            var albums = new List<TopAlbum>();
            await using (var topAlbums = conn.CreateCommand())
            {
                topAlbums.CommandText = $"""
                    SELECT s.AlbumTitle, s.ArtistsJson, s.ThumbnailUrl, COUNT(*) AS c
                    FROM play_events e JOIN songs s ON s.Id = e.SongId
                    WHERE s.AlbumTitle IS NOT NULL AND s.AlbumTitle <> ''{joinedEventFilter}
                    GROUP BY s.AlbumTitle ORDER BY c DESC LIMIT 10;
                    """;
                BindSince(topAlbums, sinceValue); BindScope(topAlbums, accountKey);
                await using var r = await topAlbums.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    albums.Add(new TopAlbum
                    {
                        Name = r.GetString(0),
                        Artist = ParseArtistNames(r.GetString(1)).FirstOrDefault(),
                        ThumbnailUrl = r.IsDBNull(2) ? null : r.GetString(2),
                        PlayCount = r.GetInt32(3)
                    });
                }
            }
            stats.TopAlbums = albums;

            // Top tracks: most-played songs (Spotify-style "on repeat").
            var tracks = new List<TopTrack>();
            await using (var topTracks = conn.CreateCommand())
            {
                topTracks.CommandText = $"""
                    SELECT s.Id, s.Title, s.ArtistsJson, s.ThumbnailUrl, COUNT(*) AS c
                    FROM play_events e JOIN songs s ON s.Id = e.SongId
                    WHERE 1=1{joinedEventFilter}
                    GROUP BY s.Id ORDER BY c DESC LIMIT 10;
                    """;
                BindSince(topTracks, sinceValue); BindScope(topTracks, accountKey);
                await using var r = await topTracks.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    tracks.Add(new TopTrack
                    {
                        SongId = r.GetString(0),
                        Title = r.GetString(1),
                        Artist = ParseArtistNames(r.GetString(2)).FirstOrDefault(),
                        ThumbnailUrl = r.IsDBNull(3) ? null : r.GetString(3),
                        PlayCount = r.GetInt32(4)
                    });
                }
            }
            stats.TopTracks = tracks;

            return stats;
        }, ct);

    // ------------------------------------------------------- artists / albums / playlists

    public Task<ArtistEntity?> GetArtistAsync(string id, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM artists WHERE Id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return MapArtist(r);
        }, ct);

    private static ArtistEntity MapArtist(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        Name = r.GetString(r.GetOrdinal("Name")),
        ThumbnailUrl = r.GetString(r.GetOrdinal("ThumbnailUrl")),
        ChannelId = GetStringOrNull(r, "ChannelId"),
        LastUpdated = ParseDate(r, "LastUpdated"),
        BookmarkedAt = ParseDate(r, "BookmarkedAt"),
        IsYouTubeSubscribed = GetIntOrZero(r, "IsYouTubeSubscribed") != 0,
        IsLocalUnsubscribed = GetIntOrZero(r, "IsLocalUnsubscribed") != 0
    };

    public Task UpsertArtistAsync(ArtistEntity artist, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO artists (Id, Name, ThumbnailUrl, ChannelId, LastUpdated, BookmarkedAt)
                VALUES ($id,$name,$thumb,$channel,$updated,$bookmarked)
                ON CONFLICT(Id) DO UPDATE SET
                    Name=excluded.Name,
                    ThumbnailUrl=excluded.ThumbnailUrl,
                    ChannelId=COALESCE(excluded.ChannelId, artists.ChannelId),
                    LastUpdated=COALESCE(excluded.LastUpdated, artists.LastUpdated),
                    BookmarkedAt=COALESCE(excluded.BookmarkedAt, artists.BookmarkedAt);
                """;
            cmd.Parameters.AddWithValue("$id", artist.Id);
            cmd.Parameters.AddWithValue("$name", artist.Name);
            cmd.Parameters.AddWithValue("$thumb", artist.ThumbnailUrl);
            cmd.Parameters.AddWithValue("$channel", (object?)artist.ChannelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", DateOrNull(artist.LastUpdated));
            cmd.Parameters.AddWithValue("$bookmarked", DateOrNull(artist.BookmarkedAt));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>Effective subscription for a scope (imported-unvetoed or locally followed).</summary>
    public Task<bool> IsArtistSubscribedAsync(string artistId, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM artists
                 WHERE Id = $id
                   AND ((IsYouTubeSubscribed = 1 AND IsLocalUnsubscribed = 0)
                     OR EXISTS (SELECT 1 FROM local_artists l WHERE l.ArtistId = $id AND l.AccountKey = $key))
                 LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", artistId);
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            var hit = await cmd.ExecuteScalarAsync(ct);
            return hit != null && hit is not DBNull;
        }, ct);

    /// <summary>
    /// Flips the local follow (never writes to YouTube). Returns the new
    /// effective state. Mirrors the album toggle incl. the veto.
    /// </summary>
    public Task<bool> ToggleArtistSubscribeAsync(ArtistEntity artist, string? accountKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            var key = accountKey ?? "";
            var now = DateTime.UtcNow.ToString("O");
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);

            bool subscribed;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT 1 FROM artists
                     WHERE Id = $id
                       AND ((IsYouTubeSubscribed = 1 AND IsLocalUnsubscribed = 0)
                         OR EXISTS (SELECT 1 FROM local_artists l WHERE l.ArtistId = $id AND l.AccountKey = $key))
                     LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$id", artist.Id);
                read.Parameters.AddWithValue("$key", key);
                subscribed = await read.ExecuteScalarAsync(ct) is { } hit and not DBNull;
            }

            if (subscribed)
            {
                await using var un = conn.CreateCommand();
                un.Transaction = tx;
                un.CommandText = """
                    UPDATE artists SET BookmarkedAt = CASE WHEN IsYouTubeSubscribed = 1 THEN BookmarkedAt ELSE NULL END,
                                       IsLocalUnsubscribed = 1
                     WHERE Id = $id;
                    DELETE FROM local_artists WHERE ArtistId = $id AND AccountKey = $key;
                    """;
                un.Parameters.AddWithValue("$id", artist.Id);
                un.Parameters.AddWithValue("$key", key);
                await un.ExecuteNonQueryAsync(ct);
                subscribed = false;
            }
            else
            {
                await using var sub = conn.CreateCommand();
                sub.Transaction = tx;
                sub.CommandText = """
                    INSERT INTO artists (Id, Name, ThumbnailUrl, ChannelId, LastUpdated, BookmarkedAt)
                    VALUES ($id, $name, $thumb, $channel, $now, $now)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        ThumbnailUrl = COALESCE(NULLIF(excluded.ThumbnailUrl, ''), artists.ThumbnailUrl),
                        BookmarkedAt = COALESCE(artists.BookmarkedAt, excluded.BookmarkedAt);
                    INSERT OR IGNORE INTO local_artists (ArtistId, AccountKey, BookmarkedAt)
                    VALUES ($id, $key, $now);
                    """;
                sub.Parameters.AddWithValue("$id", artist.Id);
                sub.Parameters.AddWithValue("$name", artist.Name);
                sub.Parameters.AddWithValue("$thumb", artist.ThumbnailUrl);
                sub.Parameters.AddWithValue("$channel", (object?)artist.ChannelId ?? DBNull.Value);
                sub.Parameters.AddWithValue("$now", now);
                sub.Parameters.AddWithValue("$key", key);
                await sub.ExecuteNonQueryAsync(ct);
                subscribed = true;
            }
            await tx.CommitAsync(ct);
            return subscribed;
        }, ct);

    /// <summary>Imports YouTube-subscribed artists (flags only; local follows untouched).</summary>
    public Task ImportAccountArtistsAsync(IEnumerable<ArtistEntity> artists, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            foreach (var artist in artists)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters.Clear();
                cmd.CommandText = """
                    INSERT INTO artists (Id, Name, ThumbnailUrl, ChannelId, LastUpdated, BookmarkedAt, IsYouTubeSubscribed)
                    VALUES ($id, $name, $thumb, $channel, $now, $now, 1)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        ThumbnailUrl = COALESCE(NULLIF(excluded.ThumbnailUrl, ''), artists.ThumbnailUrl),
                        ChannelId = COALESCE(excluded.ChannelId, artists.ChannelId),
                        IsYouTubeSubscribed = 1,
                        BookmarkedAt = COALESCE(artists.BookmarkedAt, excluded.BookmarkedAt);
                    """;
                cmd.Parameters.AddWithValue("$id", artist.Id);
                cmd.Parameters.AddWithValue("$name", artist.Name);
                cmd.Parameters.AddWithValue("$thumb", artist.ThumbnailUrl);
                cmd.Parameters.AddWithValue("$channel", (object?)artist.ChannelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }, ct);

    public Task<IReadOnlyList<ArtistEntity>> GetLibraryArtistsAsync(string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<ArtistEntity>>(async conn =>
        {
            var list = new List<ArtistEntity>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM artists
                 WHERE (IsYouTubeSubscribed = 1 AND IsLocalUnsubscribed = 0)
                    OR EXISTS (SELECT 1 FROM local_artists l WHERE l.ArtistId = artists.Id AND l.AccountKey = $key)
                 ORDER BY Name;
                """;
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(MapArtist(r));
            return list;
        }, ct);

    public Task<AlbumEntity?> GetAlbumAsync(string id, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM albums WHERE Id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new AlbumEntity
            {
                Id = r.GetString(r.GetOrdinal("Id")),
                Title = r.GetString(r.GetOrdinal("Title")),
                PlaylistId = GetStringOrNull(r, "PlaylistId"),
                ArtistsJson = r.GetString(r.GetOrdinal("ArtistsJson")),
                ThumbnailUrl = r.GetString(r.GetOrdinal("ThumbnailUrl")),
                Year = GetStringOrNull(r, "Year"),
                IsExplicit = r.GetInt32(r.GetOrdinal("IsExplicit")) != 0,
                BookmarkedAt = ParseDate(r, "BookmarkedAt"),
                IsYouTubeLiked = r.GetInt32(r.GetOrdinal("IsYouTubeLiked")) != 0,
                IsLocalUnliked = r.GetInt32(r.GetOrdinal("IsLocalUnliked")) != 0
            };
        }, ct);

    public Task UpsertAlbumAsync(AlbumEntity album, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO albums (Id, Title, PlaylistId, ArtistsJson, ThumbnailUrl, Year, IsExplicit, BookmarkedAt)
                VALUES ($id,$title,$playlist,$artists,$thumb,$year,$explicit,$bookmarked)
                ON CONFLICT(Id) DO UPDATE SET
                    Title=excluded.Title,
                    PlaylistId=COALESCE(excluded.PlaylistId, albums.PlaylistId),
                    ArtistsJson=excluded.ArtistsJson,
                    ThumbnailUrl=excluded.ThumbnailUrl,
                    Year=COALESCE(excluded.Year, albums.Year),
                    IsExplicit=excluded.IsExplicit,
                    BookmarkedAt=COALESCE(excluded.BookmarkedAt, albums.BookmarkedAt);
                """;
            cmd.Parameters.AddWithValue("$id", album.Id);
            cmd.Parameters.AddWithValue("$title", album.Title);
            cmd.Parameters.AddWithValue("$playlist", (object?)album.PlaylistId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artists", album.ArtistsJson);
            cmd.Parameters.AddWithValue("$thumb", album.ThumbnailUrl);
            cmd.Parameters.AddWithValue("$year", (object?)album.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$explicit", album.IsExplicit ? 1 : 0);
            cmd.Parameters.AddWithValue("$bookmarked", DateOrNull(album.BookmarkedAt));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<AlbumEntity>> GetLibraryAlbumsAsync(string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<AlbumEntity>>(async conn =>
        {
            var list = new List<AlbumEntity>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM albums WHERE BookmarkedAt IS NOT NULL
                   AND ((IsYouTubeLiked = 1 AND IsLocalUnliked = 0)
                     OR EXISTS (SELECT 1 FROM local_likes l WHERE l.SongId = albums.Id AND l.AccountKey = $key))
                 ORDER BY Title;
                """;
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(MapAlbum(r));
            }
            return list;
        }, ct);

    private static AlbumEntity MapAlbum(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        Title = r.GetString(r.GetOrdinal("Title")),
        PlaylistId = GetStringOrNull(r, "PlaylistId"),
        ArtistsJson = r.GetString(r.GetOrdinal("ArtistsJson")),
        ThumbnailUrl = r.GetString(r.GetOrdinal("ThumbnailUrl")),
        Year = GetStringOrNull(r, "Year"),
        IsExplicit = r.GetInt32(r.GetOrdinal("IsExplicit")) != 0,
        BookmarkedAt = ParseDate(r, "BookmarkedAt"),
        IsYouTubeLiked = r.GetInt32(r.GetOrdinal("IsYouTubeLiked")) != 0,
        IsLocalUnliked = r.GetInt32(r.GetOrdinal("IsLocalUnliked")) != 0
    };

    /// <summary>Effective album heart for a scope (imported-unvetoed or locally liked).</summary>
    public Task<bool> IsAlbumLikedAsync(string albumId, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM albums
                 WHERE Id = $id
                   AND ((IsYouTubeLiked = 1 AND IsLocalUnliked = 0)
                     OR EXISTS (SELECT 1 FROM local_likes l WHERE l.SongId = $id AND l.AccountKey = $key))
                 LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", albumId);
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            var hit = await cmd.ExecuteScalarAsync(ct);
            return hit != null && hit is not DBNull;
        }, ct);

    /// <summary>
    /// Flips the local album heart (never writes to YouTube). Returns the new
    /// effective state. Mirrors the song toggle incl. the veto.
    /// </summary>
    public Task<bool> ToggleAlbumLikeAsync(AlbumEntity album, string? accountKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            var key = accountKey ?? "";
            var now = DateTime.UtcNow.ToString("O");
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);

            bool liked;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT 1 FROM albums
                     WHERE Id = $id
                       AND ((IsYouTubeLiked = 1 AND IsLocalUnliked = 0)
                         OR EXISTS (SELECT 1 FROM local_likes l WHERE l.SongId = $id AND l.AccountKey = $key))
                     LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$id", album.Id);
                read.Parameters.AddWithValue("$key", key);
                liked = await read.ExecuteScalarAsync(ct) is { } hit and not DBNull;
            }

            if (liked)
            {
                await using var un = conn.CreateCommand();
                un.Transaction = tx;
                un.CommandText = """
                    UPDATE albums SET BookmarkedAt = CASE WHEN IsYouTubeLiked = 1 THEN BookmarkedAt ELSE NULL END,
                                      IsLocalUnliked = 1
                     WHERE Id = $id;
                    DELETE FROM local_likes WHERE SongId = $id AND AccountKey = $key;
                    """;
                un.Parameters.AddWithValue("$id", album.Id);
                un.Parameters.AddWithValue("$key", key);
                await un.ExecuteNonQueryAsync(ct);
                liked = false;
            }
            else
            {
                await using var like = conn.CreateCommand();
                like.Transaction = tx;
                like.CommandText = """
                    INSERT INTO albums (Id, Title, PlaylistId, ArtistsJson, ThumbnailUrl, Year, IsExplicit, BookmarkedAt)
                    VALUES ($id, $title, $playlist, $artists, $thumb, $year, $explicit, $now)
                    ON CONFLICT(Id) DO UPDATE SET
                        Title = excluded.Title,
                        ArtistsJson = excluded.ArtistsJson,
                        ThumbnailUrl = COALESCE(NULLIF(excluded.ThumbnailUrl, ''), albums.ThumbnailUrl),
                        Year = COALESCE(excluded.Year, albums.Year),
                        BookmarkedAt = COALESCE(albums.BookmarkedAt, excluded.BookmarkedAt);
                    INSERT OR IGNORE INTO local_likes (SongId, AccountKey, BookmarkedAt)
                    VALUES ($id, $key, $now);
                    """;
                like.Parameters.AddWithValue("$id", album.Id);
                like.Parameters.AddWithValue("$title", album.Title);
                like.Parameters.AddWithValue("$playlist", (object?)album.PlaylistId ?? DBNull.Value);
                like.Parameters.AddWithValue("$artists", album.ArtistsJson);
                like.Parameters.AddWithValue("$thumb", album.ThumbnailUrl);
                like.Parameters.AddWithValue("$year", (object?)album.Year ?? DBNull.Value);
                like.Parameters.AddWithValue("$explicit", album.IsExplicit ? 1 : 0);
                like.Parameters.AddWithValue("$now", now);
                like.Parameters.AddWithValue("$key", key);
                await like.ExecuteNonQueryAsync(ct);
                liked = true;
            }
            await tx.CommitAsync(ct);
            return liked;
        }, ct);

    /// <summary>Imports YouTube-saved albums (flags only; local hearts untouched).</summary>
    public Task ImportAccountAlbumsAsync(IEnumerable<AlbumEntity> albums, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters.Clear();
                cmd.CommandText = """
                    INSERT INTO albums (Id, Title, PlaylistId, ArtistsJson, ThumbnailUrl, Year, IsExplicit, BookmarkedAt, IsYouTubeLiked)
                    VALUES ($id, $title, $playlist, $artists, $thumb, $year, $explicit, $now, 1)
                    ON CONFLICT(Id) DO UPDATE SET
                        Title = excluded.Title,
                        ArtistsJson = excluded.ArtistsJson,
                        ThumbnailUrl = COALESCE(NULLIF(excluded.ThumbnailUrl, ''), albums.ThumbnailUrl),
                        Year = COALESCE(excluded.Year, albums.Year),
                        IsYouTubeLiked = 1,
                        BookmarkedAt = COALESCE(albums.BookmarkedAt, excluded.BookmarkedAt);
                    """;
                cmd.Parameters.AddWithValue("$id", album.Id);
                cmd.Parameters.AddWithValue("$title", album.Title);
                cmd.Parameters.AddWithValue("$playlist", (object?)album.PlaylistId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$artists", album.ArtistsJson);
                cmd.Parameters.AddWithValue("$thumb", album.ThumbnailUrl);
                cmd.Parameters.AddWithValue("$year", (object?)album.Year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$explicit", album.IsExplicit ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }, ct);

    /// <summary>
    /// Reconciles imported album flags with the account list (authoritative).
    /// Vetoes survive unless YouTube dropped the album; local hearts untouched.
    /// </summary>
    public Task ReconcileAlbumLikesAsync(IReadOnlyCollection<string> accountAlbumIds, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await FillTempIdsAsync(conn, "_account_albums", accountAlbumIds, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE albums SET IsYouTubeLiked = 0, IsLocalUnliked = 0
                 WHERE IsYouTubeLiked = 1 AND Id NOT IN (SELECT Id FROM _account_albums);
                UPDATE albums SET IsYouTubeLiked = 1
                 WHERE Id IN (SELECT Id FROM _account_albums);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>
    /// Reconciles imported artist subscriptions with the account list
    /// (authoritative). Vetoes survive unless YouTube dropped the artist;
    /// local follows are never touched.
    /// </summary>
    public Task ReconcileArtistsAsync(IReadOnlyCollection<string> accountArtistIds, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await FillTempIdsAsync(conn, "_account_artists", accountArtistIds, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE artists SET IsYouTubeSubscribed = 0, IsLocalUnsubscribed = 0
                 WHERE IsYouTubeSubscribed = 1 AND Id NOT IN (SELECT Id FROM _account_artists);
                UPDATE artists SET IsYouTubeSubscribed = 1
                 WHERE Id IN (SELECT Id FROM _account_artists);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<PlaylistEntity?> GetPlaylistAsync(string id, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM playlists WHERE Id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new PlaylistEntity
            {
                Id = r.GetString(r.GetOrdinal("Id")),
                Title = r.GetString(r.GetOrdinal("Title")),
                BrowseId = GetStringOrNull(r, "BrowseId"),
                ThumbnailUrl = GetStringOrNull(r, "ThumbnailUrl"),
                IsEditable = r.GetInt32(r.GetOrdinal("IsEditable")) != 0,
                BookmarkedAt = ParseDate(r, "BookmarkedAt"),
                LastUpdated = ParseDate(r, "LastUpdated"),
                AccountKey = GetStringOrNull(r, "AccountKey"),
                IsLocalSaved = GetIntOrZero(r, "IsLocalSaved") != 0
            };
        }, ct);

    /// <summary>True when a playlist is saved locally or imported from the account.</summary>
    public Task<bool> IsPlaylistSavedAsync(string playlistId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM playlists WHERE Id = $id AND BookmarkedAt IS NOT NULL LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", playlistId);
            var hit = await cmd.ExecuteScalarAsync(ct);
            return hit != null && hit is not DBNull;
        }, ct);

    /// <summary>
    /// Saves/removes a community playlist in the local library (never writes to
    /// YouTube). Returns the new state.
    /// </summary>
    public Task<bool> TogglePlaylistSaveAsync(PlaylistEntity playlist, string? accountKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            var key = accountKey ?? "";
            var now = DateTime.UtcNow.ToString("O");
            bool saved;
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT 1 FROM playlists WHERE Id = $id AND BookmarkedAt IS NOT NULL LIMIT 1;";
                read.Parameters.AddWithValue("$id", playlist.Id);
                saved = await read.ExecuteScalarAsync(ct) is { } hit and not DBNull;
            }

            await using var cmd = conn.CreateCommand();
            if (saved)
            {
                // Only locally-saved playlists can be un-saved; imported account
                // playlists are read-only (their bookmark belongs to the account).
                cmd.CommandText = "UPDATE playlists SET BookmarkedAt = NULL, IsLocalSaved = 0 WHERE Id = $id AND IsLocalSaved = 1;";
                cmd.Parameters.AddWithValue("$id", playlist.Id);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                return rows == 0;
            }

            cmd.CommandText = """
                INSERT INTO playlists (Id, Title, BrowseId, ThumbnailUrl, IsEditable, BookmarkedAt, LastUpdated, AccountKey, IsLocalSaved)
                VALUES ($id, $title, $browse, $thumb, 0, $now, $now, $key, 1)
                ON CONFLICT(Id) DO UPDATE SET
                    Title = excluded.Title,
                    BrowseId = COALESCE(excluded.BrowseId, playlists.BrowseId),
                    ThumbnailUrl = COALESCE(excluded.ThumbnailUrl, playlists.ThumbnailUrl),
                    BookmarkedAt = $now,
                    IsLocalSaved = 1;
                """;
            cmd.Parameters.AddWithValue("$id", playlist.Id);
            cmd.Parameters.AddWithValue("$title", playlist.Title);
            cmd.Parameters.AddWithValue("$browse", (object?)playlist.BrowseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb", (object?)playlist.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task AdoptLocalDataAsync(string accountKey, string? handleKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // One-time adoption per sign-in: unscoped local rows (created before
            // scoping, or while signed out) belong to whoever is signed in now.
            // Idempotent — only device-local ('') rows move. Pre-deletes resolve
            // PK collisions where the same song/query was touched in both scopes
            // (liking/seaching signed out, then again signed in).
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                DELETE FROM local_likes WHERE AccountKey='' AND SongId IN (SELECT SongId FROM local_likes WHERE AccountKey=$key);
                DELETE FROM local_artists WHERE AccountKey='' AND ArtistId IN (SELECT ArtistId FROM local_artists WHERE AccountKey=$key);
                DELETE FROM search_history WHERE AccountKey='' AND Query IN (SELECT Query FROM search_history WHERE AccountKey=$key);
                UPDATE playlists SET AccountKey=$key WHERE Id LIKE '{PlaylistEntity.LocalIdPrefix}%' AND (AccountKey IS NULL OR AccountKey='');
                UPDATE play_events SET AccountKey=$key WHERE AccountKey IS NULL OR AccountKey='';
                UPDATE search_history SET AccountKey=$key WHERE AccountKey IS NULL OR AccountKey='';
                UPDATE songs SET AccountKey=$key WHERE LocalPath IS NOT NULL AND LocalPath<>'' AND (AccountKey IS NULL OR AccountKey='');
                UPDATE local_likes SET AccountKey=$key WHERE AccountKey='';
                UPDATE local_artists SET AccountKey=$key WHERE AccountKey='';
                """;
            cmd.Parameters.AddWithValue("$key", accountKey);
            try
            {
                var adopted = await cmd.ExecuteNonQueryAsync(ct);
                if (adopted > 0)
                    System.Diagnostics.Trace.WriteLine($"[Database] Adopted {adopted} local row(s) into account scope");
            }
            catch (Exception ex)
            {
                // Adoption races a reload on tables it touches — retry next sync.
                System.Diagnostics.Trace.WriteLine($"[Database] Adopt failed (retry later): {ex.Message}");
            }

            // Merge the session handle scope into the email scope (same human:
            // both identifiers come from one live account_menu response).
            if (!string.IsNullOrWhiteSpace(handleKey)
                && !string.Equals(handleKey, accountKey, StringComparison.Ordinal))
            {
                await using var merge = conn.CreateCommand();
                merge.CommandText = $"""
                    DELETE FROM local_likes WHERE AccountKey=$h AND SongId IN (SELECT SongId FROM local_likes WHERE AccountKey=$key);
                    DELETE FROM local_artists WHERE AccountKey=$h AND ArtistId IN (SELECT ArtistId FROM local_artists WHERE AccountKey=$key);
                    DELETE FROM search_history WHERE AccountKey=$h AND Query IN (SELECT Query FROM search_history WHERE AccountKey=$key);
                    UPDATE playlists SET AccountKey=$key WHERE Id LIKE '{PlaylistEntity.LocalIdPrefix}%' AND AccountKey=$h;
                    UPDATE play_events SET AccountKey=$key WHERE AccountKey=$h;
                    UPDATE search_history SET AccountKey=$key WHERE AccountKey=$h;
                    UPDATE songs SET AccountKey=$key WHERE LocalPath IS NOT NULL AND LocalPath<>'' AND AccountKey=$h;
                    UPDATE local_likes SET AccountKey=$key WHERE AccountKey=$h;
                    UPDATE local_artists SET AccountKey=$key WHERE AccountKey=$h;
                    """;
                merge.Parameters.AddWithValue("$key", accountKey);
                merge.Parameters.AddWithValue("$h", handleKey);
                try
                {
                    var merged = await merge.ExecuteNonQueryAsync(ct);
                    if (merged > 0)
                        System.Diagnostics.Trace.WriteLine($"[Database] Merged handle scope into account scope ({merged} rows)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[Database] Scope merge failed (retry later): {ex.Message}");
                }
            }
        }, ct);

    public Task ApplyLikeScopeAsync(string? accountKey, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // Recomputes the materialized like columns for one scope from the
            // per-account local_likes table (YouTube flags untouched): every
            // existing like query keeps working unchanged across sign-in/out.
            // Vetoes survive (only reconcile clears them by design).
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE songs
                   SET IsSoundLibertLiked = EXISTS(
                           SELECT 1 FROM local_likes l
                            WHERE l.SongId = songs.Id AND l.AccountKey = $key),
                       IsLiked = CASE
                           WHEN IsLocalUnliked = 1 AND IsYouTubeLiked = 1 THEN 0
                           WHEN IsYouTubeLiked = 1 THEN 1
                           WHEN EXISTS(SELECT 1 FROM local_likes l
                                        WHERE l.SongId = songs.Id AND l.AccountKey = $key) THEN 1
                           ELSE 0 END,
                       BookmarkedAt = CASE
                           WHEN EXISTS(SELECT 1 FROM local_likes l
                                        WHERE l.SongId = songs.Id AND l.AccountKey = $key)
                               THEN COALESCE((SELECT l.BookmarkedAt FROM local_likes l
                                               WHERE l.SongId = songs.Id AND l.AccountKey = $key),
                                             BookmarkedAt, $now)
                           WHEN IsYouTubeLiked = 1 THEN BookmarkedAt
                           ELSE NULL END;
                """;
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task UpsertPlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO playlists (Id, Title, BrowseId, ThumbnailUrl, IsEditable, BookmarkedAt, LastUpdated)
                VALUES ($id,$title,$browse,$thumb,$editable,$bookmarked,$updated)
                ON CONFLICT(Id) DO UPDATE SET
                    Title=excluded.Title,
                    BrowseId=COALESCE(excluded.BrowseId, playlists.BrowseId),
                    ThumbnailUrl=COALESCE(excluded.ThumbnailUrl, playlists.ThumbnailUrl),
                    IsEditable=excluded.IsEditable,
                    BookmarkedAt=COALESCE(excluded.BookmarkedAt, playlists.BookmarkedAt),
                    LastUpdated=COALESCE(excluded.LastUpdated, playlists.LastUpdated);
                """;
            cmd.Parameters.AddWithValue("$id", playlist.Id);
            cmd.Parameters.AddWithValue("$title", playlist.Title);
            cmd.Parameters.AddWithValue("$browse", (object?)playlist.BrowseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb", (object?)playlist.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$editable", playlist.IsEditable ? 1 : 0);
            cmd.Parameters.AddWithValue("$bookmarked", DateOrNull(playlist.BookmarkedAt));
            cmd.Parameters.AddWithValue("$updated", DateOrNull(playlist.LastUpdated));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetLocalPlaylistCoversAsync(
        int perPlaylist = 4, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyDictionary<string, IReadOnlyList<string>>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT pi.PlaylistId, s.ThumbnailUrl
                  FROM playlist_items pi
                  JOIN songs s ON s.Id = pi.SongId
                  JOIN playlists p ON p.Id = pi.PlaylistId
                 WHERE p.IsEditable = 1 AND s.ThumbnailUrl IS NOT NULL AND s.ThumbnailUrl <> ''
                 ORDER BY pi.PlaylistId, pi.Position;
                """;
            var sets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var playlistId = r.GetString(0);
                var url = r.GetString(1);
                if (!sets.TryGetValue(playlistId, out var list))
                {
                    sets[playlistId] = list = new List<string>();
                    seen[playlistId] = new HashSet<string>(StringComparer.Ordinal);
                }
                if (list.Count >= perPlaylist) continue;
                // Same art at another size ("=w120-h120" vs "=w544-h544") is one cover.
                var key = url.Split('=')[0];
                if (seen[playlistId].Add(key)) list.Add(url);
            }
            return sets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
        }, ct);

    public Task<IReadOnlyList<PlaylistEntity>> GetLibraryPlaylistsAsync(CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<PlaylistEntity>>(async conn =>
        {
            var list = new List<PlaylistEntity>();
            await using var cmd = conn.CreateCommand();
            // Track count via a correlated subquery; a local playlist with no
            // stored cover borrows its first track's artwork.
            cmd.CommandText = """
                SELECT p.*,
                       (SELECT COUNT(*) FROM playlist_items pi WHERE pi.PlaylistId = p.Id) AS TrackCount,
                       (SELECT COUNT(*) FROM playlist_items pi
                          JOIN songs s ON s.Id = pi.SongId
                         WHERE pi.PlaylistId = p.Id
                           AND (s.VideoType LIKE 'MUSIC_VIDEO_TYPE_OMV%'
                             OR s.VideoType LIKE 'MUSIC_VIDEO_TYPE_UGC%'
                             OR (s.VideoVersionId IS NOT NULL AND s.VideoVersionId <> ''
                                 AND s.VideoVersionId <> COALESCE(NULLIF(s.SongVersionId, ''), s.Id)))) AS VideoCount,
                       (SELECT s.ThumbnailUrl FROM playlist_items pi
                          JOIN songs s ON s.Id = pi.SongId
                         WHERE pi.PlaylistId = p.Id AND s.ThumbnailUrl <> ''
                         ORDER BY pi.Position LIMIT 1) AS CoverUrl
                FROM playlists p
                WHERE p.BookmarkedAt IS NOT NULL
                  -- The account's auto "Liked Music" playlist is represented by the
                  -- built-in Liked Songs tile, so it is never listed separately.
                  AND p.Id NOT IN ('LM', 'VLLM')
                  AND LOWER(p.Title) NOT IN ('liked music', 'liked songs', 'улюблені пісні')
                ORDER BY p.SortOrder, p.Title;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var stored = GetStringOrNull(r, "ThumbnailUrl");
                list.Add(new PlaylistEntity
                {
                    Id = r.GetString(r.GetOrdinal("Id")),
                    Title = r.GetString(r.GetOrdinal("Title")),
                    BrowseId = GetStringOrNull(r, "BrowseId"),
                    ThumbnailUrl = string.IsNullOrWhiteSpace(stored) ? GetStringOrNull(r, "CoverUrl") : stored,
                    IsEditable = r.GetInt32(r.GetOrdinal("IsEditable")) != 0,
                    BookmarkedAt = ParseDate(r, "BookmarkedAt"),
                    LastUpdated = ParseDate(r, "LastUpdated"),
                    SortOrder = GetIntOrZero(r, "SortOrder"),
                    TrackCount = GetIntOrZero(r, "TrackCount"),
                    VideoCount = GetIntOrZero(r, "VideoCount"),
                    AccountKey = GetStringOrNull(r, "AccountKey"),
                    IsLocalSaved = GetIntOrZero(r, "IsLocalSaved") != 0
                });
            }
            return list;
        }, ct);

    /// <summary>Persists a new manual order for the given playlist ids.</summary>
    public Task ReorderPlaylistsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < orderedIds.Count; i++)
            {
                sb.Append("UPDATE playlists SET SortOrder=").Append(i).Append(" WHERE Id=$p").Append(i).Append(';');
                cmd.Parameters.AddWithValue("$p" + i, orderedIds[i]);
            }
            if (sb.Length == 0) return;
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>Copies an editable playlist (with all its tracks) under a new name.</summary>
    public async Task<PlaylistEntity> DuplicatePlaylistAsync(string playlistId, string newTitle, CancellationToken ct = default)
    {
        // The copy inherits the source's account scope.
        var source = await GetPlaylistAsync(playlistId, ct);
        var copy = await CreatePlaylistAsync(newTitle, source?.AccountKey, ct);

        await WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO playlist_items (PlaylistId, SongId, Position, AddedAt)
                SELECT $new, SongId, Position, $now
                FROM playlist_items WHERE PlaylistId = $old
                ORDER BY Position, AddedAt;
                """;
            cmd.Parameters.AddWithValue("$new", copy.Id);
            cmd.Parameters.AddWithValue("$old", playlistId);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        return copy;
    }

    public Task ReconcileAccountPlaylistsAsync(IReadOnlyCollection<string> accountPlaylistIds, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // An EMPTY account list is authoritative too: the user has no account
            // playlists, so every imported one must go. The old early-return left
            // stale YouTube playlists forever.
            await FillTempIdsAsync(conn, "_account_playlists", accountPlaylistIds, ct);

            // Account-origin playlists are every non-local id (local ones use the
            // `sl:` prefix). Keying on the id, not IsEditable, also cleans rows
            // that an older build stored with a wrong editable flag.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                DELETE FROM playlist_items
                 WHERE PlaylistId NOT LIKE '{PlaylistEntity.LocalIdPrefix}%'
                   AND PlaylistId NOT IN (SELECT Id FROM _account_playlists);
                DELETE FROM playlists
                 WHERE Id NOT LIKE '{PlaylistEntity.LocalIdPrefix}%'
                   AND Id NOT IN (SELECT Id FROM _account_playlists)
                   AND IsLocalSaved = 0;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    // ------------------------------------------------------- user playlists

    public Task<PlaylistEntity> CreatePlaylistAsync(string title, CancellationToken ct = default)
        => CreatePlaylistAsync(title, null, ct);

    public Task<PlaylistEntity> CreatePlaylistAsync(string title, string? accountKey, CancellationToken ct = default)
    {
        var entity = new PlaylistEntity
        {
            Id = PlaylistEntity.LocalIdPrefix + Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "New playlist" : title.Trim(),
            IsEditable = true,
            BookmarkedAt = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow,
            AccountKey = accountKey
        };

        return WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO playlists (Id, Title, BrowseId, ThumbnailUrl, IsEditable, BookmarkedAt, LastUpdated, AccountKey)
                VALUES ($id, $title, NULL, NULL, 1, $now, $now, $key);
                """;
            cmd.Parameters.AddWithValue("$id", entity.Id);
            cmd.Parameters.AddWithValue("$title", entity.Title);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$key", (object?)accountKey ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            return entity;
        }, ct);
    }

    public Task RenamePlaylistAsync(string playlistId, string newTitle, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            // Only user-owned playlists can be renamed.
            cmd.CommandText = "UPDATE playlists SET Title=$title, LastUpdated=$now WHERE Id=$id AND IsEditable=1;";
            cmd.Parameters.AddWithValue("$id", playlistId);
            cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(newTitle) ? "New playlist" : newTitle.Trim());
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using (var items = conn.CreateCommand())
            {
                items.CommandText = "DELETE FROM playlist_items WHERE PlaylistId=$id;";
                items.Parameters.AddWithValue("$id", playlistId);
                await items.ExecuteNonQueryAsync(ct);
            }

            await using (var playlist = conn.CreateCommand())
            {
                playlist.CommandText = "DELETE FROM playlists WHERE Id=$id;";
                playlist.Parameters.AddWithValue("$id", playlistId);
                await playlist.ExecuteNonQueryAsync(ct);
            }
        }, ct);

    public async Task AddSongToPlaylistAsync(string playlistId, SongEntity song, CancellationToken ct = default)
    {
        // playlist_items references songs.Id, so the row must exist first.
        await UpsertSongAsync(song, ct);

        await WithConnectionAsync(async conn =>
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO playlist_items (PlaylistId, SongId, Position, AddedAt)
                    VALUES ($pid, $sid,
                        (SELECT COALESCE(MAX(Position), -1) + 1 FROM playlist_items WHERE PlaylistId=$pid),
                        $now)
                    ON CONFLICT(PlaylistId, SongId) DO NOTHING;
                    """;
                cmd.Parameters.AddWithValue("$pid", playlistId);
                cmd.Parameters.AddWithValue("$sid", song.Id);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var touch = conn.CreateCommand())
            {
                touch.CommandText = "UPDATE playlists SET LastUpdated=$now WHERE Id=$id;";
                touch.Parameters.AddWithValue("$id", playlistId);
                touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await touch.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    public Task RemoveSongFromPlaylistAsync(string playlistId, string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM playlist_items WHERE PlaylistId=$pid AND SongId=$sid;";
            cmd.Parameters.AddWithValue("$pid", playlistId);
            cmd.Parameters.AddWithValue("$sid", songId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task MovePlaylistSongAsync(string playlistId, string songId, int newPosition, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            // Read the ordered ids, reorder in memory, then rewrite the positions.
            var ids = new List<string>();
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT SongId FROM playlist_items WHERE PlaylistId=$pid ORDER BY Position, AddedAt;";
                read.Parameters.AddWithValue("$pid", playlistId);
                await using var r = await read.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
            }

            var from = ids.IndexOf(songId);
            if (from < 0) return;
            newPosition = Math.Clamp(newPosition, 0, ids.Count - 1);
            if (newPosition == from) return;

            ids.RemoveAt(from);
            ids.Insert(newPosition, songId);

            await using var update = conn.CreateCommand();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                sb.Append("UPDATE playlist_items SET Position=").Append(i)
                  .Append(" WHERE PlaylistId=$pid AND SongId=$s").Append(i).Append(';');
                update.Parameters.AddWithValue("$s" + i, ids[i]);
            }
            update.Parameters.AddWithValue("$pid", playlistId);
            update.CommandText = sb.ToString();
            // One transaction: a crash between statements must not leave two
            // tracks on one position, and it is far faster than one commit each.
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            update.Transaction = tx;
            await update.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }, ct);

    public Task<IReadOnlyList<SongEntity>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default)
        => QuerySongsAsync(
            """
            SELECT s.* FROM songs s
            JOIN playlist_items pi ON pi.SongId = s.Id
            WHERE pi.PlaylistId = $pid
            ORDER BY pi.Position, pi.AddedAt;
            """,
            ct, c => c.Parameters.AddWithValue("$pid", playlistId));

    // ------------------------------------------------------- search history / lyrics

    public Task AddSearchHistoryAsync(string query, string? accountKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Task.CompletedTask;
        return WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO search_history (Query, Timestamp, AccountKey) VALUES ($q, $now, $key)
                ON CONFLICT(Query, AccountKey) DO UPDATE SET Timestamp=excluded.Timestamp;
                """;
            cmd.Parameters.AddWithValue("$q", query.Trim());
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<IReadOnlyList<string>> GetSearchHistoryAsync(int limit = 20, string? accountKey = null, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyList<string>>(async conn =>
        {
            var list = new List<string>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Query FROM search_history WHERE AccountKey = $key ORDER BY Timestamp DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$key", accountKey ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
            return list;
        }, ct);

    public Task UpsertLyricsAsync(string songId, string lyrics, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lyrics (SongId, Content, UpdatedAt) VALUES ($id,$content,$now)
                ON CONFLICT(SongId) DO UPDATE SET Content=excluded.Content, UpdatedAt=excluded.UpdatedAt;
                """;
            cmd.Parameters.AddWithValue("$id", songId);
            cmd.Parameters.AddWithValue("$content", lyrics);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<string?> GetLyricsAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Content FROM lyrics WHERE SongId=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", songId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }, ct);

    public Task ClearLyricsCacheAsync(CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM lyrics;";
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    // Returns null when the artist was never looked up; "" when looked up and
    // found to have no genre (so we don't re-query it forever).
    public Task<string?> GetArtistGenreAsync(string artist, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Genre FROM artist_genres WHERE Artist=$a LIMIT 1;";
            cmd.Parameters.AddWithValue("$a", artist);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull or null ? null : (string)result;
        }, ct);

    public Task SetArtistGenreAsync(string artist, string? genre, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO artist_genres (Artist, Genre, UpdatedAt)
                VALUES ($a, $g, $now)
                ON CONFLICT(Artist) DO UPDATE SET Genre=excluded.Genre, UpdatedAt=excluded.UpdatedAt;
                """;
            cmd.Parameters.AddWithValue("$a", artist);
            cmd.Parameters.AddWithValue("$g", genre ?? string.Empty);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    // Returns null when the song was never looked up; "" when looked up and
    // found to have no genre (so we don't re-query it forever).
    public Task<string?> GetSongGenreAsync(string songId, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Genre FROM song_genres WHERE SongId=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", songId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull or null ? null : (string)result;
        }, ct);

    public Task SetSongGenreAsync(string songId, string? genre, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO song_genres (SongId, Genre, UpdatedAt)
                VALUES ($id, $g, $now)
                ON CONFLICT(SongId) DO UPDATE SET Genre=excluded.Genre, UpdatedAt=excluded.UpdatedAt;
                """;
            cmd.Parameters.AddWithValue("$id", songId);
            cmd.Parameters.AddWithValue("$g", genre ?? string.Empty);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyDictionary<string, long>> GetViewCountsAsync(IEnumerable<string> songIds, CancellationToken ct = default)
        => WithConnectionAsync<IReadOnlyDictionary<string, long>>(async conn =>
        {
            var dict = new Dictionary<string, long>(StringComparer.Ordinal);
            var ids = songIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var chunk in ids.Chunk(400))
            {
                await using var cmd = conn.CreateCommand();
                var names = new List<string>(chunk.Length);
                for (var i = 0; i < chunk.Length; i++)
                {
                    var name = "$id" + i;
                    names.Add(name);
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }
                cmd.CommandText = $"SELECT Id, ViewCount FROM songs WHERE Id IN ({string.Join(",", names)});";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetString(0);
                    var views = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    if (views > 0) dict[id] = views;
                }
            }
            return dict;
        }, ct);

    public Task SetViewCountAsync(string songId, long viewCount, DateTime resolvedAt, CancellationToken ct = default)
        => WithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE songs SET ViewCount=MAX(ViewCount,$v), ViewCountAt=$at WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", songId);
            cmd.Parameters.AddWithValue("$v", viewCount);
            cmd.Parameters.AddWithValue("$at", resolvedAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    // ------------------------------------------------------------- helpers

    private Task<IReadOnlyList<SongEntity>> QuerySongsAsync(
        string sql, CancellationToken ct, Action<SqliteCommand>? bind = null)
        => WithConnectionAsync<IReadOnlyList<SongEntity>>(async conn =>
        {
            var list = new List<SongEntity>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(MapSong(r));
            return list;
        }, ct);

    private static SongEntity MapSong(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        Title = r.GetString(r.GetOrdinal("Title")),
        ArtistsJson = r.GetString(r.GetOrdinal("ArtistsJson")),
        AlbumTitle = GetStringOrNull(r, "AlbumTitle"),
        AlbumId = GetStringOrNull(r, "AlbumId"),
        DurationSeconds = r.GetInt32(r.GetOrdinal("DurationSeconds")),
        ThumbnailUrl = r.GetString(r.GetOrdinal("ThumbnailUrl")),
        IsExplicit = r.GetInt32(r.GetOrdinal("IsExplicit")) != 0,
        IsLiked = r.GetInt32(r.GetOrdinal("IsLiked")) != 0,
        IsYouTubeLiked = r.GetInt32(r.GetOrdinal("IsYouTubeLiked")) != 0,
        IsSoundLibertLiked = r.GetInt32(r.GetOrdinal("IsSoundLibertLiked")) != 0,
        IsLocalUnliked = r.GetInt32(r.GetOrdinal("IsLocalUnliked")) != 0,
        IsLibrary = r.GetInt32(r.GetOrdinal("IsLibrary")) != 0,
        TotalPlayTimeMs = r.GetInt64(r.GetOrdinal("TotalPlayTimeMs")),
        PlayCount = r.GetInt32(r.GetOrdinal("PlayCount")),
        LastPlayedAt = ParseDate(r, "LastPlayedAt"),
        BookmarkedAt = ParseDate(r, "BookmarkedAt"),
        CreatedAt = ParseDate(r, "CreatedAt") ?? DateTime.UtcNow,
        LocalPath = GetStringOrNull(r, "LocalPath"),
        VideoType = GetStringOrNull(r, "VideoType"),
        SongVersionId = GetStringOrNull(r, "SongVersionId"),
        VideoVersionId = GetStringOrNull(r, "VideoVersionId"),
        ClipMissAt = ParseDate(r, "ClipMissAt"),
        DownloadPath = GetStringOrNull(r, "DownloadPath"),
        DownloadedAt = ParseDate(r, "DownloadedAt"),
        ViewCount = GetLongOrZero(r, "ViewCount"),
        ViewCountAt = ParseDate(r, "ViewCountAt"),
        AccountKey = GetStringOrNull(r, "AccountKey")
    };

    private static void BindSong(SqliteCommand cmd, SongEntity song)
    {
        cmd.Parameters.AddWithValue("$id", song.Id);
        cmd.Parameters.AddWithValue("$title", song.Title);
        cmd.Parameters.AddWithValue("$artists", song.ArtistsJson);
        cmd.Parameters.AddWithValue("$albumTitle", (object?)song.AlbumTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$albumId", (object?)song.AlbumId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", song.DurationSeconds);
        cmd.Parameters.AddWithValue("$thumb", song.ThumbnailUrl);
        cmd.Parameters.AddWithValue("$explicit", song.IsExplicit ? 1 : 0);
        cmd.Parameters.AddWithValue("$liked", song.IsLiked ? 1 : 0);
        cmd.Parameters.AddWithValue("$youtubeLiked", song.IsYouTubeLiked ? 1 : 0);
        cmd.Parameters.AddWithValue("$soundLibertLiked", song.IsSoundLibertLiked ? 1 : 0);
        cmd.Parameters.AddWithValue("$library", song.IsLibrary ? 1 : 0);
        cmd.Parameters.AddWithValue("$totalPlay", song.TotalPlayTimeMs);
        cmd.Parameters.AddWithValue("$playCount", song.PlayCount);
        cmd.Parameters.AddWithValue("$lastPlayed", DateOrNull(song.LastPlayedAt));
        cmd.Parameters.AddWithValue("$bookmarked", DateOrNull(song.BookmarkedAt));
        cmd.Parameters.AddWithValue("$created", song.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$localPath", (object?)song.LocalPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$videoType", (object?)song.VideoType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$songVersion", (object?)song.SongVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$videoVersion", (object?)song.VideoVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$downloadPath", (object?)song.DownloadPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$downloadedAt", DateOrNull(song.DownloadedAt));
        cmd.Parameters.AddWithValue("$viewCount", song.ViewCount);
        cmd.Parameters.AddWithValue("$viewCountAt", DateOrNull(song.ViewCountAt));
        cmd.Parameters.AddWithValue("$accountKey", string.IsNullOrWhiteSpace(song.AccountKey) ? "" : song.AccountKey);
    }

    private static object DateOrNull(DateTime? value)
        => value.HasValue ? value.Value.ToUniversalTime().ToString("O") : DBNull.Value;

    private static DateTime? ParseDate(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        if (r.IsDBNull(ordinal)) return null;
        return DateTime.TryParse(r.GetString(ordinal), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    private static string? GetStringOrNull(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    private static int GetIntOrZero(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? 0 : r.GetInt32(ordinal);
    }

    private static long GetLongOrZero(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? 0L : r.GetInt64(ordinal);
    }

    private static IEnumerable<string> ParseArtistNames(string artistsJson)
    {
        if (string.IsNullOrWhiteSpace(artistsJson)) yield break;
        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(artistsJson) ?? Array.Empty<string>();
        }
        catch
        {
            yield break;
        }
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
    }
}
