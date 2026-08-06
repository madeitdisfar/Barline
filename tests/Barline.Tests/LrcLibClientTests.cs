using Barline.Lyrics;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Guards what the widget tells LRCLIB about itself.
/// </summary>
/// <remarks>
/// Both of these have already been wrong at once: the agent claimed 1.0 while 1.2.0
/// was shipping, and its link pointed at a repository that did not exist. LRCLIB
/// serves timed lyrics free and asks only that clients identify themselves, so that
/// one behaving badly can be contacted instead of simply blocked — a stale version
/// and a dead link quietly remove both halves of that.
/// </remarks>
public class LrcLibClientTests
{
    [Fact]
    public void UserAgentReportsTheShippingVersion()
    {
        var version = typeof(LrcLibClient).Assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.StartsWith(
            $"Barline/{version.Major}.{version.Minor}.{version.Build} ",
            LrcLibClient.UserAgent);
    }

    [Fact]
    public void UserAgentCarriesAContactLink() =>
        Assert.Matches(@"\(https://github\.com/[^/\s]+/[^/\s)]+\)$", LrcLibClient.UserAgent);
}
