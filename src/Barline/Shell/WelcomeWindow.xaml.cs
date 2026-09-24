using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Barline.Diagnostics;
using Barline.Platform;
using Barline.Settings;
using Barline.Startup;
using Barline.Ui;

namespace Barline.Shell;

/// <summary>
/// The one-time greeting. See the note in the XAML for what it is for.
/// </summary>
internal partial class WelcomeWindow : Window
{
    private readonly Theme _theme;
    private readonly BarColorResolver _bars;
    private readonly AutoStartService _autoStart;

    /// <summary>
    /// Set while the switch is being moved to match what Windows reported, so that
    /// moving it does not read as the user asking for the change all over again.
    /// </summary>
    private bool _syncing;

    /// <summary>Which page is showing: the introduction, or the setup after it.</summary>
    private Page _page = Page.Intro;

    private enum Page { Intro, Setup }

    /// <summary>
    /// The cover the sample track is showing, kept so the bar color can be resolved
    /// against it again whenever the theme moves.
    /// </summary>
    private readonly ImageSource _sampleArt;

    /// <summary>
    /// Raised when the window is closed with its own button rather than the title bar,
    /// which on the last page reads Get started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app answers it by opening the settings window. Closing this one used to
    /// leave nothing on screen at all, since the widget is hidden until something
    /// plays, so the last thing a new user saw of the app was it disappearing. The
    /// settings window is something to look around in, and it stays until they close
    /// it themselves.
    /// </para>
    /// <para>
    /// Not raised for the title bar's close button. That is someone dismissing the
    /// greeting, and answering a dismissal with another window is not listening.
    /// </para>
    /// </remarks>
    public event EventHandler? GetStarted;

    public WelcomeWindow(Theme theme, SettingsStore settings, AutoStartService autoStart)
    {
        _theme = theme;
        _autoStart = autoStart;

        // Its own resolver, for the same reason the settings preview has one: two
        // controls sharing an animated brush fight over it.
        _bars = new BarColorResolver(theme, settings);

        InitializeComponent();

        _sampleArt = DemoContent.CreateArt();
        SampleArt.Source = _sampleArt;
        SampleTitle.Text = "Everything In Its Right Place";
        SampleArtist.Text = "Radiohead";

        SampleBars.BarBrush = _bars.Brush;
        SampleBars.BarCount = settings.Current.VisualizerBarCount;

        // No LevelSource, so the bars run on their own decorative motion rather than
        // opening an audio capture for the sake of a picture.
        SampleBars.IsActive = true;

        // The override exists because the notice is invisible on any machine set up
        // the way this one asks you to set it up, so the branch could otherwise only
        // be looked at by changing a Windows setting to inspect a window.
        // Pointing at the wrong end of the taskbar is worse than not pointing at all,
        // and the reader is looking at their own taskbar while they read this.
        if (TaskbarAlignment.IsLeft())
        {
            WhereItLives.Text =
                "It lives at the right end of your taskbar, beside the clock, " +
                "and looks like this.";
        }

        // Unconditional, because the button moves with the widget. Aligning the
        // taskbar left sends Windows' own Widgets button to the far end, which is the
        // end the widget crosses to, so the two land on each other there exactly as
        // they do at the left end of a centered taskbar.
        bool widgetsShowing =
            WidgetsButton.IsVisible() ||
            DevOverride.Read("BARLINE_WELCOME") == "widgets";

        if (widgetsShowing)
        {
            WidgetsNotice.Visibility = Visibility.Visible;
            TaskbarSettingsButton.Click += (_, _) => OpenTaskbarSettings();
        }

        NextButton.Click += (_, _) =>
        {
            if (_page == Page.Intro)
            {
                ShowPage(Page.Setup, animate: true);
                return;
            }

            // Closed first, so the settings window opens onto a desktop this one has
            // already left and takes the focus rather than sharing it.
            Close();
            GetStarted?.Invoke(this, EventArgs.Empty);
        };

        BackButton.Click += (_, _) => ShowPage(Page.Intro, animate: true);

        // The second page starts one pager-width to the right, which is only known
        // once the window has been laid out. Only the width matters: the height is
        // settled by the taller page, and the window never changes it afterwards.
        Pager.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) ShowPage(_page, animate: false);
        };

        AutoStartToggle.Checked += (_, _) => OnAutoStartToggled(true);
        AutoStartToggle.Unchecked += (_, _) => OnAutoStartToggled(false);

        // Read rather than assumed off. Somebody reinstalling may have left it on, and
        // a switch that disagrees with Windows is worse than no switch.
        _ = RefreshAutoStartAsync();

        ApplyTheme();
        _theme.Changed += OnThemeChanged;

        Closed += (_, _) =>
        {
            _theme.Changed -= OnThemeChanged;

            // Stops the render-loop subscription behind the animation. Left running,
            // it would keep a closed window's control ticking for the life of the app.
            SampleBars.IsActive = false;
        };
    }

    /// <summary>
    /// Brings a page into view, sliding the other one out the way it would go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both pages move, rather than one sliding over the other, so what is on screen
    /// always reads as one strip being pulled sideways: forward to the left, back to
    /// the right. The curve is the app's shared one; the duration is longer than the
    /// usual quarter second because the distance is the whole window, and at the
    /// normal speed the text crosses it too fast to be seen going.
    /// </para>
    /// <para>
    /// A page that has left is hidden once it is off screen, so the keyboard cannot
    /// tab into controls nobody can see. Hidden rather than collapsed, since it still
    /// holds the window at the taller page's height.
    /// </para>
    /// </remarks>
    private void ShowPage(Page page, bool animate)
    {
        _page = page;
        bool setup = page == Page.Setup;
        double width = Pager.ActualWidth;

        BackButton.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = setup ? "Get started" : "Next";
        IntroDot.Opacity = setup ? DotDim : DotLit;
        SetupDot.Opacity = setup ? DotLit : DotDim;

        double introTo = setup ? -width : 0d;
        double setupTo = setup ? 0d : width;

        // Honors the Windows setting for turning animations off, which is also the
        // only kind of slide nobody is inconvenienced by skipping.
        if (!animate || width <= 0d || !SystemParameters.ClientAreaAnimation)
        {
            Place(IntroShift, introTo);
            Place(SetupShift, setupTo);
            HideTheOtherPage();
            return;
        }

        IntroPage.Visibility = Visibility.Visible;
        SetupPage.Visibility = Visibility.Visible;

        Move(IntroShift, introTo, null);
        Move(SetupShift, setupTo, HideTheOtherPage);
    }

    private const double DotLit = 0.9d;
    private const double DotDim = 0.3d;

    private void HideTheOtherPage()
    {
        IntroPage.Visibility = _page == Page.Intro ? Visibility.Visible : Visibility.Hidden;
        SetupPage.Visibility = _page == Page.Setup ? Visibility.Visible : Visibility.Hidden;
    }

    private static void Place(TranslateTransform shift, double x)
    {
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = x;
    }

    private void Move(TranslateTransform shift, double to, Action? done)
    {
        var move = new DoubleAnimationUsingKeyFrames();

        // One frame, so it sets off from wherever the page is now. A second click while
        // the first swipe is still under way turns it around mid-flight rather than
        // snapping it to either end first.
        move.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.PageMs)),
            Motion.Standard));

        move.FillBehavior = FillBehavior.HoldEnd;

        if (done is not null)
        {
            // Only if the swipe that finished is still the one wanted. Back pressed
            // part way through starts a new one, and the old one finishing must not
            // hide the page that is now on its way in.
            var target = _page;
            move.Completed += (_, _) =>
            {
                if (_page == target) done();
            };
        }

        shift.BeginAnimation(TranslateTransform.XProperty, move);
    }

    private async void OnAutoStartToggled(bool enabled)
    {
        if (_syncing) return;

        // Set from what Windows did rather than what was asked, which for a packaged
        // app are not always the same. See AutoStartService.
        ApplyAutoStartState(await _autoStart.SetEnabledAsync(enabled));
    }

    private async Task RefreshAutoStartAsync() =>
        ApplyAutoStartState(await _autoStart.GetStateAsync());

    private void ApplyAutoStartState(AutoStartState state)
    {
        bool previous = _syncing;
        _syncing = true;
        try { AutoStartToggle.IsChecked = state == AutoStartState.Enabled; }
        finally { _syncing = previous; }

        string note = AutoStartService.Describe(state);
        AutoStartNote.Text = note;
        AutoStartNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenTaskbarSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WidgetsButton.SettingsUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DebugLog.Write($"welcome: could not open taskbar settings: {ex.Message}");

            // The button is a shortcut, not the only route, so a failure demotes it to
            // the instructions rather than leaving a control that does nothing.
            TaskbarSettingsButton.IsEnabled = false;
            TaskbarSettingsButton.Content = "Settings, Personalization, Taskbar";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowChrome();
    }

    /// <summary>
    /// Paints the title bar to match. Without it a dark window wears a light caption,
    /// which is the first thing that says an app is not part of the system.
    /// </summary>
    private void ApplyWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var caption = (_theme.WindowBackground as SolidColorBrush)?.Color ?? Colors.Black;
        TitleBarTheme.Apply(handle, _theme.IsLight, caption);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyWindowChrome();
    }

    private void ApplyTheme()
    {
        Resources["WindowBackgroundBrush"] = _theme.WindowBackground;
        Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        Resources["TextSecondaryBrush"] = _theme.TextSecondary;
        Resources["TextTertiaryBrush"] = _theme.TextTertiary;
        Resources["CardBackgroundBrush"] = _theme.CardBackground;
        Resources["CardStrokeBrush"] = _theme.CardStroke;
        Resources["ControlAltFillBrush"] = _theme.ControlAltFill;
        Resources["ControlStrongStrokeBrush"] = _theme.ControlStrongStroke;
        Resources["AccentFillBrush"] = _theme.AccentFill;
        Resources["TextOnAccentBrush"] = _theme.TextOnAccent;
        Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        Resources["SubtlePressedBrush"] = _theme.SubtlePressed;

        // The sample sits on the shade the taskbar actually is, which is the same
        // value the bar contrast correction measures against. Anything else would
        // show colors the widget will not produce.
        var strip = new SolidColorBrush(_theme.BackdropEstimate);
        strip.Freeze();
        SampleStrip.Background = strip;

        // The resolver starts on white and stays there until it is asked, so without
        // this the sample drew white bars on the light taskbar shade, which is the one
        // combination the widget itself will never produce. Asked here rather than in
        // the constructor because every mode is measured against the taskbar material,
        // so the answer changes with the theme.
        _bars.Update(_sampleArt);

        // The widget draws its text over the taskbar, not over this window, so the
        // sample's own labels follow the taskbar's shade rather than the window's.
        var onStrip = _theme.IsLight ? Brushes.Black : Brushes.White;
        SampleTitle.Foreground = onStrip;
        SampleArtist.Foreground = onStrip.Clone();
        SampleArtist.Opacity = 0.72d;
    }
}
