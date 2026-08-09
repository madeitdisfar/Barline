using Barline.Platform;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Whether to tell a first-run user to switch off Windows' own Widgets button.
/// </summary>
/// <remarks>
/// The cost is asymmetric, which is what decides the unknown case. Saying it
/// needlessly is a sentence somebody reads once and ignores. Staying quiet when the
/// button is really there means two things drawing in the same corner, which reads as
/// the app being broken rather than as a setting to change.
/// </remarks>
public class WidgetsButtonTests
{
    [Fact]
    public void Zero_is_the_only_value_that_means_hidden() =>
        Assert.False(WidgetsButton.Interpret(0));

    [Fact]
    public void One_means_visible() =>
        Assert.True(WidgetsButton.Interpret(1));

    /// <summary>
    /// Windows only writes this value once somebody changes it, so an untouched
    /// install has no value at all — and that install is the one showing the button.
    /// </summary>
    [Fact]
    public void An_absent_value_means_visible() =>
        Assert.True(WidgetsButton.Interpret(null));

    /// <summary>
    /// A value of an unexpected type is not evidence the button is gone, so it is not
    /// treated as such.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData(0L)]
    public void An_unreadable_value_means_visible(object raw) =>
        Assert.True(WidgetsButton.Interpret(raw));
}
