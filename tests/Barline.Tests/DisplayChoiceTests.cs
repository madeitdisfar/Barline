using System.Runtime.InteropServices;
using Barline.Shell;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Which taskbar the widget rides when the user has asked for a particular display.
/// </summary>
/// <remarks>
/// The rule is separated from the acquiring precisely so it can be exercised without
/// a second monitor plugged in. What is being pinned is not the happy path but the
/// promise around it: the widget never ends up nowhere. Every way the preference can
/// fail to resolve has to land on a real window, because the alternative is a widget
/// that vanishes when a laptop is undocked and looks for all the world like a crash.
/// </remarks>
public class DisplayChoiceTests
{
    private static readonly IntPtr Primary = new(0x1000);
    private static readonly IntPtr Secondary = new(0x2000);
    private static readonly IntPtr Third = new(0x3000);

    private const string PrimaryId = @"\\?\DISPLAY#SDC4189#4&cb2af14&6&UID8388688#{guid}";
    private const string SecondaryId = @"\\?\DISPLAY#SAM0000#3&32c8cab7&1&UID256#{guid}";
    private const string AbsentId = @"\\?\DISPLAY#GONE0000#0&0&0&UID0#{guid}";

    private static IReadOnlyList<TaskbarWindow> Both() =>
    [
        new(Primary, PrimaryId),
        new(Secondary, SecondaryId),
    ];

    [Fact]
    public void No_preference_takes_the_primary()
    {
        Assert.Equal(Primary, Displays.Choose(Primary, Both(), preferred: null));
    }

    [Fact]
    public void An_empty_preference_is_no_preference()
    {
        // The settings file is documented as editable, so a key cleared to "" is an
        // expected input rather than a corrupt one.
        Assert.Equal(Primary, Displays.Choose(Primary, Both(), preferred: ""));
    }

    [Fact]
    public void A_chosen_secondary_wins_over_the_primary()
    {
        Assert.Equal(Secondary, Displays.Choose(Primary, Both(), SecondaryId));
    }

    [Fact]
    public void Choosing_the_primary_explicitly_still_gives_the_primary()
    {
        Assert.Equal(Primary, Displays.Choose(Primary, Both(), PrimaryId));
    }

    [Fact]
    public void A_display_that_is_not_connected_falls_back_to_the_primary()
    {
        // The undocking case, and the one this rule exists for.
        Assert.Equal(Primary, Displays.Choose(Primary, Both(), AbsentId));
    }

    [Fact]
    public void The_choice_is_matched_without_regard_to_case()
    {
        Assert.Equal(Secondary, Displays.Choose(Primary, Both(), SecondaryId.ToUpperInvariant()));
    }

    [Fact]
    public void A_taskbar_whose_display_could_not_be_identified_is_skipped()
    {
        // Identification is allowed to fail, and a null id must never match a
        // preference that happens to be null-ish further down the call.
        IReadOnlyList<TaskbarWindow> taskbars =
        [
            new(Secondary, null),
            new(Third, SecondaryId),
        ];

        Assert.Equal(Third, Displays.Choose(Primary, taskbars, SecondaryId));
    }

    [Fact]
    public void With_no_primary_the_first_taskbar_found_is_used()
    {
        // Explorer restarting: Shell_TrayWnd is gone for a moment while a secondary
        // taskbar is still up. Anything beats hiding.
        IReadOnlyList<TaskbarWindow> taskbars = [new(Secondary, SecondaryId)];

        Assert.Equal(Secondary, Displays.Choose(IntPtr.Zero, taskbars, AbsentId));
    }

    [Fact]
    public void With_nothing_at_all_the_answer_is_nothing()
    {
        Assert.Equal(IntPtr.Zero, Displays.Choose(IntPtr.Zero, [], PrimaryId));
    }

    /// <summary>
    /// The one thing about the interop that cannot be seen by reading it.
    /// </summary>
    /// <remarks>
    /// <c>DISPLAYCONFIG_MODE_INFO</c> is declared by size rather than by shape,
    /// because nothing reads its contents. If that size is wrong,
    /// <c>QueryDisplayConfig</c> writes past the array it was given, which is the kind
    /// of fault that shows up somewhere else entirely.
    /// </remarks>
    [Fact]
    public void The_mode_entry_is_the_size_the_api_expects()
    {
        Assert.Equal(64, Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_MODE_INFO>());
    }

    [Fact]
    public void The_display_config_structures_are_the_sizes_the_api_expects()
    {
        Assert.Equal(72, Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(84, Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>());
    }
}
