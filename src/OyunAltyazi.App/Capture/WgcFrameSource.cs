#pragma warning disable CA1416
using Vortice.Mathematics;
using System.Runtime.InteropServices;
using OyunAltyazi.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace OyunAltyazi.App.Capture;

public sealed unsafe class WgcFrameSource : IFrameSource
{
    private static readonly Guid IidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr hs);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hs);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr inspectable);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);

    private readonly IntPtr _hwnd;
    private readonly Func<RegionF> _region;
    private readonly int _minIntervalMs;
    private readonly object _lock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private Windows.Graphics.SizeInt32 _poolSize;
    private long _lastMs;
    private bool _running;
    private Timer? _watch;
    private Frame? _lastFrame;
    private RegionF? _lastRegion;
    private long _lastEmitMs;

    public event Action<Frame>? FrameArrived;
    public event Action<string>? Stopped;

    public WgcFrameSource(IntPtr hwnd, Func<RegionF> region, int maxFps = 15)
    {
        _hwnd = hwnd;
        _region = region;
        _minIntervalMs = 1000 / Math.Max(1, maxFps);
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public void Start()
    {
        string? failure = null;
        lock (_lock)
        {
            if (_running) return;
            if (!IsWindow(_hwnd)) failure = "Pencere kapandı";
            else
            {
                try
                {
                    D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out _device).CheckError();
                    _ctx = _device!.ImmediateContext;
                    using var dxgi = _device.QueryInterface<IDXGIDevice>();
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var insp));
                    _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(insp);
                    _item = CreateItem(_hwnd);
                    _item.Closed += OnClosed;
                    _poolSize = _item.Size;
                    _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                    _pool.FrameArrived += OnPoolFrame;
                    _session = _pool.CreateCaptureSession(_item);
                    try { _session.IsCursorCaptureEnabled = false; } catch { }
                    try { _session.IsBorderRequired = false; } catch { }
                    _session.StartCapture();
                    _running = true;
                    _watch = new Timer(_ => Tick(), null, 100, 100);
                }
                catch (Exception e)
                {
                    Cleanup();
                    failure = "Yakalama başlatılamadı: " + e.Message;
                }
            }
        }
        if (failure is not null) Stopped?.Invoke(failure);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            Cleanup();
        }
    }

    public void Dispose() => Stop();

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        bool was;
        lock (_lock) { was = _running; _running = false; Cleanup(); }
        if (was) Stopped?.Invoke("Pencere kapandı");
    }

    // WGC only delivers frames when the window content changes. A static subtitle would never be
    // confirmed by the stabilizer, so a quiet source repeats its last frame (same region only).
    private void Tick()
    {
        if (!IsWindow(_hwnd)) { OnClosed(null!, null!); return; }
        Frame? again = null;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (_running && _lastFrame is not null && _lastRegion == _region() && now - _lastEmitMs >= 100)
            {
                _lastEmitMs = now;
                again = _lastFrame with { TimestampMs = now };
            }
        }
        if (again is not null) FrameArrived?.Invoke(again);
    }

    private void Cleanup()
    {
        _watch?.Dispose(); _watch = null;
        _lastFrame = null; _lastRegion = null;
        try { if (_pool is not null) _pool.FrameArrived -= OnPoolFrame; } catch { }
        try { if (_item is not null) _item.Closed -= OnClosed; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null; _pool = null; _item = null;
        _staging?.Dispose(); _staging = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { }
        _winrtDevice = null;
    }

    private void OnPoolFrame(Direct3D11CaptureFramePool pool, object _)
    {
        Frame? output = null;
        lock (_lock)
        {
            if (!_running || _pool is null) return;
            using var f = pool.TryGetNextFrame();
            if (f is null) return;
            try
            {
                var size = f.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    _poolSize = size;
                    if (size.Width > 0 && size.Height > 0)
                        pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    return;
                }
                long now = Environment.TickCount64;
                if (now - _lastMs < _minIntervalMs) return;
                _lastMs = now;
                var region = _region();
                output = CopyRegion(f, now, region);
                _lastFrame = output; _lastRegion = region; _lastEmitMs = now;
            }
            catch (Exception) { /* a dropped frame is fine; the next one retries */ }
        }
        if (output is not null) FrameArrived?.Invoke(output);
    }

    private Frame? CopyRegion(Direct3D11CaptureFrame f, long ts, RegionF r)
    {
        var size = f.ContentSize;
        int x = Math.Clamp((int)(r.X * size.Width), 0, size.Width - 1);
        int y = Math.Clamp((int)(r.Y * size.Height), 0, size.Height - 1);
        int w = Math.Clamp((int)(r.W * size.Width), 1, size.Width - x);
        int h = Math.Clamp((int)(r.H * size.Height), 1, size.Height - y);
        var surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(f.Surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in IidAccess, out var access));
            try
            {
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                Guid iid = IidTexture2D;
                IntPtr texPtr;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texPtr));
                using var src = new ID3D11Texture2D(texPtr);
                if (_staging is null || _staging.Description.Width != (uint)w || _staging.Description.Height != (uint)h)
                {
                    _staging?.Dispose();
                    _staging = _device!.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                    });
                }
                _ctx!.CopySubresourceRegion(_staging, 0, 0, 0, 0, src, 0, new Box(x, y, 0, x + w, y + h, 1));
                var map = _ctx.Map(_staging, 0, MapMode.Read);
                try
                {
                    var bytes = new byte[w * h * 4];
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(map.DataPointer + row * (int)map.RowPitch, bytes, row * w * 4, w * 4);
                    return new Frame(w, h, w * 4, bytes, ts);
                }
                finally { _ctx.Unmap(_staging, 0); }
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(surfacePtr); }
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        IntPtr factory;
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, in IidInterop, out factory)); }
        finally { WindowsDeleteString(hs); }
        try
        {
            var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[3];
            Guid iid = IidItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(createForWindow(factory, hwnd, &iid, &itemPtr));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>Single full-window frame (downscaled to maxWidth) for the region selector.</summary>
    public static Frame? Snapshot(IntPtr hwnd, int maxWidth = 1280)
    {
        Frame? got = null;
        using var done = new ManualResetEventSlim();
        using var src = new WgcFrameSource(hwnd, () => new RegionF(0, 0, 1, 1), 60);
        src.FrameArrived += f => { got ??= f; done.Set(); };
        src.Stopped += _ => done.Set();
        src.Start();
        done.Wait(3000);
        src.Stop();
        if (got is null || got.Width <= maxWidth) return got;
        int nw = maxWidth, nh = Math.Max(1, got.Height * maxWidth / got.Width);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                Buffer.BlockCopy(got.Bgra, (y * got.Height / nh) * got.Stride + (x * got.Width / nw) * 4, o, (y * nw + x) * 4, 4);
        return new Frame(nw, nh, nw * 4, o, got.TimestampMs);
    }
}
