using System.Diagnostics;

namespace OyunAltyazi.App.Ocr;

public static class OcrSetup
{
    /// <summary>Yönetici izniyle Windows İngilizce OCR özelliğini kurar. İzin reddedilirse false.</summary>
    public static async Task<bool> InstallEnglishOcrAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Minimized,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~en-US~0.0.1.0'\"",
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; } // UAC iptal
    }
}
