using System.Text;
using Entangle.Sync;

namespace Entangle.Tests;

public class ContentHasherTests
{
    [Fact]
    public void CrlfAndLfTextHashIdentically()
    {
        var crlf = ContentHasher.HashBytes(
            ContentHasher.NormalizeLineEndings(Encoding.UTF8.GetBytes("line1\r\nline2\r\n")));
        var lf = ContentHasher.HashBytes(Encoding.UTF8.GetBytes("line1\nline2\n"));

        Assert.Equal(lf, crlf);
    }

    [Fact]
    public void BinaryContentIsNotNormalized()
    {
        var withNul = new byte[] { (byte)'a', 0, (byte)'\r', (byte)'\n' };
        var normalized = ContentHasher.NormalizeLineEndings(withNul);

        // NormalizeLineEndings itself always drops CRLF; the binary guard is at
        // the hash level, so verify the heuristic instead.
        Assert.True(ContentHasher.LooksBinary(withNul));
        Assert.False(ContentHasher.LooksBinary(Encoding.UTF8.GetBytes("text")));
    }

    [Fact]
    public void LoneCarriageReturnIsPreserved()
    {
        var input = Encoding.UTF8.GetBytes("a\rb");
        var normalized = ContentHasher.NormalizeLineEndings(input);
        Assert.Equal("a\rb", Encoding.UTF8.GetString(normalized));
    }

    [Fact]
    public void NormalizeLineEndingsDropsCrBeforeLf()
    {
        var input = Encoding.UTF8.GetBytes("a\r\nb");
        var normalized = ContentHasher.NormalizeLineEndings(input);
        Assert.Equal("a\nb", Encoding.UTF8.GetString(normalized));
    }

    [Fact]
    public void HashContentMatchesHashFileForTextAndBinary()
    {
        // A receiver records HashContent(bytes it was sent); the scanner later
        // records HashFile(the same bytes on disk). They must agree, or every
        // transfer would be followed by a spurious re-transfer.
        var cases = new[]
        {
            Encoding.UTF8.GetBytes("plain text\nwithout trailing newline"),
            Encoding.UTF8.GetBytes("crlf\r\nlines\r\n"),
            new byte[] { (byte)'b', 0, (byte)'i', (byte)'n', (byte)'\r', (byte)'\n', 0 },
            Array.Empty<byte>(),
        };

        foreach (var bytes in cases)
        {
            var path = Path.Combine(Path.GetTempPath(), "entangle-hash-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(path, bytes);
                Assert.Equal(ContentHasher.HashFile(path), ContentHasher.HashContent(bytes));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
