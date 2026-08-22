using System.Security.Cryptography;

namespace Beam.Sync;

/// <summary>SHA-256 content hashing used to detect content changes.</summary>
public static class ContentHasher
{
    /// <summary>Hash a file's bytes, returning an uppercase hex digest.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return HashStream(stream);
    }

    /// <summary>Hash a stream's bytes, returning an uppercase hex digest.</summary>
    public static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
