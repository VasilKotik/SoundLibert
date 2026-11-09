#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace SoundLibert.UI.Services;

/// <summary>
/// Playback progress on the app's taskbar button (ITaskbarList3): a green bar
/// for the position, yellow while paused, an indeterminate bar while a track
/// is loading, nothing when stopped. Best-effort — any COM failure just turns
/// the feature off for the session.
/// <para>
/// Created with CoCreateInstance and called through the raw vtable (no
/// [ComImport] RCW, which does not mix well with CsWinRT's ComWrappers).
/// </para>
/// </summary>
public sealed unsafe class TaskbarProgress
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    // ITaskbarList4 (derives from ITaskbarList3, same slots for the progress
    // calls). Asking for ITaskbarList3 itself returned E_NOINTERFACE on
    // Windows 11 build 26300, while ITaskbarList4 works there and on 7+.
    private static readonly Guid IidTaskbarList4 = new("c43dc798-95d1-4bea-9030-bb99e2983a1a");
    private const uint ClsctxInprocServer = 0x1;

    // vtable slots: IUnknown 0-2, ITaskbarList 3-7 (HrInit = 3),
    // ITaskbarList2 8, ITaskbarList3 SetProgressValue = 9, SetProgressState = 10.
    private const int SlotHrInit = 3;
    private const int SlotSetProgressValue = 9;
    private const int SlotSetProgressState = 10;

    private readonly IntPtr _hwnd;
    private IntPtr _taskbar;
    private TaskbarState _lastState = (TaskbarState)(-1);
    private ulong _lastValue = ulong.MaxValue;

    public enum TaskbarState
    {
        None = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    public TaskbarProgress(IntPtr hwnd)
    {
        _hwnd = hwnd;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidTaskbarList, IntPtr.Zero, ClsctxInprocServer, IidTaskbarList4, out var instance));
            _taskbar = instance;
            var hrInit = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(SlotHrInit);
            Marshal.ThrowExceptionForHR(hrInit(_taskbar));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Taskbar] init failed: {ex.Message}");
            Release();
        }
    }

    private IntPtr Slot(int index) => (*(IntPtr**)_taskbar)[index];

    /// <summary>Must be called on the UI (STA) thread.</summary>
    public void Update(TaskbarState state, double fraction = 0)
    {
        if (_taskbar == IntPtr.Zero) return;
        try
        {
            if (state != _lastState)
            {
                var setState = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Slot(SlotSetProgressState);
                Marshal.ThrowExceptionForHR(setState(_taskbar, _hwnd, (int)state));
                _lastState = state;
                _lastValue = ulong.MaxValue;
            }
            if (state is TaskbarState.Normal or TaskbarState.Paused)
            {
                // 1000 steps: precise enough, and unchanged values are skipped.
                var value = (ulong)Math.Clamp(Math.Round(fraction * 1000), 0, 1000);
                if (value != _lastValue)
                {
                    var setValue = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, ulong, int>)Slot(SlotSetProgressValue);
                    Marshal.ThrowExceptionForHR(setValue(_taskbar, _hwnd, value, 1000));
                    _lastValue = value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Taskbar] update failed: {ex.Message}");
            Release();
        }
    }

    private void Release()
    {
        if (_taskbar == IntPtr.Zero) return;
        try { Marshal.Release(_taskbar); } catch { }
        _taskbar = IntPtr.Zero;
    }
}
#endif
