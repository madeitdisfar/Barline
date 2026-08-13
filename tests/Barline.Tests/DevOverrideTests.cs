using Barline.Platform;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The switches that make the app easier to work on, and that a shipped build must not
/// honor.
/// </summary>
/// <remarks>
/// The one that matters is <c>BARLINE_LICENSE</c>. A user-level environment variable is
/// inherited by anything Explorer launches, so a packaged build that read it would put
/// every paid feature one <c>setx</c> away. These tests can only run unpackaged, so
/// they pin the half that is observable: that the pass-through works and has not been
/// inverted.
/// </remarks>
public class DevOverrideTests
{
    private const string Switch = "BARLINE_TEST_SWITCH";

    [Fact]
    public void A_build_from_source_reads_the_switch()
    {
        Environment.SetEnvironmentVariable(Switch, "1");

        try
        {
            // The premise of everything below. If this ever runs packaged, the
            // assertions after it are asserting the opposite of what they mean.
            Assert.False(PackageContext.IsPackaged);

            Assert.Equal("1", DevOverride.Read(Switch));
            Assert.True(DevOverride.IsSet(Switch));
            Assert.True(DevOverride.IsOn(Switch));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Switch, null);
        }
    }

    /// <summary>
    /// <c>IsOn</c> is stricter than <c>IsSet</c>, and the difference is load bearing:
    /// the settings window opens on <c>=1</c> alone, while the welcome window takes any
    /// value because one of them is the word "widgets".
    /// </summary>
    [Fact]
    public void Any_value_counts_as_set_but_only_one_counts_as_on()
    {
        Environment.SetEnvironmentVariable(Switch, "widgets");

        try
        {
            Assert.True(DevOverride.IsSet(Switch));
            Assert.False(DevOverride.IsOn(Switch));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Switch, null);
        }
    }

    [Fact]
    public void An_absent_switch_is_absent()
    {
        Assert.Null(DevOverride.Read(Switch));
        Assert.False(DevOverride.IsSet(Switch));
        Assert.False(DevOverride.IsOn(Switch));
    }
}
