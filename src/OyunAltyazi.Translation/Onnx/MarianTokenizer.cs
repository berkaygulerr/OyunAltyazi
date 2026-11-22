// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from sefflex/GameTranslator (AGPL-3.0), windows/src/GameTranslator.Translation/MarianTokenizer.cs.
// Changes: separate source/target SentencePiece models and a target-prefix encoder (continuation of an already shown sentence).

using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace OyunAltyazi.Translation;

/// <summary>
/// OPUS-MT (Marian) tokenizer. SentencePiece only splits text into pieces; ids come from the model's shared
/// <c>vocab.json</c> (same as the Hugging Face MarianTokenizer). Source text uses source.spm, target text target.spm.
/// </summary>
public sealed class MarianTokenizer
{
    public const int EosId = 43741;
    public const int UnkId = 52508;
    public const int PadId = 57059;
    private const char WordStart = '▁';

    private readonly SentencePieceTokenizer _source, _target;
    private readonly Dictionary<string, int> _vocab;
    private readonly string?[] _idToPiece;

    private MarianTokenizer(SentencePieceTokenizer source, SentencePieceTokenizer target, Dictionary<string, int> vocab)
    {
        _source = source; _target = target; _vocab = vocab;
        _idToPiece = new string?[vocab.Values.Max() + 1];
        foreach (var (piece, id) in vocab) _idToPiece[id] = piece;
    }

    public int VocabularySize => _idToPiece.Length;

    public static MarianTokenizer Load(string sourceSpmPath, string targetSpmPath, string vocabJsonPath)
    {
        static SentencePieceTokenizer Spm(string path)
        {
            using var s = File.OpenRead(path);
            // no BOS/EOS from spm: Marian appends only its own </s> id
            return SentencePieceTokenizer.Create(s, addBeginningOfSentence: false, addEndOfSentence: false);
        }
        return new MarianTokenizer(Spm(sourceSpmPath), Spm(targetSpmPath), ReadVocab(vocabJsonPath));
    }

    private static Dictionary<string, int> ReadVocab(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateObject()) vocab[entry.Name] = entry.Value.GetInt32();
        if (vocab.Count == 0) throw new InvalidDataException("vocab.json is empty");
        return vocab;
    }

    /// <summary>Encoder input: piece ids + trailing &lt;/s&gt;.</summary>
    public int[] EncodeSource(string text) => [.. Pieces(_source, text), EosId];

    /// <summary>Forced decoder prefix: piece ids of already shown Turkish text, no &lt;/s&gt;.</summary>
    public int[] EncodeTargetPrefix(string text) => string.IsNullOrWhiteSpace(text) ? [] : [.. Pieces(_target, text)];

    private List<int> Pieces(SentencePieceTokenizer spm, string text)
    {
        var tokens = spm.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false,
            considerPreTokenization: true, considerNormalization: true);
        var ids = new List<int>(tokens.Count + 1);
        var unknown = spm.UnknownId;
        for (var i = 0; i < tokens.Count; i++)
        {
            var piece = tokens[i].Value;
            if (tokens[i].Id == unknown)
            {
                // original SentencePiece merges consecutive unknown characters into one piece; this library splits them.
                // The merged piece is still looked up in vocab.json ("ğ" is unknown to the spm but present in the shared vocab).
                var run = new StringBuilder(piece);
                while (i + 1 < tokens.Count && tokens[i + 1].Id == unknown) run.Append(tokens[++i].Value);
                piece = run.ToString();
            }
            ids.Add(_vocab.TryGetValue(piece, out var id) ? id : UnkId);
        }
        return ids;
    }

    /// <summary>Decoder output to text; special tokens are skipped.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id is EosId or UnkId or PadId || id < 0 || id >= _idToPiece.Length) continue;
            if (_idToPiece[id] is { } piece) sb.Append(piece);
        }
        sb.Replace(WordStart, ' ');
        return sb.ToString().Trim();
    }
}
