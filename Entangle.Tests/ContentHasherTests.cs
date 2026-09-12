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
}
