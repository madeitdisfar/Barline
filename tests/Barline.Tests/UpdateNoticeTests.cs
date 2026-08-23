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
