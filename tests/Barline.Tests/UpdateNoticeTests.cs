using Barline.Platform;
using Barline.Tray;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// How a waiting update is worded in the tray menu.
/// </summary>
/// <remarks>
/// The Store decides when there is an update and cannot be made to say so on demand,
/// so nothing above this line can be tested here. What can is the convention the rest
/// of it rests on: an empty version means an update whose number could not be read,
/// which is still an update and still worth an item.
/// </remarks>
public class UpdateNoticeTests
{
    [Fact]
    public void A_known_version_is_named()
    {
        Assert.Equal("Update to 2.2.0", TrayMenu.UpdateLabel("2.2.0"));
    }

    [Fact]
    public void An_unknown_version_still_offers_the_update()
    {
        Assert.Equal("Update Barline", TrayMenu.UpdateLabel(string.Empty));
    }
}

/// <summary>
/// Turning the Store's one progress figure into the two waits a person sees.
/// </summary>
/// <remarks>
/// The split is documented rather than observed: <c>PackageDownloadProgress</c> runs
/// 0 to 0.8 while the package downloads and 0.8 to 1 while it installs, with one of
/// the OS's own dialogs in between. Mapped straight through, a bar would stop at 80%
/// to ask a question, which reads as stuck.
/// </remarks>
public class UpdateProgressTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.4, 0.5)]
    [InlineData(0.79, 0.9875)]
    public void The_first_four_fifths_are_the_download(double far, double shown)
    {
        var step = StoreUpdates.Describe(far);

        Assert.False(step.Installing);
        Assert.Equal(shown, step.Fraction, 4);
    }

    [Theory]
    [InlineData(0.8, 0.0)]
    [InlineData(0.9, 0.5)]
    [InlineData(1.0, 1.0)]
    public void The_last_fifth_is_the_install(double far, double shown)
    {
        var step = StoreUpdates.Describe(far);

        Assert.True(step.Installing);
        Assert.Equal(shown, step.Fraction, 4);
    }
}

/// <summary>
/// Noticing that this run follows an update.
/// </summary>
/// <remarks>
/// The card this decides exists to answer a disappearance: installing closes the app,
/// so the widget goes away and comes back as a different version. Which makes the
/// cases that are not that worth stating, since each of them would put a notice in
/// front of somebody who watched nothing happen.
/// </remarks>
public class VersionChangeTests
{
    [Fact]
    public void A_higher_version_is_an_update()
    {
        Assert.True(VersionChange.IsUpdate("2.1.1", "2.2.0"));
    }

    [Fact]
    public void A_first_run_is_not_one()
    {
        // Nothing to have updated from, and the welcome window is already talking.
        Assert.False(VersionChange.IsUpdate(null, "2.2.0"));
    }

    [Fact]
    public void The_same_version_is_not_one()
    {
        Assert.False(VersionChange.IsUpdate("2.2.0", "2.2.0"));
    }

    [Fact]
    public void Going_back_is_not_one()
    {
        // A rolled-back build is somebody's own doing, and not news to them.
        Assert.False(VersionChange.IsUpdate("2.3.0", "2.2.0"));
    }

    [Fact]
    public void Something_that_will_not_parse_is_not_compared()
    {
        Assert.False(VersionChange.IsUpdate("who knows", "2.2.0"));
    }
}
