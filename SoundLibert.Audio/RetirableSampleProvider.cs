using NAudio.Wave;

namespace SoundLibert.Audio;

/// <summary>
/// A WaveOut thread can finish a callback after its device was disposed.
/// Retiring this per-output gate waits for its current read and prevents any
/// later callback from consuming the next output's sought PCM.
/// <para>
/// It also lifts the thread that fills the sound card (a pool thread NAudio
/// borrows for the output's life) above the ordinary: it needs little time, but
/// on time, or the music stutters while another app in front keeps the
/// processor busy. The thread gets its usual priority back on retirement.
/// </para>
/// </summary>
internal sealed class RetirableSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly object _readLock = new();
    private bool _retired;
    private Thread? _lifted;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_readLock)
        {
            if (_retired) return 0;
            if (_lifted == null) Lift(Thread.CurrentThread);
            return source.Read(buffer, offset, count);
        }
    }

    public void Retire()
    {
        lock (_readLock)
        {
            _retired = true;
            if (_lifted == null) return;
            try { _lifted.Priority = ThreadPriority.Normal; } catch { /* the thread is gone */ }
            _lifted = null;
        }
    }

    private void Lift(Thread thread)
    {
        _lifted = thread;
        try { thread.Priority = ThreadPriority.Highest; } catch { /* not allowed here: plays as before */ }
    }
}
