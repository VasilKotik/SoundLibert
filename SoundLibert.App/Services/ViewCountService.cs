using System.Diagnostics;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Resolves and caches global YouTube view counts for catalogue rows. The DB
/// cache is the fast path; only rows still at 0 are resolved over the network,
/// with a cap and a small concurrency so a huge playlist cannot flood requests.
/// </summary>
internal sealed class ViewCountService : IViewCountService
{
    private const int MaxResolvePerCall = 40;
    private const int Concurrency = 4;

    private readonly IMusicProvider _provider;
    private readonly IMusicDatabase _database;

    public ViewCountService(IMusicProvider provider, IMusicDatabase database)
    {
        _provider = provider;
        _database = database;
    }

    public async Task HydrateAsync(IReadOnlyList<SongItem> songs, CancellationToken ct = default)
    {
        var ids = songs
            .Where(s => s.ViewCount == 0 && !s.IsLocal && !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => s.Id)
            .ToList();
        if (ids.Count == 0) return;

        try
        {
            var cached = await _database.GetViewCountsAsync(ids, ct);
            if (cached.Count == 0) return;
            foreach (var song in songs)
                if (cached.TryGetValue(song.Id, out var views))
                    song.ViewCount = views;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Views] hydrate failed: {ex.Message}");
        }
    }

    public async Task ResolveMissingAsync(IReadOnlyList<SongItem> songs, CancellationToken ct = default)
    {
        var missing = songs
            .Where(s => s.ViewCount == 0 && !s.IsLocal && !string.IsNullOrWhiteSpace(s.Id))
            .Take(MaxResolvePerCall)
            .ToList();
        if (missing.Count == 0) return;

        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = missing.Select(async song =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var views = await _provider.GetViewCountAsync(song.Id, ct);
                if (views is > 0)
                {
                    song.ViewCount = views.Value;
                    try { await _database.SetViewCountAsync(song.Id, views.Value, DateTime.UtcNow, ct); }
                    catch { /* cache write is best-effort */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Views] {song.Id} failed: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }
}
