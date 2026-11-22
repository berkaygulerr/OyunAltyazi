// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from sefflex/GameTranslator (AGPL-3.0), windows/src/GameTranslator.Translation/ModelStore.cs.
// Changes: target.spm added (continuation), mandatory SHA-256 check of every file on install and when asked, no partial/resume logic.
// Model: Helsinki-NLP/opus-mt-tc-big-en-tr (CC BY 4.0), int8 ONNX export by onnx-community, used unmodified.

using System.Security.Cryptography;
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

public sealed record OnnxModelFile(string Name, Uri Url, long Size, string Sha256);

public static class OnnxPaths
{
    // same data root as the CT2 model (outside the Velopack install dir); a different folder, the CT2 model is never touched
    public static string ModelDir => Path.Combine(Ct2Paths.Root, "models", "opus-mt-tc-big-en-tr-onnx");
}

/// <summary>Pinned, checksummed model assets. A file under its final name is always verified; a partial download is never installed.</summary>
public sealed class OnnxModelStore(string installDirectory, IReadOnlyList<OnnxModelFile>? files = null, HttpClient? http = null)
{
    // pinned to one repository revision: the SHA-256 values below only hold for this commit
    public const string Revision = "ce128ea834a090aca240e4e113e7756e06cc78e0";
    private static readonly string BaseUrl = $"https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr/resolve/{Revision}/";

    public static IReadOnlyList<OnnxModelFile> EnglishTurkish { get; } =
    [
        new(OnnxMarianEngine.EncoderFile, new(BaseUrl + "onnx/" + OnnxMarianEngine.EncoderFile), 135_730_164,
            "58bfc076347346049a52a8d53b1f6e4664af74e9737aded374d7d1f78805f46e"),
        new(OnnxMarianEngine.DecoderFile, new(BaseUrl + "onnx/" + OnnxMarianEngine.DecoderFile), 395_716_769,
            "23223bce303d925b119ec905b9422a2814e7ad5f96275407be1d387b13879147"),
        new(OnnxMarianEngine.SourceSpmFile, new(BaseUrl + OnnxMarianEngine.SourceSpmFile), 796_953,
            "180d3f94ddfee5585d78b702290ded0f4f9bd40f299349c8383b32a579fefad4"),
        new(OnnxMarianEngine.TargetSpmFile, new(BaseUrl + OnnxMarianEngine.TargetSpmFile), 833_391,
            "cd0c475692178b6c626aadaef01bd8f98d8053946938e4e67005fc1e4811d797"),
        new(OnnxMarianEngine.VocabFile, new(BaseUrl + OnnxMarianEngine.VocabFile), 1_501_287,
            "01ef522455d2bf22b0716416ee34688cc2b3f5fa257f742665d21b6acc450ac1"),
    ];

    private readonly IReadOnlyList<OnnxModelFile> _files = files ?? EnglishTurkish;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public IReadOnlyList<OnnxModelFile> Files => _files;
    public long TotalBytes => _files.Sum(f => f.Size);
    public string PathOf(string name) => Path.Combine(installDirectory, name);

    /// <summary>Cheap check (names and sizes); use <see cref="VerifyAsync"/> for the checksum.</summary>
    public bool IsInstalled => _files.All(f => new FileInfo(PathOf(f.Name)) is { Exists: true } i && i.Length == f.Size);

    /// <summary>Names of files that are missing, have the wrong size or a wrong SHA-256.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken ct = default)
    {
        var bad = new List<string>();
        foreach (var f in _files)
        {
            var info = new FileInfo(PathOf(f.Name));
            if (!info.Exists || info.Length != f.Size || !string.Equals(await HashAsync(info.FullName, ct).ConfigureAwait(false), f.Sha256, StringComparison.OrdinalIgnoreCase))
                bad.Add(f.Name);
        }
        return bad;
    }

    /// <summary>Downloads missing/invalid files into .part, installs each only after size and SHA-256 match. Throws <see cref="InvalidDataException"/> otherwise.</summary>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(installDirectory);
        long done = 0, total = TotalBytes;
        foreach (var f in _files)
        {
            var final = PathOf(f.Name);
            if (new FileInfo(final) is { Exists: true } i && i.Length == f.Size && await HashAsync(final, ct).ConfigureAwait(false) == f.Sha256)
            { done += f.Size; progress?.Report((double)done / total); continue; }
            var part = final + ".part";
            using (var resp = await _http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n; progress?.Report((double)done / total);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                if (dst.Length != f.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    dst.Close(); File.Delete(part);
                    throw new InvalidDataException($"{f.Name}: size/SHA-256 mismatch");
                }
            }
            File.Move(part, final, overwrite: true);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
