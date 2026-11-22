using System.Runtime.InteropServices;
using System.Text;

namespace OyunAltyazi.Translation;

/// <summary>
/// Stores the user's Gemini API key encrypted with Windows DPAPI (current user scope) in its own file, never in settings.json.
/// The key is never logged; a corrupt/foreign/other-user file simply loads as "no key".
/// </summary>
public sealed class DpapiKeyStore(string directory)
{
    public const string FileName = "gemini.key.dat";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OyunAltyazi.Gemini.ApiKey.v1");
    private string FilePath => Path.Combine(directory, FileName);

    public bool Exists => File.Exists(FilePath);

    public void Save(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new ArgumentException("API anahtarı boş olamaz.", nameof(key));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI yalnızca Windows'ta kullanılabilir.");
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(key), protect: true) ?? throw new IOException("Anahtar şifrelenemedi.");
        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string? Load()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(FilePath)) return null;
            var plain = Transform(File.ReadAllBytes(FilePath), protect: false);
            if (plain is null) return null;
            var s = Encoding.UTF8.GetString(plain).Trim();
            return s.Length == 0 ? null : s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public void Delete() { try { File.Delete(FilePath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int cbData; public IntPtr pbData; }
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var din = new DataBlob { cbData = input.Length, pbData = inH.AddrOfPinnedObject() };
            var den = new DataBlob { cbData = Entropy.Length, pbData = enH.AddrOfPinnedObject() };
            DataBlob dout;
            bool ok = protect
                ? CryptProtectData(ref din, null, ref den, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out dout)   // CurrentUser scope (no LOCAL_MACHINE flag)
                : CryptUnprotectData(ref din, IntPtr.Zero, ref den, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out dout);
            if (!ok) return null;
            try
            {
                var result = new byte[dout.cbData];
                Marshal.Copy(dout.pbData, result, 0, dout.cbData);
                return result;
            }
            finally { LocalFree(dout.pbData); }
        }
        finally { inH.Free(); enH.Free(); }
    }
}
