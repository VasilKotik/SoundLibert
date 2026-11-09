using SoundLibert.Domain.Enums;

namespace SoundLibert.Domain.Interfaces;

public interface IAudioPlayer : IDisposable
{
    event EventHandler<MediaPlayerState>? StateChanged;
    event EventHandler<TimeSpan>? PositionChanged;
    event EventHandler? TrackEnded;

    /// <summary>
    /// The current track stopped playing because of an error (a failed download,
    /// an undecodable stream, a lost output device), as opposed to reaching its
    /// end. A failure to START playback is reported by <see cref="PlayAsync"/> instead.
    /// </summary>
    event EventHandler<Exception>? PlaybackFailed;

    MediaPlayerState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    bool IsMuted { get; set; }

    /// <summary>Extra gain (0..1 typical) applied on top of the user volume, e.g. loudness normalisation.</summary>
    double NormalizationGain { get; set; }

    /// <summary>
    /// Crossfade duration in seconds (0 = off). When > 0 the track fades out over
    /// the last N seconds and the next one fades in over the first N seconds.
    /// </summary>
    double CrossfadeSeconds { get; set; }

    /// <summary>Equalizer band gains in dB (−12..+12); 0 = bypass.</summary>
    double EqualizerBassDb { get; set; }
    double EqualizerMidDb { get; set; }
    double EqualizerTrebleDb { get; set; }

    /// <summary>Output device by name (empty = the system default). Applies immediately.</summary>
    string OutputDeviceName { get; set; }

    /// <summary>Names of the available output devices.</summary>
    IReadOnlyList<string> GetOutputDevices();

    /// <summary>
    /// Starts playback from <paramref name="startPosition"/> (e.g. a restored
    /// session position). Seeking BEFORE the output exists needs no buffer
    /// flush, unlike SeekAsync after Play, which can collide with a
    /// just-started device (WaveStillPlaying → dead output → phantom skip).
    /// Completes once playback has started or a newer call superseded this one;
    /// throws when this is still the current call and the media cannot be played.
    /// </summary>
    Task PlayAsync(string url, IReadOnlyDictionary<string, string> headers, TimeSpan startPosition, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
    Task SetVolumeAsync(double volume, CancellationToken ct = default);
}
