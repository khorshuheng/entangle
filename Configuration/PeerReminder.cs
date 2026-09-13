namespace Entangle.Configuration;

/// <summary>
/// The message shown when the service cannot start because no peer has been chosen
/// yet. The other peer's address cannot be guessed — on the intended two-machine
/// setup nothing local is right — so it is asked for rather than invented, and the
/// user is told exactly where to put it.
/// </summary>
public static class PeerReminder
{
    /// <summary>The reminder to print, naming the configuration file to edit.</summary>
    public static string Compose(string configPath) => $"""
        Entangle has no peer configured yet, so there is nothing to sync with.

        Set the other peer's address in {configPath}:

          "PeerAddress": "http://otherhost:5000"

        Each peer has to be given the other's address. On a single host, run the
        second peer on another port and point the two at each other, for example
        http://localhost:5000 and http://localhost:5001.

        The same value can be passed for one run instead:

          entangle run --Entangle:PeerAddress=http://otherhost:5000
        """;
}
