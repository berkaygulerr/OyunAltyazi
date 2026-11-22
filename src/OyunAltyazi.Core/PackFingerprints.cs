using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OyunAltyazi.Core;

public sealed class PackFingerprints
{
    private readonly PackCatalog _catalog;
    private readonly byte[] _rows;
    private const int RowSize = 9;

    private PackFingerprints(PackCatalog catalog, byte[] rows) { _catalog = catalog; _rows = rows; }

    public static PackFingerprints LoadDefault()
    {
        var catalog = PackCatalog.Default;
        using var source = typeof(PackFingerprints).Assembly.GetManifestResourceStream("OyunAltyazi.Core.PackFingerprints.bin.gz")
            ?? throw new InvalidDataException("Pack fingerprint index missing.");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        Span<byte> header = stackalloc byte[36];
        gzip.ReadExactly(header);
        var stamp = string.Join('\n', catalog.Entries.Select(e => $"{e.Id}:{e.Sha256}"));
        if (!header[..32].SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))))
            throw new InvalidDataException("Pack fingerprint catalog mismatch.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
        if (count is <= 0 or > 3_000_000) throw new InvalidDataException("Invalid fingerprint count.");
        var rows = new byte[count * RowSize];
        gzip.ReadExactly(rows);
        if (gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected fingerprint data.");
        return new(catalog, rows);
    }

    public PackCatalogEntry? Find(string text)
    {
        var key = ulong.Parse(TextKey.PackKey(text), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int low = 0, high = _rows.Length / RowSize - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int offset = mid * RowSize;
            ulong current = BinaryPrimitives.ReadUInt64LittleEndian(_rows.AsSpan(offset, 8));
            if (current == key)
            {
                int slot = _rows[offset + 8];
                return slot < _catalog.Entries.Count ? _catalog.Entries[slot] : null;
            }
            if (current < key) low = mid + 1;
            else high = mid - 1;
        }
        return null;
    }
}
