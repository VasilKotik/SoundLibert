using NAudio.Dsp;
using NAudio.Wave;

namespace SoundLibert.Audio;

/// <summary>
/// A 3-band equalizer (low-shelf / peaking / high-shelf) built from NAudio
/// <see cref="BiQuadFilter"/>s. Each channel has its own filters: a filter keeps
/// state between samples, so sharing one across interleaved channels would bleed
/// left into right and halve its effective frequency. Bands with 0 dB gain are
/// bypassed entirely, so the default (flat) path is free of extra processing.
/// A boost lowers the input by the same amount, so peaks do not clip.
/// </summary>
internal sealed class BiquadEqualizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;

    // One immutable snapshot, swapped whole: the audio thread must never see a
    // half-updated set of bands while the UI thread moves a slider.
    private volatile Bands? _bands;

    private float _bassDb;
    private float _midDb;
    private float _trebleDb;

    public BiquadEqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = Math.Max(1, source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float BassDb
    {
        get => _bassDb;
        set { if (Math.Abs(_bassDb - value) > 0.01f) { _bassDb = value; Rebuild(); } }
    }

    public float MidDb
    {
        get => _midDb;
        set { if (Math.Abs(_midDb - value) > 0.01f) { _midDb = value; Rebuild(); } }
    }

    public float TrebleDb
    {
        get => _trebleDb;
        set { if (Math.Abs(_trebleDb - value) > 0.01f) { _trebleDb = value; Rebuild(); } }
    }

    private void Rebuild()
    {
        var low = Math.Abs(_bassDb) > 0.01f ? new BiQuadFilter[_channels] : null;
        var mid = Math.Abs(_midDb) > 0.01f ? new BiQuadFilter[_channels] : null;
        var high = Math.Abs(_trebleDb) > 0.01f ? new BiQuadFilter[_channels] : null;
        if (low == null && mid == null && high == null)
        {
            _bands = null;
            return;
        }

        for (var channel = 0; channel < _channels; channel++)
        {
            if (low != null) low[channel] = BiQuadFilter.LowShelf(_sampleRate, 160f, 0.7f, _bassDb);
            if (mid != null) mid[channel] = BiQuadFilter.PeakingEQ(_sampleRate, 1000f, 1.0f, _midDb);
            if (high != null) high[channel] = BiQuadFilter.HighShelf(_sampleRate, 6000f, 0.7f, _trebleDb);
        }

        var boostDb = Math.Max(0f, Math.Max(_bassDb, Math.Max(_midDb, _trebleDb)));
        _bands = new Bands(low, mid, high, (float)Math.Pow(10, -boostDb / 20.0));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var bands = _bands;
        if (bands == null) return read;

        for (var i = 0; i < read; i++)
        {
            var channel = i % _channels;
            var sample = buffer[offset + i] * bands.PreGain;
            if (bands.Low != null) sample = bands.Low[channel].Transform(sample);
            if (bands.Mid != null) sample = bands.Mid[channel].Transform(sample);
            if (bands.High != null) sample = bands.High[channel].Transform(sample);
            buffer[offset + i] = sample;
        }
        return read;
    }

    private sealed record Bands(BiQuadFilter[]? Low, BiQuadFilter[]? Mid, BiQuadFilter[]? High, float PreGain);
}
