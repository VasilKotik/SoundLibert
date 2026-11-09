using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundLibert.UI.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public virtual void Initialize() { }
}
