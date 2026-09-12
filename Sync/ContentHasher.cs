using System.Security.Cryptography;

namespace Entangle.Sync;

/// <summary>
/// SHA-256 content hashing used to detect content changes. Text files are
/// normalized (CRLF to LF) before hashing so that line-ending differences
/// between Windows and Linux peers don't cause spurious churn.
/// </summary>
public static class ContentHasher
{
    /// <summary>Hash a file's bytes, returning an uppercase hex digest.</summary>
    public static string HashFile(string path) => HashContent(File.ReadAllBytes(path));

    /// <summary>
    /// Hash in-memory content exactly as <see cref="HashFile"/> hashes the same
    /// bytes on disk. A receiver must use this (not <see cref="HashBytes"/>) when
    /// recording what it just wrote, or the stored hash would disagree with the
    /// one the scanner later computes for text files and the path would churn.
    /// </summary>
    public static string HashContent(byte[] bytes)
        => HashBytes(LooksBinary(bytes) ? bytes : NormalizeLineEndings(bytes));

    /// <summary>Hash a stream's bytes, returning an uppercase hex digest.</summary>
    public static string HashStream(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return HashBytes(buffer.ToArray());
    }

    /// <summary>SHA-256 hash of raw bytes, uppercase hex.</summary>
    public static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    /// <summary>
    /// Heuristic text/binary detection: a NUL byte in the content marks it as
    /// binary, so line endings are left alone for binary data.
    /// </summary>
    public static bool LooksBinary(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b == 0)
                return true;
        }

        return false;
    }

    /// <summary>Normalize CRLF line endings to LF.</summary>
    public static byte[] NormalizeLineEndings(byte[] bytes)
    {
        if (bytes.Length == 0)
            return bytes;

        var result = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            // Drop the '\r' of a CRLF pair, keeping a lone '\r' as-is.
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                continue;

            result.Add(bytes[i]);
        }

        return result.ToArray();
    }
}
