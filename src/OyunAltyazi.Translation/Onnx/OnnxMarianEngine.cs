// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from sefflex/GameTranslator (AGPL-3.0), windows/src/GameTranslator.Translation/OnnxMarianEngine.cs.
// Changes: optional forced target prefix (continuation of a sentence whose beginning is already on screen), configurable threads.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OyunAltyazi.Translation;

/// <summary>
/// Runs the Marian encoder/decoder on ONNX Runtime (CPU): beam search with KV cache. The merged decoder computes the cross-attention
/// keys on the first step and afterwards only processes the new token (use_cache_branch). With a target prefix the first step
/// feeds [start, prefix...] at once, so generation continues the shown text and only the new tokens are returned.
/// </summary>
public sealed class OnnxMarianEngine : IDisposable
{
    public const string EncoderFile = "encoder_model_int8.onnx", DecoderFile = "decoder_model_merged_int8.onnx",
        SourceSpmFile = "source.spm", TargetSpmFile = "target.spm", VocabFile = "vocab.json";
    public static readonly string[] RequiredFiles = [EncoderFile, DecoderFile, SourceSpmFile, TargetSpmFile, VocabFile];

    private const int DecoderStartId = MarianTokenizer.PadId; // single target language: decoding starts with <pad>
    private const int HeadCount = 16, HeadSize = 64;          // opus-mt-tc-big (d_model 1024)
    private const int MaxInputTokens = 256;
    public const int DefaultBeams = 4;

    private readonly MarianTokenizer _tokenizer;
    private readonly InferenceSession _encoder, _decoder;
    private readonly int _maxOutputTokens, _beams;
    private readonly string[] _pastNames, _decoderInputNames, _decoderOutputNames;
    private readonly bool[] _isSelfAttention;

    public OnnxMarianEngine(string modelDirectory, int maxOutputTokens = 128, int beams = DefaultBeams, int intraOpThreads = 2)
    {
        _maxOutputTokens = maxOutputTokens;
        _beams = Math.Max(1, beams);
        _tokenizer = MarianTokenizer.Load(Path.Combine(modelDirectory, SourceSpmFile), Path.Combine(modelDirectory, TargetSpmFile),
            Path.Combine(modelDirectory, VocabFile));
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
            InterOpNumThreads = 1,
        };
        _encoder = new InferenceSession(Path.Combine(modelDirectory, EncoderFile), options);
        try { _decoder = new InferenceSession(Path.Combine(modelDirectory, DecoderFile), options); }
        catch { _encoder.Dispose(); throw; }

        _pastNames = _decoder.InputNames.Where(n => n.StartsWith("past_key_values.", StringComparison.Ordinal)).ToArray();
        _decoderInputNames = ["input_ids", "encoder_attention_mask", "encoder_hidden_states", "use_cache_branch", .. _pastNames];
        _decoderOutputNames = ["logits", .. _pastNames.Select(n => "present." + n["past_key_values.".Length..])];
        _isSelfAttention = _pastNames.Select(n => n.EndsWith(".decoder.key", StringComparison.Ordinal) ||
                                                  n.EndsWith(".decoder.value", StringComparison.Ordinal)).ToArray();
    }

    public MarianTokenizer Tokenizer => _tokenizer;

    /// <summary>Translates one sentence. With <paramref name="targetPrefix"/> (Turkish already shown) returns only the continuation.</summary>
    public string Translate(string sentence, string? targetPrefix, CancellationToken cancellationToken)
    {
        var ids = _tokenizer.EncodeSource(sentence);
        if (ids.Length > MaxInputTokens) { ids = ids[..MaxInputTokens]; ids[^1] = MarianTokenizer.EosId; }
        var prefix = string.IsNullOrWhiteSpace(targetPrefix) ? [] : _tokenizer.EncodeTargetPrefix(targetPrefix);
        var output = Generate(ids, prefix, cancellationToken);
        var text = _tokenizer.Decode(output);
        return prefix.Length == 0 ? text : text.TrimStart(' ', ',', ';', ':', '.', '!', '?', '…', '-').Trim();
    }

    private List<int> Generate(int[] sourceIds, int[] prefix, CancellationToken cancellationToken)
    {
        var beams = _beams;
        var length = sourceIds.Length;
        var inputIds = sourceIds.Select(i => (long)i).ToArray();
        long[] sourceShape = [1, length];

        using var inputValue = OrtValue.CreateTensorValueFromMemory(inputIds, sourceShape);
        using var sourceMask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, length).ToArray(), sourceShape);
        using var runOptions = new RunOptions();
        using var encoderOutputs = _encoder.Run(runOptions, ["input_ids", "attention_mask"], [inputValue, sourceMask], ["last_hidden_state"]);
        // every beam attends to the same source: encoder output is replicated per beam
        var encoded = encoderOutputs[0].GetTensorDataAsSpan<float>();
        var hiddenData = new float[encoded.Length * beams];
        for (var b = 0; b < beams; b++) encoded.CopyTo(hiddenData.AsSpan(b * encoded.Length));
        var hiddenSize = encoded.Length / length;
        using var hidden = OrtValue.CreateTensorValueFromMemory(hiddenData, [beams, length, hiddenSize]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, beams * length).ToArray(), [beams, length]);

        var past = new OrtValue[_pastNames.Length];
        for (var i = 0; i < past.Length; i++)
            past[i] = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [beams, HeadCount, 0, HeadSize]);

        var running = new List<int>[beams];
        var scores = new float[beams];
        for (var b = 0; b < beams; b++)
        {
            running[b] = [];
            scores[b] = b == 0 ? 0 : float.NegativeInfinity; // all beams start identical: only the first proposes candidates
        }
        var finished = new List<(List<int> Tokens, float Score)>();
        // step 0 feeds [start, forced prefix...] for every beam; later steps one token per beam
        var first = new long[1 + prefix.Length];
        first[0] = DecoderStartId;
        for (var i = 0; i < prefix.Length; i++) first[1 + i] = prefix[i];
        var tokens = new long[beams * first.Length];
        for (var b = 0; b < beams; b++) first.CopyTo(tokens, b * first.Length);
        var seqLen = first.Length;
        var useCache = new bool[1];
        var limit = Math.Min(_maxOutputTokens, 2 * length + 16);
        try
        {
            for (var step = 0; step < limit; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                useCache[0] = step > 0;
                using var tokenValue = OrtValue.CreateTensorValueFromMemory(tokens, [beams, seqLen]);
                using var cacheValue = OrtValue.CreateTensorValueFromMemory(useCache, [1]);
                OrtValue[] inputs = [tokenValue, maskValue, hidden, cacheValue, .. past];
                var outputs = _decoder.Run(runOptions, _decoderInputNames, inputs, _decoderOutputNames);
                List<(int Beam, int Token, float Score)> candidates;
                using (var logits = outputs[0])
                    candidates = Candidates(logits.GetTensorDataAsSpan<float>(), scores, seqLen, 2 * beams);

                var sources = new int[beams];
                var nextRunning = new List<int>[beams];
                var count = 0;
                var next = new long[beams];
                for (var rank = 0; rank < candidates.Count && count < beams; rank++)
                {
                    var (beam, token, score) = candidates[rank];
                    if (float.IsNegativeInfinity(score)) break;
                    if (token == MarianTokenizer.EosId)
                    {
                        if (rank < beams) AddFinished(finished, running[beam], score, beams); // same rule as Hugging Face
                        continue;
                    }
                    sources[count] = beam;
                    nextRunning[count] = [.. running[beam], token];
                    scores[count] = score;
                    next[count] = token;
                    count++;
                }
                for (var b = count; b < beams; b++)
                {
                    sources[b] = sources[0];
                    nextRunning[b] = nextRunning[0] ?? [];
                    scores[b] = float.NegativeInfinity;
                    next[b] = next[0];
                }

                for (var i = 0; i < past.Length; i++)
                {
                    var present = outputs[i + 1];
                    if (_isSelfAttention[i]) { past[i].Dispose(); past[i] = Reorder(present, sources); }
                    else if (step == 0) { past[i].Dispose(); past[i] = present; } // cross attention is identical for all beams
                    else present.Dispose();
                }
                running = nextRunning;
                tokens = next;
                seqLen = 1;
                if (count == 0) break;
                if (finished.Count >= beams && scores[0] / (step + 1) <= finished[^1].Score) break;
            }
        }
        finally { foreach (var value in past) value.Dispose(); }
        return finished.Count > 0 ? finished[0].Tokens : running[0];
    }

    private static void AddFinished(List<(List<int> Tokens, float Score)> finished, List<int> tokens, float score, int beams)
    {
        var normalized = score / (tokens.Count + 1);
        var index = finished.FindIndex(f => f.Score < normalized);
        finished.Insert(index < 0 ? finished.Count : index, (tokens, normalized));
        if (finished.Count > beams) finished.RemoveAt(finished.Count - 1);
    }

    /// <summary>Best <paramref name="take"/> (beam, token, score) over all beams; scores use the last position of each beam. &lt;pad&gt; is never produced (bad_words_ids).</summary>
    private static List<(int Beam, int Token, float Score)> Candidates(ReadOnlySpan<float> logits, float[] scores, int seqLen, int take)
    {
        var vocab = logits.Length / (scores.Length * seqLen);
        var best = new List<(int Beam, int Token, float Score)>(take + 1);
        for (var b = 0; b < scores.Length; b++)
        {
            if (float.IsNegativeInfinity(scores[b])) continue;
            var row = logits.Slice((b * seqLen + seqLen - 1) * vocab, vocab);
            var max = float.NegativeInfinity;
            for (var i = 0; i < row.Length; i++)
                if (i != MarianTokenizer.PadId && row[i] > max) max = row[i];
            double sum = 0;
            for (var i = 0; i < row.Length; i++)
                if (i != MarianTokenizer.PadId) sum += Math.Exp(row[i] - max);
            var offset = scores[b] - max - (float)Math.Log(sum);
            for (var i = 0; i < row.Length; i++)
            {
                if (i == MarianTokenizer.PadId) continue;
                var score = row[i] + offset;
                if (best.Count == take && score <= best[^1].Score) continue;
                var index = best.FindIndex(c => c.Score < score);
                best.Insert(index < 0 ? best.Count : index, (b, i, score));
                if (best.Count > take) best.RemoveAt(take);
            }
        }
        return best;
    }

    private static OrtValue Reorder(OrtValue present, int[] sources)
    {
        var identity = true;
        for (var b = 0; b < sources.Length; b++) identity &= sources[b] == b;
        if (identity) return present;
        using (present)
        {
            var shape = present.GetTensorTypeAndShape().Shape;
            var data = present.GetTensorDataAsSpan<float>();
            var block = data.Length / sources.Length;
            var copy = new float[data.Length];
            for (var b = 0; b < sources.Length; b++)
                data.Slice(sources[b] * block, block).CopyTo(copy.AsSpan(b * block));
            return OrtValue.CreateTensorValueFromMemory(copy, shape);
        }
    }

    public void Dispose() { _decoder.Dispose(); _encoder.Dispose(); }
}
