using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OyunAltyazi.App;

public sealed class Hotkeys : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private const uint MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x312;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();

    public Hotkeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Registers Ctrl+Shift+key. Returns false if another app owns the combination.</summary>
    public bool Register(int id, char key, Action action)
    {
        _actions[id] = action;
        return RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, key);
    }

    private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(w.ToInt32(), out var a)) { a(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
    }
}
