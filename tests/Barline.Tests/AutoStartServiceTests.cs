using Barline.Startup;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Covers the branch that decides how the widget registers itself to start.
/// </summary>
/// <remarks>
/// The consequence of getting this wrong is silent either way round. Detected as
/// packaged when it is not, the app asks for a startup task that no manifest
/// declares and never starts; detected as unpackaged when it is not, it writes a Run
/// key that Windows ignores for packaged apps, reporting success and doing nothing.
/// The test host is an ordinary unpackaged process, which is the case that has to
/// hold today — and running it at all proves the interop marshals.
/// </remarks>
public class AutoStartServiceTests
{
    [Fact]
    public void DetectsAnUnpackagedProcess() =>
        Assert.False(new AutoStartService().IsPackaged);

    /// <summary>
    /// Unpackaged, reading the state must not throw or report the query as having
    /// failed — <see cref="AutoStartState.Unavailable"/> is reserved for Windows
    /// declining to answer, and would show the user a warning that is not true.
    /// </summary>
    [Fact]
    public async Task ReportsAConcreteStateWhenUnpackaged()
    {
        var state = await new AutoStartService().GetStateAsync();

        Assert.True(
            state is AutoStartState.Enabled or AutoStartState.Disabled,
            $"expected a definite state when unpackaged, got {state}");
    }
}
