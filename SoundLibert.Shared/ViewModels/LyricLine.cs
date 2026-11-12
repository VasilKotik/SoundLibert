using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// One line of lyrics with its start time (Zero for plain, untimed lyrics).
/// <see cref="IsCurrent"/> is observable so the lyrics list can highlight the
/// line that is actually playing — the previous approach (ListBox SelectedIndex)
/// did not reliably show it.
/// </summary>
public sealed class LyricLine : INotifyPropertyChanged
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool IsTimed { get; init; }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
