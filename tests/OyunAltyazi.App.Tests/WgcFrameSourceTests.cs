using System.Diagnostics;
using OyunAltyazi.App.Capture;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

/// <summary>Test window: a separate process hosting a plain WinForms form (notepad on Win11 is a shared/tabbed app).</summary>
public sealed class TestWindow : IDisposable
{
    private readonly Process _p;
    public IntPtr Hwnd { get; }
    public int Pid => _p.Id;

    public TestWindow()
    {
        var script = "Add-Type -AssemblyName System.Windows.Forms;" +
            "$f=New-Object Windows.Forms.Form;$f.Text='OyunAltyaziTest';$f.BackColor='White';$f.Width=800;$f.Height=500;" +
            "$f.StartPosition='Manual';$f.Left=50;$f.Top=50;" +
            "$l=New-Object Windows.Forms.Label;$l.Text='Price: Move up, now.';$l.Font=New-Object Drawing.Font('Segoe UI',28);$l.AutoSize=$true;$l.ForeColor='Black';$f.Controls.Add($l);" +
            "[void]$f.ShowDialog()";
        _p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script}\"") { WindowStyle = ProcessWindowStyle.Normal })!;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            var w = WindowEnumerator.List().FirstOrDefault(x => x.ProcessId == _p.Id && x.Title == "OyunAltyaziTest");
            if (w is not null) { Hwnd = w.Hwnd; Thread.Sleep(500); return; }
            Thread.Sleep(200);
        }
        Kill();
        throw new InvalidOperationException("Test penceresi açılamadı");
    }

    public void Kill() { try { _p.Kill(true); } catch { } }
    public void Dispose() => Kill();
}

public class WgcFrameSourceTests
{
    [Fact]
    public void Lists_test_window_excludes_own_process()
    {
        using var w = new TestWindow();
        var list = WindowEnumerator.List();
        Assert.Contains(list, x => x.ExeName == "powershell.exe" && x.ProcessId == w.Pid);
        Assert.DoesNotContain(list, x => x.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void Captures_region_of_window()
    {
        using var w = new TestWindow();
        using var src = new WgcFrameSource(w.Hwnd, () => new RegionF(0, 0, 1, 0.3));
        Frame? got = null;
        using var ev = new ManualResetEventSlim();
        src.FrameArrived += f => { got ??= f; ev.Set(); };
        src.Start();
        Assert.True(ev.Wait(2000), "kare gelmedi");
        Assert.True(got!.Width > 0 && got.Height > 0);
        Assert.Contains(got.Bgra, b => b != 0);
    }

    [Fact]
    public void Stopped_raised_when_window_closes()
    {
        using var w = new TestWindow();
        using var src = new WgcFrameSource(w.Hwnd, () => new RegionF(0, 0, 1, 0.3));
        using var ev = new ManualResetEventSlim();
        string? reason = null;
        src.Stopped += r => { reason = r; ev.Set(); };
        src.Start();
        Thread.Sleep(500);
        w.Kill();
        Assert.True(ev.Wait(2000), "Stopped gelmedi");
        Assert.Equal("Pencere kapandı", reason);
    }

    [Fact]
    public void Snapshot_returns_frame()
    {
        using var w = new TestWindow();
        var f = WgcFrameSource.Snapshot(w.Hwnd);
        Assert.NotNull(f);
        Assert.True(f!.Width > 0);
    }
}
