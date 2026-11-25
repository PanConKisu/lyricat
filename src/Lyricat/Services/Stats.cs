using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LyricsStatus.Services;

public static class Stats
{
    static readonly object Gate = new();
    static readonly byte[] Magic = "LYCS"u8.ToArray();
    static readonly byte[] Entropy = "Lyricat/status-changes/v1"u8.ToArray();
    static long _total = -1;
    static long _session;

    public static string FilePath => Path.Combine(Paths.Data, "stats.dat");

    public static bool WasReset { get { lock (Gate) { Load(); return _wasReset; } } }
    static bool _wasReset;

    public static long Session { get { lock (Gate) return _session; } }
    public static long Total { get { lock (Gate) { Load(); return _total; } } }

    public static void Add()
    {
        lock (Gate)
        {
            Load();
            _session++;
            _total++;
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, Seal(_total));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception) { /* disk trouble: the count stays in memory */ }
        }
    }

    static void Load()
    {
        if (_total >= 0) return;
        _total = 0;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (Unseal(File.ReadAllBytes(FilePath)) is { } total) _total = total;
            else _wasReset = true;
        }
        catch (Exception) { _wasReset = true; }
    }

    internal static byte[] Seal(long total)
    {
        var plain = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(plain, total);
        BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(8), ~total);
        return [.. Magic, .. ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)];
    }

    internal static long? Unseal(byte[] data)
    {
        if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(data[Magic.Length..], Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length != 16) return null;
            var total = BinaryPrimitives.ReadInt64LittleEndian(plain);
            return total >= 0 && BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(8)) == ~total ? total : null;
        }
        catch (CryptographicException) { return null; }
    }
}
