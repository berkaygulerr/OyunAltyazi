using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OyunAltyazi.App.UI;

/// <summary>Koyu başlık çubuğu (DWMWA_USE_IMMERSIVE_DARK_MODE = 20).</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        void Set()
        {
            int on = 1;
            var h = new WindowInteropHelper(w).Handle;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Set(); else w.SourceInitialized += (_, _) => Set();
    }
}
