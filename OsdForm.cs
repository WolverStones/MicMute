namespace MicMute;

/// <summary>
/// Discreet borderless OSD pinned above the system tray. Click-through,
/// no-activate, auto-dismiss, cached GDI resources.
///
/// v2 palette ported from MWBToggle — softened greys, regular (not semibold)
/// weight, muted accent dots, tighter padding, lower opacity. Same positioning
/// logic as before: Shell_TrayWnd anchoring when it lands inside the working
/// area, working-area corner fallback for top/left/right taskbars.
/// </summary>
internal sealed class OsdForm : Form
{
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private bool _disposed;

    // Cached GDI resources — created once, reused across the app lifetime.
    // BG + text follow the user's Theme pin (restart-to-apply, captured at
    // first class load). Dot colours stay semantic — vivid muted-red and
    // muted-green that read fine against both Mocha-dark and Latte-light
    // backgrounds without per-theme tuning. Theme.Initialize MUST fire in
    // TrayApp's ctor before `new OsdForm()` is called (it does — line 87).
    private static readonly Font s_labelFont = new("Segoe UI", 9f);
    private static readonly SolidBrush s_bgBrush = new(Theme.BgColor);
    private static readonly SolidBrush s_textBrush = new(Theme.FgColor);
    private static readonly SolidBrush s_mutedDotBrush = new(Color.FromArgb(0xCC, 0x5A, 0x5A));  // muted red
    private static readonly SolidBrush s_activeDotBrush = new(Color.FromArgb(0x4C, 0xB8, 0x74)); // muted green

    private static readonly string s_mutedLabel = "Mic Muted";
    private static readonly string s_activeLabel = "Mic Active";
    // Dot is drawn via FillEllipse for deterministic pixel placement —
    // DrawString(U+25CF) shifts vertically with font metrics and ends up
    // 1-2px below the letter baseline.
    private const int DotSize = 8;

    private bool _showMuted;
    private string _customText;
    // Cached label measurement from the most recent ShowInternal() call.
    // OnPaint uses .Height to vertically centre the text against the pill,
    // which itself grows DPI-aware via Math.Max(28, _labelSize.Height + 10).
    // Re-measured each Show* so it tracks the current displayText + DPI.
    private Size _labelSize;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // No `TopMost = true`: WinForms ignores ShowWithoutActivation on TopMost
        // forms, so Show() stole focus from the foreground game — fullscreen
        // games flicker on focus loss and then cover the OSD. Topmost comes from
        // WS_EX_TOPMOST in CreateParams + SetWindowPos(HWND_TOPMOST) in ShowInternal.
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.BgColor;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        // Set once: changing Opacity on every show toggles WS_EX_LAYERED at
        // runtime, which itself causes a visible blink.
        Opacity = 215.0 / 255.0;

        _dismissTimer = new System.Windows.Forms.Timer();
        _dismissTimer.Tick += (_, _) =>
        {
            // A5-F13: check _disposed before touching the timer — a late Tick that
            // fires after Dispose() would otherwise hit an ObjectDisposedException.
            if (_disposed) return;
            _dismissTimer.Stop();
            HideNative();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — no taskbar entry
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT — click-through
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE — never take focus from the game
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Show the OSD with standard mute/active state display.</summary>
    public void ShowOsd(bool muted, int durationMs)
    {
        _showMuted = muted;
        _customText = null;
        ShowInternal(muted ? s_mutedLabel : s_activeLabel, durationMs);
    }

    /// <summary>Show the OSD with custom notification text.</summary>
    public void ShowNotification(string text, bool isMuted, int durationMs)
    {
        _showMuted = isMuted;
        _customText = text;
        ShowInternal(text, durationMs);
    }

    /// <summary>
    /// Show the OSD until explicitly hidden. Used for sticky PTT "mic listening"
    /// where the user needs a persistent indicator that the mic is hot.
    /// </summary>
    public void ShowPersistent(string text, bool isMuted)
    {
        _showMuted = isMuted;
        _customText = text;
        ShowInternal(text, -1);
    }

    /// <summary>Hide a persistent OSD. Safe to call when not shown.</summary>
    public void HidePersistent()
    {
        if (_disposed || IsDisposed) return;
        _dismissTimer.Stop();
        HideNative();
    }

    // Show/hide via Win32 directly — Form.Show() activates the window even with
    // ShowWithoutActivation in some paths, and Visible-state bookkeeping isn't
    // needed for a click-through overlay.
    private bool IsShownNative => IsHandleCreated && NativeMethods.IsWindowVisible(Handle);

    private void ShowNative()
    {
        if (!IsShownNative)
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        // Re-assert topmost on every show — games may have pushed themselves above us.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void HideNative()
    {
        if (IsShownNative)
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    private void ShowInternal(string displayText, int durationMs)
    {
        // Defensive: any failure in measurement/positioning should not kill
        // the OSD pipeline or propagate to the caller (a hotkey handler).
        try
        {
            if (_disposed || IsDisposed) return;
            if (string.IsNullOrWhiteSpace(displayText)) return;

            // Force the handle so DeviceDpi (and thus LogicalToDeviceUnits below) is the
            // real monitor scale on the very first toast — otherwise a pre-handle measure
            // falls back to 96 and the pill renders undersized at 125%/150% until reshown.
            if (!IsHandleCreated) { _ = Handle; }

            // A5-F10: use TextRenderer.MeasureText instead of CreateGraphics()+MeasureString.
            // CreateGraphics() allocates a short-lived GDI DC that can be missed by the
            // using-block if the form handle isn't created yet (ISR window).  TextRenderer
            // is handle-independent and consistent across DPI contexts.  The paint path
            // retains DrawString (GDI+); the 1-2 px kerning difference is negligible for
            // a short pill label — accepted per design review.
            {
                _labelSize = TextRenderer.MeasureText(displayText, s_labelFont);
                // Paddings scale with DPI; _labelSize is already device-correct (MeasureText
                // reads the current DC). 34 = left pad 10 + dot gutter 12 + right pad 12.
                int w = LogicalToDeviceUnits(34) + _labelSize.Width;
                // Pill height must clear the rendered text + padding at any
                // display scale. At 100% scale 9pt Segoe UI is ~15px tall and
                // 28px gave generous breathing room; at 175% scale it renders
                // ~26px and the hardcoded 28 clipped descenders. _labelSize is
                // already DPI-correct (TextRenderer measures at the current
                // DC), so deriving height from it keeps the pill proportional
                // across every monitor scale. Floor at 28 to preserve the
                // intended visual weight at 100%.
                int h = Math.Max(LogicalToDeviceUnits(28), _labelSize.Height + LogicalToDeviceUnits(10));

                // Default anchor: bottom-right corner of the working area.
                // WorkingArea already excludes the taskbar regardless of its
                // edge (top/left/right/bottom), so this is safe for every
                // taskbar orientation.
                var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                var workArea = screen.WorkingArea;
                int xPos = workArea.Right - w - LogicalToDeviceUnits(12);
                int yPos = workArea.Bottom - h - LogicalToDeviceUnits(8);

                // Try precise Shell_TrayWnd anchoring; accept only if it
                // stays inside the working area. Top/left/right taskbars
                // place the naive anchor off-screen — the bounds check
                // rejects those and the working-area fallback wins.
                nint trayHwnd = NativeMethods.FindWindow("Shell_TrayWnd", "");
                if (trayHwnd != 0 &&
                    NativeMethods.GetWindowRect(trayHwnd, out var rect))
                {
                    int anchoredX = rect.Right - w - LogicalToDeviceUnits(12);
                    int anchoredY = rect.Top - h - LogicalToDeviceUnits(8);
                    if (anchoredY >= workArea.Top &&
                        anchoredX >= workArea.Left &&
                        anchoredX + w <= workArea.Right)
                    {
                        xPos = anchoredX;
                        yPos = anchoredY;
                    }
                }

                SetBounds(xPos, yPos, w, h);
            }

            // Win11 rounded corners — returns HRESULT on older Windows
            // without throwing. Enhancement, not a requirement.
            int preference = NativeMethods.DWMWCP_ROUND;
            _ = NativeMethods.DwmSetWindowAttribute(Handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, sizeof(int));

            Invalidate();

            ShowNative();

            // durationMs <= 0 is the "persistent" sentinel — no auto-dismiss.
            // Caller owns Hide() via HidePersistent().
            _dismissTimer.Stop();
            if (durationMs > 0)
            {
                _dismissTimer.Interval = Math.Max(500, durationMs);
                _dismissTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("OsdForm.ShowInternal failed: " + ex.Message);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.FillRectangle(s_bgBrush, ClientRectangle);

        // Centre the dot vertically against the 28px pill. SmoothingMode
        // antialiases the ellipse so it doesn't look like a chunky square.
        var prev = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var dotBrush = _showMuted ? s_mutedDotBrush : s_activeDotBrush;
        // Owner-draw paint geometry runs in device pixels and is never auto-scaled —
        // scale the dot size + inset by DPI so it stays proportional at 125%/150%.
        int dot = LogicalToDeviceUnits(DotSize);
        int dotY = (ClientSize.Height - dot) / 2;
        g.FillEllipse(dotBrush, LogicalToDeviceUnits(11), dotY, dot, dot);
        g.SmoothingMode = prev;

        string label = _customText ?? (_showMuted ? s_mutedLabel : s_activeLabel);
        // Vertically centre text against the pill — pill height is DPI-aware
        // (Math.Max(28, _labelSize.Height + 10)), so a fixed top-padding like
        // the previous y=5 left the text top-aligned at higher scales. Use the
        // cached label height to centre regardless of current pill height.
        int textY = Math.Max(0, (ClientSize.Height - _labelSize.Height) / 2);
        g.DrawString(label, s_labelFont, s_textBrush, LogicalToDeviceUnits(24), textY);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _dismissTimer.Stop();
            _dismissTimer.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
