namespace OyunAltyazi.Core;

public sealed record ReadLogEntry(long TimeMs, bool Changed, string Raw, string Cleaned, string Event);

public sealed class SubtitlePipeline(IFrameSource source, IOcrEngine ocr, TranslationChain chain, ISubtitleSink sink,
                                     string game, LatencyStats stats, Func<long> clockMs, int maxReadsPerSecond = 10,
                                     Func<RegionF>? regionProvider = null, SentenceJoiner? joiner = null) : IDisposable
{
    private readonly Func<SentenceJoiner> _joinerFactory = joiner is null ? () => new SentenceJoiner() : () => joiner;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();
    private readonly CoverAnchor _anchor = new();
    private RegionF? _coverBox;
    private bool _anchorTop;
    private readonly Dictionary<string, int> _speakerSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChangeGate _gate = new();
    private readonly object _lock = new();
    private readonly List<string> _recent = new();
    private readonly int _minIntervalMs = 1000 / Math.Max(1, maxReadsPerSecond);
    private int _busy;
    public Action<ReadLogEntry>? Log { get; set; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    private long _lastReadMs = long.MinValue;
    private long _lastCapturedMs, _lastOcrDoneMs;
    private IReadOnlyList<SubtitleText> _lastScene = [];
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

    /// <summary>One on-screen speaker line: its own stabilizer, sentence joiner and translation.</summary>
    private sealed class Slot(Stabilizer stabilizer, SentenceJoiner joiner)
    {
        public readonly Stabilizer Stab = stabilizer;
        public readonly SentenceJoiner Joiner = joiner;
        public CancellationTokenSource? Cts;
        public long Seq;
        public ShownUtterance? Shown;
        public List<string> Parts = new(); // shown Turkish of each fragment of the open sentence
    }

    private void CancelTranslation()
    {
        lock (_lock)
        {
            foreach (var slot in _slots.Values) { slot.Seq++; slot.Cts?.Cancel(); slot.Cts = null; }
            _slots.Clear();
            _order = new();
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

    // A slot is one speaker's line; unlabelled lines are keyed by their position.
    private static string SlotKey(SubtitleText u, int index) => u.Speaker is null ? "#" + index : u.Speaker;

    private async Task ProcessAsync(Frame frame)
    {
        try
        {
            IReadOnlyList<SubtitleText> scene;
            string raw = "";
            bool changed = _gate.HasChanged(frame);
            if (changed)
            {
                var region = regionProvider?.Invoke() ?? new RegionF(0, 0, 1, 1);
                _lastCapturedMs = frame.TimestampMs;
                var result = await ocr.ReadAsync(frame, CancellationToken.None).ConfigureAwait(false);
                _lastOcrDoneMs = clockMs();
                raw = string.Join(" | ", result.Lines.Select(l => l.Text));
                scene = TextCleaner.CleanScene(result, out var rel, out _lastLines, KnownSpeakers());
                foreach (var u in scene) NoteSpeaker(u);
                _lastScene = scene;
                _lastRegion = region;
                _lastBox = rel is null ? null : new RegionF(region.X + rel.X * region.W, region.Y + rel.Y * region.H, rel.W * region.W, rel.H * region.H);
            }
            else scene = _lastScene;

            var events = new List<(string Key, StabilizerEvent Ev)>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            for (int i = 0; i < scene.Count; i++)
            {
                var key = SlotKey(scene[i], i);
                // Slot identity follows the LINE, not its (often mis-read) speaker label: a read that matches what a slot shows or is
                // about to show belongs to that slot; a lone utterance with a lone slot is that slot's line.
                lock (_lock)
                {
                    if (!_slots.ContainsKey(key))
                    {
                        var match = _slots.FirstOrDefault(kv => !present.Contains(kv.Key) && kv.Value.Stab.Matches(scene[i].Text));
                        if (match.Key is not null) key = match.Key;
                        else if (scene.Count == 1 && _slots.Count == 1) key = _slots.Keys.First();
                    }
                }
                if (!present.Add(key)) continue;
                order.Add(key);
                Slot slot;
                lock (_lock) { if (!_slots.TryGetValue(key, out slot!)) _slots[key] = slot = new Slot(new Stabilizer(), _joinerFactory()); }
                if (slot.Stab.Feed(scene[i]) is { } ev) events.Add((key, ev));
            }
            Slot[] absent; string[] absentKeys;
            lock (_lock) { absentKeys = _slots.Keys.Where(k => !present.Contains(k)).ToArray(); absent = absentKeys.Select(k => _slots[k]).ToArray(); }
            for (int i = 0; i < absent.Length; i++)
                if (absent[i].Stab.Feed(null) is { } ev) events.Add((absentKeys[i], ev));
            if (order.Count > 0) lock (_lock) _order = order;

            Log?.Invoke(new ReadLogEntry(frame.TimestampMs, changed, raw, string.Join(" ¶ ", scene.Select(u => (u.Speaker is null ? "" : u.Speaker + ": ") + u.Text)),
                events.Any(e => e.Ev is ShowLine) ? "SHOW" : events.Any(e => e.Ev is ClearLine) ? "CLEAR" : ""));

            bool anyShow = events.Any(e => e.Ev is ShowLine);
            if (anyShow && _lastBox is { } eb) { _coverBox = _anchor.Stabilize(eb, _lastRegion ?? new RegionF(0, 0, 1, 1)); _anchorTop = _anchor.AnchorTop; }
            foreach (var (key, ev) in events)
            {
                switch (ev)
                {
                    case ShowLine show:
                        Slot? slot; lock (_lock) _slots.TryGetValue(key, out slot);
                        if (slot is not null) StartTranslation(slot, show.Line, slot.Joiner.Feed(show.Line, clockMs()), _lastCapturedMs, _lastOcrDoneMs, clockMs());
                        break;
                    case ClearLine:
                        lock (_lock)
                        {
                            if (_slots.Remove(key, out var gone)) { gone.Seq++; gone.Cts?.Cancel(); gone.Cts = null; }
                        }
                        break;
                }
            }
            if (events.Any(e => e.Ev is ClearLine)) Publish();
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

    private RegionF? _lastRegion;

    /// <summary>Hands the sink the current stacked utterances in on-screen order; clears it when none is left. Caller must hold no lock.</summary>
    private void Publish()
    {
        lock (_lock) PublishLocked();
    }

    private void PublishLocked()
    {
        var list = _order.Where(k => _slots.TryGetValue(k, out var s) && s.Shown is not null).Select(k => _slots[k].Shown!).ToList();
        if (list.Count == 0) { if (_slots.Count == 0) sink.Clear(); return; }
        sink.Show(list, _coverBox, _lastLines, _anchorTop);
    }

    private IReadOnlyCollection<string> KnownSpeakers()
    {
        lock (_lock) return _speakerSeen.Where(kv => kv.Value >= 3).Select(kv => kv.Key).ToArray();
    }

    private void NoteSpeaker(SubtitleText? read)
    {
        if (read?.Speaker is null) return;
        lock (_lock)
        {
            _speakerSeen[read.Speaker] = _speakerSeen.GetValueOrDefault(read.Speaker) + 1;
            if (_speakerSeen.Count > 40) _speakerSeen.Remove(_speakerSeen.OrderBy(kv => kv.Value).First().Key);
        }
    }

    private void StartTranslation(Slot slot, SubtitleText line, JoinResult join, long capturedMs, long ocrDoneMs, long stableMs)
    {
        CancellationTokenSource cts;
        long mySeq;
        string[] prev;
        int partIndex; string[] prefixParts; List<string> parts;
        lock (_lock)
        {
            slot.Cts?.Cancel();
            cts = slot.Cts = new CancellationTokenSource();
            mySeq = ++slot.Seq;
            prev = _recent.ToArray();
            if (join.Joined is null) slot.Parts = new List<string>();
            partIndex = slot.Parts.Count;
            prefixParts = slot.Parts.ToArray();
            slot.Parts.Add("");
            parts = slot.Parts;
        }
        var shownPrefix = prefixParts.Length > 0 && prefixParts.All(p => p.Length > 0) ? string.Join(" ", prefixParts) : null;
        var ctx = new TranslationContext(game, prev, line.Speaker);
        var joined = join.Joined;
        _ = Task.Run(async () =>
        {
            try
            {
                // 1) the fragment itself, immediately
                var r = await chain.TranslateAsync(line, ctx, cts.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (mySeq != slot.Seq || cts.IsCancellationRequested) return;
                    if (joined is null) { _recent.Add(line.Text); while (_recent.Count > 6) _recent.RemoveAt(0); }
                    slot.Shown = new ShownUtterance(line.Speaker, r.Text, r.Source);
                    if (r.Source != TranslationSource.Fallback) parts[partIndex] = r.Text;
                    PublishLocked();
                    stats.Add(new LineTiming(capturedMs, ocrDoneMs, stableMs, clockMs(), r.Source));
                }
                if (joined is null || shownPrefix is null) return;
                // 2) only this fragment's part, translated with the earlier fragments as context (never repeats them)
                var jr = await chain.TranslateContinuationAsync(line, joined, shownPrefix, new TranslationContext(game, prev, joined.Speaker), cts.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (mySeq != slot.Seq || cts.IsCancellationRequested) return;
                    if (jr.Source == TranslationSource.Fallback) return; // keep the fragment's good translation
                    if (_recent.Count > 0) _recent.RemoveAt(_recent.Count - 1); // earlier open fragment(s) are superseded
                    _recent.Add(joined.Text);
                    while (_recent.Count > 6) _recent.RemoveAt(0);
                    slot.Shown = new ShownUtterance(joined.Speaker, jr.Text, jr.Source);
                    parts[partIndex] = jr.Text;
                    PublishLocked();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }
}
