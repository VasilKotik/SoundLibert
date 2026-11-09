using NAudio.Wave;

namespace SoundLibert.Audio;

/// <summary>
/// A WaveOut thread can finish a callback after its device was disposed.
/// Retiring this per-output gate waits for its current read and prevents any
/// later callback from consuming the next output's sought PCM.
/// </summary>
internal sealed class RetirableSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly object _readLock = new();
    private bool _retired;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_readLock)
            return _retired ? 0 : source.Read(buffer, offset, count);
    }

    public void Retire()
    {
        lock (_readLock) _retired = true;
    }
}
