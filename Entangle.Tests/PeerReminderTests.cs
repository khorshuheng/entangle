using Entangle.Configuration;

namespace Entangle.Tests;

/// <summary>
/// The reminder shown when a peer has no peer yet: it has to name the file to edit,
/// the key, and the way out, because the address cannot be guessed for the user.
/// </summary>
public class PeerReminderTests
{
    private const string ConfigPath = "/home/user/sync-dir/appsettings.json";

    private static string Reminder => PeerReminder.Compose(ConfigPath);

    [Fact]
    public void NamesTheConfigurationFileToEdit()
    {
        Assert.Contains(ConfigPath, Reminder);
    }

    [Fact]
    public void NamesTheKeyAndShowsAnAddress()
    {
        Assert.Contains("\"PeerAddress\"", Reminder);
        Assert.Contains("http://otherhost:5000", Reminder);
    }

    [Fact]
    public void ExplainsThatBothPeersNeedEachOthersAddress()
    {
        Assert.Contains("Each peer", Reminder);
        Assert.Contains("http://localhost:5001", Reminder);
    }

    [Fact]
    public void OffersTheCommandLineAlternative()
    {
        Assert.Contains("entangle run --Entangle:PeerAddress=", Reminder);
    }

    [Fact]
    public void SaysWhyNothingCanStart()
    {
        Assert.Contains("no peer configured", Reminder);
    }
}
