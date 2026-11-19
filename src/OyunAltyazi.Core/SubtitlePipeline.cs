namespace OyunAltyazi.Core;

public sealed class SubtitlePipeline(IFrameSource source, IOcrEngine ocr, TranslationChain chain, ISubtitleSink sink,
                                     string game, LatencyStats stats, Func<long> clockMs, int maxReadsPerSecond = 10,
                                     Func<RegionF>? regionProvider = null) : IDisposable
{
    private readonly ChangeGate _gate = new();
    private readonly Stabilizer _stabilizer = new();
    private readonly object _lock = new();
    private readonly List<string> _recent = new();
    private readonly int _minIntervalMs = 1000 / Math.Max(1, maxReadsPerSecond);
    private int _busy;
    private long _lastReadMs = long.MinValue;
    private SubtitleText? _lastRead;
    private long _lastCapturedMs, _lastOcrDoneMs;
    private CancellationTokenSource? _translateCts;
    private long _seq;
    private bool _running;
    private RegionF? _lastBox;
    private int _lastLines = 1;

    public void Start()
    {
        if (_running) return;
        _running = true;
        source.FrameArrived += OnFrame;
        source.Stopped += OnStopped;
        source.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        source.FrameArrived -= OnFrame;
        source.Stopped -= OnStopped;
        source.Stop();
        CancelTranslation();
    }

    public void Dispose() => Stop();

    private void OnStopped(string reason)
    {
        Stop();
        sink.Clear();
    }

    private void CancelTranslation()
    {
        lock (_lock)
        {
            _seq++;
            _translateCts?.Cancel();
            _translateCts = null;
        }
    }

    private void OnFrame(Frame frame)
    {
        // Queue depth 1: while OCR is busy (or the rate limit is not yet reached) frames are dropped.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var now = clockMs();
        if (_lastReadMs != long.MinValue && now - _lastReadMs < _minIntervalMs)
        {
            Volatile.Write(ref _busy, 0);
            return;
        }
        _lastReadMs = now;
        _ = ProcessAsync(frame);
    }

    private async Task ProcessAsync(Frame frame)
    {
        try
        {
            SubtitleText? read;
            if (_gate.HasChanged(frame))
            {
                var region = regionProvider?.Invoke() ?? new RegionF(0, 0, 1, 1);
                _lastCapturedMs = frame.TimestampMs;
                var result = await ocr.ReadAsync(frame, CancellationToken.None).ConfigureAwait(false);
                _lastOcrDoneMs = clockMs();
                read = TextCleaner.Clean(result, out var rel, out _lastLines);
                _lastRead = read;
                _lastBox = rel is null ? null : new RegionF(region.X + rel.X * region.W, region.Y + rel.Y * region.H, rel.W * region.W, rel.H * region.H);
            }
            else
            {
                read = _lastRead;
            }

            switch (_stabilizer.Feed(read))
            {
                case ShowLine show:
                    StartTranslation(show.Line, _lastBox, _lastLines, _lastCapturedMs, _lastOcrDoneMs, clockMs());
                    break;
                case ClearLine:
                    CancelTranslation();
                    sink.Clear();
                    break;
            }
        }
        catch (Exception)
        {
            // An OCR failure on one frame must not kill the pipeline; the next frame retries.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void StartTranslation(SubtitleText line, RegionF? box, int lineCount, long capturedMs, long ocrDoneMs, long stableMs)
    {
        CancellationTokenSource cts;
        long mySeq;
        string[] prev;
        lock (_lock)
        {
            _translateCts?.Cancel();
            cts = _translateCts = new CancellationTokenSource();
            mySeq = ++_seq;
            prev = _recent.ToArray();
        }
        var ctx = new TranslationContext(game, prev, line.Speaker);
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await chain.TranslateAsync(line, ctx, cts.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (mySeq != _seq || cts.IsCancellationRequested) return;
                    _recent.Add(line.Text);
                    while (_recent.Count > 6) _recent.RemoveAt(0);
                    sink.Show(line.Speaker, r.Text, r.Source, box, lineCount);
                    stats.Add(new LineTiming(capturedMs, ocrDoneMs, stableMs, clockMs(), r.Source));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }
}
