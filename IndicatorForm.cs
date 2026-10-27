using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace MicMute;

/// <summary>
/// Always-on mute indicator: the mic icon (or a small antialiased dot) that stays above the
/// foreground app (games in borderless / fullscreen-optimised mode included).
///
/// - Click-through and non-activating, so it never steals focus from a game.
/// - Hidden while the mic is live unless Indicator_ShowWhenLive=1.
/// - Edit mode (hotkey or tray menu) turns off click-through so the dot can be
///   dragged; leaving edit mode stores the position for the executable that
///   was in the foreground, so every game gets its own spot clear of its HUD.
/// - Polls the foreground window once a second to swap per-game positions and
///   re-assert HWND_TOPMOST (some games push themselves above topmost windows).
///
/// Rendered with UpdateLayeredWindow (per-pixel alpha) instead of WinForms
/// painting — a Region-clipped form gives a jagged circle at this size.
/// </summary>
internal sealed class IndicatorForm : Form
{
    private static readonly Color s_mutedColor = Color.FromArgb(0xFF, 0x2D, 0x2D);
    private static readonly Color s_liveColor = Color.FromArgb(0x34, 0xD3, 0x6B);
    private static readonly Color s_editRingColor = Color.FromArgb(0xFF, 0xFF, 0xFF);

    private readonly Config _config;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private bool _muted;
    private bool _editMode;
    // _profile = whose saved position is on screen; _fgProfile = the app that
    // is actually in the foreground. They differ when the user clicks into an
    // app without its own position (desktop, browser on the 2nd monitor…) —
    // the dot then stays where the game put it instead of jumping away.
    private string _profile = Config.IndicatorDefaultProfile;
    private string _fgProfile = Config.IndicatorDefaultProfile;
    private string _editProfile;
    private Point _pos;
    private bool _dragging;
    private Point _dragOffset;
    private bool _disposed;

    // Full-resolution icons for Indicator_Style=icon (reloaded on ApplyConfig).
    private Icon _iconMuted;
    private Icon _iconLive;

    // Cache pid → exe name; the foreground pid changes rarely.
    private uint _cachedPid;
    private string _cachedExe;

    public bool EditMode => _editMode;

    /// <summary>Profile key of the foreground app (target of move / reset).</summary>
    public string CurrentProfile => _fgProfile;

    /// <summary>Profile the last edit-mode session saved its position under.</summary>
    public string LastSavedProfile { get; private set; }

    public IndicatorForm(Config config)
    {
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += OnPoll;

        LoadIcons();
        _ = Handle;
        _pos = ResolvePosition(_profile);
        _pollTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST |
                          NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED |
                          NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    // ── Public API ───────────────────────────────────────────────────────

    public void SetMuted(bool muted)
    {
        _muted = muted;
        Render();
        UpdateVisibility();
    }

    /// <summary>Re-read enabled/size/style/show-when-live (and custom icons) from config.</summary>
    public void ApplyConfig()
    {
        if (!_config.IndicatorEnabled && _editMode)
            ExitEditMode(save: false);
        LoadIcons();
        Render();
        UpdateVisibility();
    }

    public void ToggleEditMode()
    {
        if (_editMode)
            ExitEditMode(save: true);
        else
            EnterEditMode();
    }

    /// <summary>Drop the stored position for the current app (falls back to the default spot).</summary>
    public void ResetCurrentProfile()
    {
        string key = _editMode ? _editProfile : _fgProfile;
        if (_config.IndicatorPositions.Remove(key))
            _config.Save();
        if (_editMode || string.Equals(key, _profile, StringComparison.OrdinalIgnoreCase))
        {
            _profile = Config.IndicatorDefaultProfile;
            _pos = ResolvePosition(_profile);
            Render();
        }
    }

    // ── Edit mode ────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (!_config.IndicatorEnabled) return;
        _editMode = true;
        // Edit for the app in the foreground; on the desktop that's "*", the global default.
        _editProfile = _fgProfile;
        SetClickThrough(false);
        Render();
        UpdateVisibility();
    }

    private void ExitEditMode(bool save)
    {
        _editMode = false;
        _dragging = false;
        Capture = false;
        SetClickThrough(true);
        if (save)
        {
            _config.IndicatorPositions[_editProfile] = _pos;
            // First ever save also seeds the default, so the dot doesn't jump
            // back to the factory spot in every other app.
            _config.IndicatorPositions.TryAdd(Config.IndicatorDefaultProfile, _pos);
            LastSavedProfile = _editProfile;
            _profile = _editProfile;
            if (!_config.Save())
                Log.Warn("Indicator: position couldn't be saved");
        }
        _editProfile = null;
        Render();
        UpdateVisibility();
    }

    private void SetClickThrough(bool on)
    {
        nint ex = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        ex = on ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~(nint)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, ex);
    }

    protected override void WndProc(ref Message m)
    {
        // Clicking the dot in edit mode must not pull focus from the game.
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_editMode) return;
        if (e.Button == MouseButtons.Right)
        {
            // Right-click = done (same as pressing the edit hotkey again).
            ExitEditMode(save: true);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragOffset = new Point(Cursor.Position.X - _pos.X, Cursor.Position.Y - _pos.Y);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var cur = Cursor.Position;
        _pos = new Point(cur.X - _dragOffset.X, cur.Y - _dragOffset.Y);
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    // ── Foreground tracking ──────────────────────────────────────────────

    private void OnPoll(object sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            // While editing, stay pinned to the app we started editing for —
            // dragging may briefly change the foreground (e.g. tray menu).
            if (!_editMode)
            {
                string profile = DetectForegroundProfile();
                if (profile != null)
                {
                    _fgProfile = profile;
                    // Only apps with their own saved position move the dot.
                    // Everything else (desktop, apps without a position) leaves
                    // it where it is, so alt-tabbing out of a game or clicking
                    // the 2nd monitor doesn't drag it off the game's spot.
                    if (profile != Config.IndicatorDefaultProfile &&
                        _config.IndicatorPositions.ContainsKey(profile) &&
                        !string.Equals(profile, _profile, StringComparison.OrdinalIgnoreCase))
                    {
                        _profile = profile;
                        _pos = ResolvePosition(profile);
                        Render();
                    }
                }
            }

            if (NativeMethods.IsWindowVisible(Handle))
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Warn("Indicator poll failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns the lowercase exe name of the foreground window, the default
    /// profile for the desktop/shell, or null when the foreground is MicMute
    /// itself or the taskbar (tray menu open) — keep the previous profile then.
    /// </summary>
    private string DetectForegroundProfile()
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return null;

        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        string className = cls.ToString();
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass")
            return null;
        if (className is "Progman" or "WorkerW")
            return Config.IndicatorDefaultProfile;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownPid) return null;

        if (pid != _cachedPid)
        {
            _cachedPid = pid;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                _cachedExe = proc.ProcessName.ToLowerInvariant() + ".exe";
            }
            catch
            {
                _cachedExe = null;
            }
        }
        return _cachedExe ?? Config.IndicatorDefaultProfile;
    }

    private Point ResolvePosition(string profile)
    {
        if (!_config.IndicatorPositions.TryGetValue(profile, out var p) &&
            !_config.IndicatorPositions.TryGetValue(Config.IndicatorDefaultProfile, out p))
        {
            // Default: left edge, vertically centred on the primary screen —
            // the screen edge is the area game HUDs use least.
            var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
            p = new Point(wa.Left + 6, wa.Top + wa.Height / 2);
        }

        // Monitor unplugged / resolution changed: pull the dot back on screen.
        int size = DotPixels();
        var screen = Screen.FromPoint(p).Bounds;
        if (!screen.Contains(p))
            p = new Point(screen.Left + 6, screen.Top + screen.Height / 2);
        p.X = Math.Clamp(p.X, screen.Left, screen.Right - size);
        p.Y = Math.Clamp(p.Y, screen.Top, screen.Bottom - size);
        return p;
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void UpdateVisibility()
    {
        bool want = _config.IndicatorEnabled && (_muted || _config.IndicatorShowWhenLive || _editMode);
        bool shown = NativeMethods.IsWindowVisible(Handle);
        if (want && !shown)
        {
            Render();
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        else if (!want && shown)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        }
    }

    private bool UseIcon => _config.IndicatorStyle == "icon";

    private void LoadIcons()
    {
        _iconMuted?.Dispose();
        _iconLive?.Dispose();
        _iconMuted = TrayApp.LoadIconRaw(_config.IconMuted, "mic_off.ico", out _);
        _iconLive = TrayApp.LoadIconRaw(_config.IconActive, "mic_on.ico", out _);
    }

    // An icon needs more pixels than a dot to stay recognisable, so it gets a 24px floor.
    private int DotPixels() => Math.Max(4, (int)Math.Round(
        (UseIcon ? Math.Max(24, _config.IndicatorSize) : _config.IndicatorSize) * DeviceDpi / 96.0));

    private void Render()
    {
        if (_disposed || !IsHandleCreated) return;

        int dot = DotPixels();
        // Room for the dark halo, plus the edit-mode ring outside it.
        int halo = Math.Max(3, dot / 4);
        int editRing = _editMode ? Math.Max(3, dot / 5) : 0;
        int pad = halo + editRing;
        int size = dot + pad * 2;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = _muted ? s_mutedColor : s_liveColor;
            if (_editMode)
            {
                float w = Math.Max(2f, editRing * 0.7f);
                using var ring = new Pen(s_editRingColor, w);
                g.DrawEllipse(ring, w / 2, w / 2, size - w, size - w);
            }

            // Soft dark halo: concentric fills with fading alpha — makes the dot
            // pop on bright scenes (snow, sky, white UI).
            for (int i = halo; i >= 1; i--)
            {
                int a = 70 * (halo - i + 1) / halo;
                using var shadow = new SolidBrush(Color.FromArgb(a, 0, 0, 0));
                g.FillEllipse(shadow, pad - i, pad - i, dot + i * 2, dot + i * 2);
            }

            var icon = _muted ? _iconMuted : _iconLive;
            if (UseIcon && icon != null)
            {
                // Dark backplate behind the icon so it reads on bright scenes.
                using (var plate = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.FillEllipse(plate, pad, pad, dot, dot);
                int inset = Math.Max(1, dot / 8);
                int iconPx = dot - inset * 2;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // new Icon(icon, size) picks the closest frame in the .ico.
                using var sized = new Icon(icon, iconPx, iconPx);
                using var img = sized.ToBitmap();
                g.DrawImage(img, new Rectangle(pad + inset, pad + inset, iconPx, iconPx));
            }
            else
            {
                using (var fill = new SolidBrush(color))
                    g.FillEllipse(fill, pad, pad, dot, dot);

                // White rim keeps it readable on dark and red scenes.
                float rim = Math.Max(1.5f, dot / 10f);
                using (var outline = new Pen(Color.FromArgb(235, 255, 255, 255), rim))
                    g.DrawEllipse(outline, pad + rim / 2, pad + rim / 2, dot - rim, dot - rim);
            }
        }

        nint screenDc = NativeMethods.GetDC(0);
        nint memDc = NativeMethods.CreateCompatibleDC(screenDc);
        nint hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        nint oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
        try
        {
            // _pos is the dot's top-left; shift the window by the padding.
            var dst = new NativeMethods.POINT { X = _pos.X - pad, Y = _pos.Y - pad };
            var sz = new NativeMethods.SIZE { Cx = size, Cy = size };
            var src = new NativeMethods.POINT();
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _iconMuted?.Dispose();
            _iconLive?.Dispose();
        }
        base.Dispose(disposing);
    }
}
