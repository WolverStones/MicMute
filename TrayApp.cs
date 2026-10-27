namespace MicMute;

/// <summary>
/// Main application form — runs as a hidden window with a system tray icon.
/// Handles global hotkeys, mute state management, periodic sync, and all UI.
/// </summary>
internal sealed class TrayApp : Form
{
    private const int HOTKEY_ID_MAIN = 1;
    private const int HOTKEY_ID_DEAFEN = 2;
    private const int HOTKEY_ID_INDICATOR_EDIT = 3;

    private readonly Config _config;
    private readonly AudioManager _audio;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly System.Windows.Forms.Timer _syncTimer;
    private readonly OsdForm _osdForm;
    private readonly IndicatorForm _indicator;

    // Cached icons — loaded once at startup, reloaded on settings change
    private Icon _iconActive;
    private Icon _iconMuted;
    // State
    private bool _muted;
    private bool _deafened;
    private bool _speakerWasMuted;
    private bool _lockDebounce;
    private bool _flashing;
    private int _flashCount;
    private readonly System.Windows.Forms.Timer _flashTimer;

    // Explorer restart detection
    private readonly uint _wmTaskbarCreated;

    // Cached tooltip strings — only rebuilt on state change
    private string _cachedTooltipMuted = "";
    private string _cachedTooltipActive = "";
    private bool _tooltipDirty = true;

    // Track registered hotkeys for cleanup
    private bool _mainHotkeyRegistered;
    private bool _deafenHotkeyRegistered;
    private bool _indicatorHotkeyRegistered;

    // Singleton dialogs
    private SettingsDialog _settingsDialog;

    // Device menu lazy loading
    private ToolStripMenuItem _deviceMenuItem;
    private ToolStripMenuItem _indicatorResetItem;
    private bool _deviceMenuPopulated;

    // Reusable bold font for menu title (disposed in cleanup)
    private Font _menuTitleFont;

    // Themed renderer for the tray context menu (and its submenu drop-downs).
    // Created once in the ctor after Theme.Initialize fires so its static GDI
    // brush cache captures the right palette.
    private MenuRenderer _menuRenderer;

    // One-shot set — we log a custom-sound failure once per file path so a
    // broken MuteSound doesn't spam the log on every toggle.
    private readonly HashSet<string> _playSoundFailed = new(StringComparer.OrdinalIgnoreCase);

    // One-shot: notify the user the first time a custom icon fails to load.
    // Mirrors _playSoundFailed pattern.
    private bool _customIconFailNotified;
    private bool _pendingCustomIconToast;

    // Consecutive OnSyncTick failure tracking for A3-F16 / A3-F17.
    private int _syncConsecutiveFails;
    private bool _syncFailTooltipShown;
    private int _muteLockFightBackFails;
    private bool _muteLockFightBackTooltipShown;

    private bool _disposed;
    private bool _shuttingDown;

    // Static weak reference for AppDomain.ProcessExit best-effort cleanup (A2-F08).
    private static WeakReference<TrayApp> _liveInstance;

    public TrayApp()
    {
        // Hidden window — no taskbar presence
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        Size = Size.Empty;

        _config = new Config();
        _config.Load();

        // Lock in the window-chrome theme BEFORE any class with a static GDI
        // capture is first touched — OsdForm, MenuRenderer, and any future
        // renderer reads Theme.* at first class load. Restart-to-apply: the
        // GDI caches can't be invalidated without a process restart, so the
        // Settings dialog auto-restarts when the user changes this pin.
        Theme.Initialize(Theme.ResolveIsDark(_config.ThemeMode));

        _audio = new AudioManager();

        // Load icons
        LoadIcons();

        // Initialize audio endpoint
        bool hasAudio = _audio.Initialize(_config.DeviceId);

        // Read initial mute state
        if (hasAudio)
        {
            bool? currentMute = _audio.GetMute();
            _muted = currentMute ?? false;
            ApplyStartupMutePreference();
        }

        // Tray icon. Win11 tray-icon hygiene:
        //   1. Sweep zombies left behind by prior versioned WinGet installs and
        //      .NET single-file extraction-cache churn — without this, the user
        //      accumulates "N MicMutes" cruft in Settings → Other system tray
        //      icons across upgrades.
        //   2. Capture the baseline NotifyIconSettings subkey set BEFORE NIM_ADD
        //      so the promoter's Phase-2 orphan-claim has a "before" reference.
        //   3. Seed Text=non-empty BEFORE Visible=true so Shell_NotifyIcon
        //      passes NIF_TIP and Explorer writes the full schema (without it
        //      the subkey is sparse — only IconSnapshot, no ExecutablePath —
        //      forcing the slower Phase-2 path on every cold start).
        TrayIconPromoter.SweepStaleEntries(
            ourExeName: Path.GetFileName(Application.ExecutablePath),
            currentExePath: Application.ExecutablePath);
        var trayBaseline = TrayIconPromoter.CaptureBaseline();

        _menuRenderer = new MenuRenderer();
        _trayMenu = new ContextMenuStrip
        {
            Renderer = _menuRenderer,
            BackColor = Theme.BgColor,
            ForeColor = Theme.FgColor,
        };
        _trayIcon = new NotifyIcon
        {
            Text = "MicMute",   // seed for NIF_TIP — overwritten by SyncTrayIcon below
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _trayIcon.MouseClick += OnTrayMouseClick;
        // The foreground app changes while the menu is closed — refresh the
        // "Reset position for <app>" label each time the menu opens.
        _trayMenu.Opening += (_, _) =>
        {
            if (_indicatorResetItem != null && _indicator != null)
                _indicatorResetItem.Text = "Reset position for " + DescribeProfile(_indicator.CurrentProfile);
        };

        // Build menu
        BuildTrayMenu();
        InvalidateTooltipCache();
        SyncTrayIcon();

        // Promote our subkey to visible-in-taskbar (vs hidden-in-overflow). Ticks
        // for up to 10 s while Explorer populates the schema, then self-disposes.
        StartTrayIconPromotion(trayBaseline);

        // OSD form (must be created before hotkey registration — errors show via OSD)
        _osdForm = new OsdForm();

        // Always-on mute indicator (red dot). Created after Theme/config load.
        _indicator = new IndicatorForm(_config);
        _indicator.SetMuted(_muted);

        // Flash timer (reusable, not started)
        _flashTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _flashTimer.Tick += OnFlashTick;

        // Explorer restart recovery
        _wmTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // A2-F08: register for process-exit best-effort cleanup (ghost icon prevention).
        _liveInstance = new WeakReference<TrayApp>(this);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Periodic sync timer — 15s to detect mic plug/unplug and external mute changes.
        // A2-F13: start the safety-net timer BEFORE hotkey registration so it is ticking
        // even if hotkey reg throws. Hotkey actions sync immediately, so this doesn't
        // affect responsiveness.
        try
        {
            _syncTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _syncTimer.Tick += OnSyncTick;
            _syncTimer.Start();

            // Hotkey registration is deferred to OnHandleCreated (A2-F10) where the
            // HWND is guaranteed to be valid.  The fields are initialised here so
            // the hotkey section below is a no-op — the real call happens in
            // OnHandleCreated.
            // RegisterMainHotkey / RegisterDeafenHotkey are called from OnHandleCreated.

            // Subscribe to suspend/resume events (BUG-002).
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch
        {
            DisposeSafe();
            throw;
        }

        // Show notification if no mic found
        if (!hasAudio)
        {
            ShowTimedTooltip("No microphone detected.\nPlug one in \u2014 MicMute will auto-detect it.", 5000);
        }
    }

    // A2-F10: defer native RegisterHotKey calls until the HWND is alive.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterMainHotkey();
        RegisterDeafenHotkey();
        RegisterIndicatorHotkey();

        // A3-F13: deliver any custom-icon-fallback toast that was queued before the
        // handle existed (LoadIcons runs in the ctor, before OnHandleCreated).
        if (_pendingCustomIconToast)
        {
            _pendingCustomIconToast = false;
            BeginInvoke((Action)(() =>
                ShowTimedTooltip(
                    "Custom icon couldn't load — using built-in icon.\nCheck the path in Settings.", 5000)));
        }

        // --after-theme-restart: dispatched by TrayApp.TryAutoRestartForTheme
        // in the outgoing process. Defer a confirmation toast by ~800ms so it
        // lands after the OSD's first paint and after the tray icon has had a
        // moment to materialize — the user clicked Apply on a theme change and
        // would otherwise just see their Settings dialog vanish.
        if (Environment.GetCommandLineArgs().Contains("--after-theme-restart"))
        {
            var themeToastTimer = new System.Windows.Forms.Timer { Interval = 800 };
            themeToastTimer.Tick += (_, _) =>
            {
                themeToastTimer.Stop();
                themeToastTimer.Dispose();
                if (_disposed || IsDisposed) return;
                ShowTimedTooltip("Theme applied.", 2000);
            };
            themeToastTimer.Start();
        }
    }

    // BUG-002 / A2-F02: re-initialise audio on resume from sleep/hibernate.
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            return;

        // Marshal to the UI thread — PowerModeChanged fires on a thread-pool thread.
        if (IsHandleCreated)
        {
            BeginInvoke((Action)ResumeRecovery);
        }
    }

    private void ResumeRecovery()
    {
        // Re-run the same recovery path that OnSyncTick uses when the endpoint
        // is gone — Release + Initialize + RegisterMainHotkey + tooltip refresh.
        // The 15 s _syncTimer is left running as a belt-and-suspenders fallback.
        _audio.Release();

        if (_audio.Initialize(_config.DeviceId))
        {
            bool? m = _audio.GetMute();
            _muted = m ?? false;
            SyncTrayIcon();
            RegisterMainHotkey();
            RegisterDeafenHotkey();
            Log.Warn("ResumeRecovery: audio endpoint re-initialised after resume");
        }
        else
        {
            _muted = false;
            SyncTrayIcon();
            ShowTimedTooltip("Microphone not found after resume.\nWill auto-reconnect.", 5000);
        }
    }

    // A2-F08: AppDomain.ProcessExit best-effort cleanup — hides the ghost tray icon
    // and unregisters hotkeys on unhandled crashes that skip the normal Dispose path.
    private static void OnProcessExit(object sender, EventArgs e)
    {
        if (_liveInstance != null && _liveInstance.TryGetTarget(out var app) && !app._disposed)
        {
            try
            {
                app._trayIcon.Visible = false;
                app._trayIcon.Dispose();
            }
            catch { /* best-effort */ }

            try
            {
                if (app._mainHotkeyRegistered)
                    NativeMethods.UnregisterHotKey(app.Handle, HOTKEY_ID_MAIN);
                if (app._deafenHotkeyRegistered)
                    NativeMethods.UnregisterHotKey(app.Handle, HOTKEY_ID_DEAFEN);
                if (app._indicatorHotkeyRegistered)
                    NativeMethods.UnregisterHotKey(app.Handle, HOTKEY_ID_INDICATOR_EDIT);
            }
            catch { /* best-effort */ }
        }
    }

    // A1-F09: null-safe dispose used when ctor throws partway through init.
    private void DisposeSafe()
    {
        try { _syncTimer?.Stop(); _syncTimer?.Dispose(); } catch { }
        try { _flashTimer?.Stop(); _flashTimer?.Dispose(); } catch { }
        try { _pttUniversalTimer?.Stop(); _pttUniversalTimer?.Dispose(); } catch { }
        try { _osdForm?.Dispose(); } catch { }
        try { _indicator?.Dispose(); } catch { }
        try { if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); } } catch { }
        try { DisposeMenuItems(); _trayMenu?.Dispose(); } catch { }
        try { _menuTitleFont?.Dispose(); } catch { }
        // _menuRenderer holds only static GDI — no instance state to dispose.
        try { DisposeIcons(); } catch { }
        try { _audio?.Dispose(); } catch { }
        try { _settingsDialog?.Dispose(); } catch { }
    }

    // ── Icon Loading ──────────────────────────────────────────────────────

    private void LoadIcons()
    {
        bool activeUsedFallback = false;
        bool mutedUsedFallback = false;
        _iconActive = LoadIcon(_config.IconActive, "mic_on.ico", out activeUsedFallback);
        _iconMuted  = LoadIcon(_config.IconMuted,  "mic_off.ico", out mutedUsedFallback);

        // A3-F13: notify the user once per session if a custom icon fell back to
        // the embedded resource, mirroring the _playSoundFailed pattern.
        if (!_customIconFailNotified && (activeUsedFallback || mutedUsedFallback))
        {
            _customIconFailNotified = true;
            // _osdForm may not exist yet on the very first ctor call (icons are
            // loaded before the OSD is constructed).  ShowTimedTooltip guards
            // against that via _osdForm null-check below, but we post this via
            // BeginInvoke so the ctor finishes first and _osdForm is ready.
            if (_osdForm != null)
            {
                ShowTimedTooltip(
                    "Custom icon couldn't load — using built-in icon.\nCheck the path in Settings.", 5000);
            }
            else if (IsHandleCreated)
            {
                // Queue until the message pump is running.
                BeginInvoke((Action)(() =>
                    ShowTimedTooltip(
                        "Custom icon couldn't load — using built-in icon.\nCheck the path in Settings.", 5000)));
            }
            else
            {
                // First-ctor path: Handle not yet created; BeginInvoke would throw.
                // Defer the toast until OnHandleCreated fires.
                _pendingCustomIconToast = true;
            }
        }
    }

    private static Icon LoadIcon(string customPath, string embeddedName, out bool usedFallback)
    {
        // All icons come back pre-rasterized to the current tray small-icon
        // size, so Shell_NotifyIcon doesn't have to rescale on every update.
        // Without this, a 256x256 .ico forces the shell to resample down to
        // 16/24/32 px on every NIM_MODIFY — noticeable as per-click lag.
        using Icon raw = LoadIconRaw(customPath, embeddedName, out usedFallback);
        try
        {
            return RasterizeToTraySize(raw);
        }
        catch (Exception ex)
        {
            Log.Warn($"LoadIcon rasterize failed: {ex.Message}");
            return (Icon)raw.Clone();
        }
    }

    // Backward-compat overload so callers that don't need the fallback flag still compile.
    private static Icon LoadIcon(string customPath, string embeddedName)
        => LoadIcon(customPath, embeddedName, out _);

    internal static Icon LoadIconRaw(string customPath, string embeddedName, out bool usedFallback)
    {
        usedFallback = false;
        // Priority: custom path > file on disk next to exe > embedded resource
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            try { return new Icon(customPath); }
            catch (Exception ex)
            {
                Log.Warn($"LoadIcon custom '{customPath}' failed: {ex.Message}");
                usedFallback = true;
            }
        }
        else if (!string.IsNullOrEmpty(customPath))
        {
            // Path was configured but file doesn't exist — that's a fallback too.
            usedFallback = true;
        }

        string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(dir))
        {
            string diskPath = Path.Combine(dir, embeddedName);
            if (File.Exists(diskPath))
            {
                try { return new Icon(diskPath); }
                catch (Exception ex) { Log.Warn($"LoadIcon disk '{diskPath}' failed: {ex.Message}"); }
            }
        }

        // Embedded resource
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream(embeddedName);
        if (stream != null)
            return new Icon(stream);

        // Ultimate fallback — clone the system icon so it's safe to dispose
        return (Icon)SystemIcons.Application.Clone();
    }

    private static Icon RasterizeToTraySize(Icon src)
    {
        Size target = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(target.Width, target.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawIcon(src, new Rectangle(0, 0, target.Width, target.Height));
        }
        // Bitmap.GetHicon() returns a handle we must destroy ourselves; Icon.FromHandle
        // doesn't take ownership. Clone to produce an owned Icon, then free the temp.
        nint hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private void DisposeIcons()
    {
        // Safe to dispose — we always own these (cloned system icons or loaded from file/resource)
        _iconActive?.Dispose();
        _iconMuted?.Dispose();
    }

    // ── Tray Icon / Tooltip ──────────────────────────────────────────────

    private void InvalidateTooltipCache()
    {
        string modeName = _config.Mode == "push-to-talk" ? " [PTT]" : "";
        string suffix = _deafened ? " [DEAFENED]" : "";
        string combined = modeName + suffix;
        _cachedTooltipMuted = "MicMute v" + Config.Version + " \u2014 Mic: MUTED" + combined;
        _cachedTooltipActive = "MicMute v" + Config.Version + " \u2014 Mic: Active" + combined;
        _tooltipDirty = false;
    }

    private void SyncTrayIcon()
    {
        // Every _muted change funnels through here — keep the indicator in step
        // (before the flash guard, which only concerns the tray icon).
        _indicator?.SetMuted(_muted);
        if (_flashing)
            return;
        SetTrayIcon();
    }

    /// <summary>
    /// Drive the TrayIconPromoter retry timer until our subkey is identified or
    /// the 10 s budget elapses. Idempotent — Phase 1 no-ops once IsPromoted=1.
    /// </summary>
    private void StartTrayIconPromotion(HashSet<string> baseline)
    {
        var promoteTimer = new System.Windows.Forms.Timer { Interval = 500 };
        int attempts = 0;
        const int maxAttempts = 20;   // 500 ms * 20 = 10 s cap
        promoteTimer.Tick += (_, _) =>
        {
            attempts++;
            bool done = TrayIconPromoter.TryPromote(Application.ExecutablePath, baseline)
                        || attempts >= maxAttempts;
            if (done)
            {
                promoteTimer.Stop();
                promoteTimer.Dispose();
            }
        };
        promoteTimer.Start();
    }

    private void SetTrayIcon()
    {
        if (_tooltipDirty)
            InvalidateTooltipCache();

        var icon = _muted ? _iconMuted : _iconActive;
        var tooltip = _muted ? _cachedTooltipMuted : _cachedTooltipActive;

        if (_trayIcon.Icon != icon)
            _trayIcon.Icon = icon;
        // NotifyIcon tooltip max 63 chars
        if (tooltip.Length > 63)
            tooltip = tooltip[..63];
        if (_trayIcon.Text != tooltip)
            _trayIcon.Text = tooltip;
    }

    // ── Core Mute Operations ─────────────────────────────────────────────

    private void ToggleMute()
    {
        if (!_audio.HasEndpoint)
        {
            ShowTimedTooltip("No microphone available.\nTry Tray \u2192 Reinitialise Mic.", 5000);
            return;
        }

        bool newState = !_muted;
        if (!_audio.SetMute(newState))
        {
            ShowTimedTooltip("SetMute failed. Device may have changed \u2014 try Reinitialise Mic.", 5000);
            return;
        }

        _muted = newState;
        _config.SaveLastMuteState(_muted);
        SyncTrayIcon();
        ShowOsd();
        PlayFeedback();
    }

    /// <summary>
    /// Apply a mute state. Returns true on success (or no-op), false if the
    /// Core Audio call failed. Callers MUST check the return value on paths
    /// where a silent failure would lie to the user — deafen entry and PTT
    /// press/release being the two catastrophic ones: the tray would show
    /// "muted" while the mic is actually hot, or vice-versa.
    /// </summary>
    private bool SetMuteState(bool muted, bool quiet = false)
    {
        if (!_audio.HasEndpoint)
            return false;
        if (_muted == muted)
            return true;

        if (!_audio.SetMute(muted))
            return false;

        _muted = muted;
        _config.SaveLastMuteState(_muted);
        SyncTrayIcon();

        if (!quiet)
        {
            ShowOsd();
            PlayFeedback();
        }
        return true;
    }

    // ── Sound Feedback ───────────────────────────────────────────────────

    private void PlayFeedback()
    {
        if (!_config.SoundFeedback)
            return;

        string soundFile = _muted ? _config.MuteSound : _config.UnmuteSound;
        if (!string.IsNullOrEmpty(soundFile) && File.Exists(soundFile))
        {
            // Use SND_ASYNC to avoid blocking the UI thread
            if (!NativeMethods.PlaySound(soundFile, 0,
                NativeMethods.SND_FILENAME | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT))
            {
                // Surface the first failure per file so a corrupt/unsupported WAV
                // doesn't silently fall back to beep forever. HashSet.Add returns
                // true only on the first occurrence — subsequent calls are quiet.
                if (_playSoundFailed.Add(soundFile))
                {
                    Log.Warn($"PlaySound failed for '{soundFile}'; falling back to beep");
                    ShowTimedTooltip(
                        "Custom sound couldn't play:\n" + Path.GetFileName(soundFile) +
                        "\nFalling back to the built-in beep.", 4000);
                }
                PlayToneSequence(_muted);
            }
        }
        else
        {
            PlayToneSequence(_muted);
        }
    }

    private static void PlayToneSequence(bool muted)
    {
        NativeMethods.Beep(muted ? 587u : 880u, 80);
    }

    private static void PlayModeChirp(string mode)
    {
        NativeMethods.Beep(mode == "push-to-talk" ? 1568u : 1175u, 50);
    }

    // ── OSD ──────────────────────────────────────────────────────────────

    private void ShowOsd()
    {
        if (!_config.OsdEnabled)
            return;
        _osdForm.ShowOsd(_muted, _config.OsdDuration);
    }

    // ── Icon Flash ───────────────────────────────────────────────────────

    private void FlashIcon()
    {
        if (_flashing)
            _flashTimer.Stop();
        _flashing = true;
        _flashCount = 0;
        _flashTimer.Start();
    }

    private void OnFlashTick(object sender, EventArgs e)
    {
        try
        {
            bool showOpposite = (_flashCount % 2) == 0;
            if (showOpposite)
            {
                // Show opposite icon
                _trayIcon.Icon = _muted ? _iconActive : _iconMuted;
            }
            else
            {
                SetTrayIcon();
            }

            _flashCount++;
            if (_flashCount >= 2)
            {
                _flashTimer.Stop();
                _flashing = false;
                SetTrayIcon();
            }
        }
        catch (Exception ex)
        {
            Log.Error("OnFlashTick", ex);
            _flashTimer.Stop();
            _flashing = false;
        }
    }

    // ── Periodic Sync ────────────────────────────────────────────────────

    private void OnSyncTick(object sender, EventArgs e)
    {
        try
        {
            if (!_audio.HasEndpoint)
            {
                // Try to find a newly plugged-in mic
                if (_audio.Initialize(_config.DeviceId))
                {
                    ShowTimedTooltip("Microphone detected \u2014 auto-connected.", 3000);
                    bool? m = _audio.GetMute();
                    _muted = m ?? false;
                    SyncTrayIcon();
                    // Re-arm the hotkey. Without this the polling path
                    // (which bails when HasEndpoint went false) never restarts,
                    // silently leaving the user with no hotkey until they
                    // mode-switch or relaunch.
                    RegisterMainHotkey();
                    _syncConsecutiveFails = 0;
                    _syncFailTooltipShown = false;
                }
                return;
            }

            // Check if device is still valid
            bool? currentMute = _audio.GetMute();
            if (currentMute == null)
            {
                // Device went away
                _audio.Release();
                _muted = false;

                // If the user was deafened, mic loss shouldn't leave them
                // stuck with speakers muted and no UI affordance to recover.
                // Restore speakers to their pre-deafen state and clear the
                // deafen flag so the tooltip doesn't contradict itself.
                // Only clear _deafened if the speaker restore actually
                // succeeded — otherwise next mic-plug won't know we were
                // deafened and the user gets stuck with muted speakers AND
                // no "deafened" state to toggle off.
                if (_deafened)
                {
                    bool restored = true;
                    try { AudioManager.SetSpeakerMute(_speakerWasMuted); }
                    catch (Exception ex)
                    {
                        Log.Warn("SetSpeakerMute restore on device-loss failed: " + ex.Message);
                        restored = false;
                    }
                    if (restored)
                    {
                        _deafened = false;
                        _tooltipDirty = true;
                    }
                }

                SyncTrayIcon();
                ShowTimedTooltip("Microphone disconnected.\nWill auto-reconnect when available.", 5000);
                return;
            }

            bool externalMuted = currentMute.Value;
            if (externalMuted != _muted)
            {
                if (_config.MuteLock)
                {
                    // Fight back: re-apply our state
                    if (_lockDebounce)
                    {
                        _lockDebounce = false;
                        _muteLockFightBackFails = 0;
                        _muteLockFightBackTooltipShown = false;
                        return;
                    }
                    if (_audio.SetMute(_muted))
                    {
                        _lockDebounce = true;
                        _muteLockFightBackFails = 0;
                        _muteLockFightBackTooltipShown = false;

                        // BUG-007 / A8-F01: if sticky PTT is active and we couldn't hold
                        // our un-muted state (external muted won and we gave up), the
                        // sticky context is now invalid — clear it.
                        // Here we successfully fought back to _muted. If _muted is true
                        // and sticky was set, sticky-unmuted is a lie.
                        if (_pttStickyUnmuted && _muted)
                            ClearStickyPttOverride();
                    }
                    else
                    {
                        // Don't set the debounce on failure — we want the next
                        // tick to retry the fight-back instead of silently
                        // conceding the external state for a full 15s cycle.
                        Log.Warn("MuteLock fight-back SetMute failed; will retry on next sync tick");

                        // A3-F17: after N=3 consecutive fight-back failures, surface a
                        // high-visibility tooltip and flash the icon. The user's mic may
                        // be live while MuteLock thinks it's muted.
                        _muteLockFightBackFails++;
                        if (_muteLockFightBackFails >= 3 && !_muteLockFightBackTooltipShown)
                        {
                            _muteLockFightBackTooltipShown = true;
                            ShowTimedTooltip(
                                "\u26a0 MicMute couldn't keep your mic muted \u2014 mic may be live.\nTray \u2192 Reinit Mic.",
                                8000);
                            FlashIcon();
                        }

                        // BUG-007 / A8-F01: fight-back failed — external muted our mic
                        // and we couldn't re-assert. If sticky PTT says we're unmuted,
                        // that's now a lie — clear the sticky context so the OSD stops
                        // claiming the mic is listening.
                        if (_pttStickyUnmuted && externalMuted)
                            ClearStickyPttOverride();
                    }
                }
                else
                {
                    // Accept external change
                    _muted = externalMuted;
                    SyncTrayIcon();

                    // BUG-007 / A8-F01: MuteLock is OFF. External app changed the mic
                    // state. If sticky PTT says we're unmuted but the mic is now muted,
                    // the OSD is lying — clear the sticky flag so the user sees truth.
                    if (_pttStickyUnmuted && externalMuted)
                        ClearStickyPttOverride();
                }
            }
            else
            {
                _lockDebounce = false;
                _muteLockFightBackFails = 0;
                _muteLockFightBackTooltipShown = false;
            }

            // A3-F16: reset consecutive failure counter on any successful tick.
            _syncConsecutiveFails = 0;
            _syncFailTooltipShown = false;
        }
        catch (Exception ex)
        {
            // Swallow to keep the tray alive — the global handler is our safety net,
            // but a transient audio-service hiccup shouldn't kill the app.
            Log.Error("OnSyncTick", ex);

            // A3-F16: after 4 consecutive tick exceptions, show a one-shot tooltip
            // so the user knows their mute state may be stale.
            _syncConsecutiveFails++;
            if (_syncConsecutiveFails >= 4 && !_syncFailTooltipShown)
            {
                _syncFailTooltipShown = true;
                ShowTimedTooltip(
                    "MicMute is having trouble reaching Windows audio \u2014 some states may be stale.",
                    5000);
            }
        }
    }

    // ── Hotkey Registration ──────────────────────────────────────────────

    private void RegisterMainHotkey()
    {
        UnregisterMainHotkey();

        // PTT mode takes the polling path unconditionally — fullscreen-safe,
        // accepts bare modifier keys, no anti-cheat signature. Toggle mode
        // uses RegisterHotKey (event-driven, zero idle cost).
        bool pttMode = _config.Mode == "push-to-talk";
        if (!Config.ParseHotkey(_config.Hotkey, out uint mods, out uint vk, allowBare: pttMode))
        {
            ShowTimedTooltip("Invalid hotkey: " + _config.Hotkey + "\nFalling back to tray-only mode.", 5000);
            return;
        }

        if (pttMode)
        {
            StartUniversalPttPoll(vk, mods & ~NativeMethods.MOD_NOREPEAT);
            return;
        }

        if (NativeMethods.RegisterHotKey(Handle, HOTKEY_ID_MAIN, mods, vk))
        {
            _mainHotkeyRegistered = true;
        }
        else
        {
            // A8-F07 / A8-F12: capture the Win32 error before any other call clears it.
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Warn($"RegisterMainHotkey failed for '{_config.Hotkey}': Win32 error {err} (0x{err:X4})");
            ShowTimedTooltip("Could not register hotkey: " + Config.HotkeyToReadable(_config.Hotkey), 5000);
        }
    }

    private void UnregisterMainHotkey()
    {
        if (_mainHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_MAIN);
            _mainHotkeyRegistered = false;
        }

        // StopUniversalPttPoll handles the safety re-mute if the user was
        // holding PTT during a rebind / mode switch — don't duplicate here.
        StopUniversalPttPoll();
    }

    private void RegisterDeafenHotkey()
    {
        UnregisterDeafenHotkey();

        if (string.IsNullOrEmpty(_config.DeafenHotkey))
            return;

        if (!Config.ParseHotkey(_config.DeafenHotkey, out uint mods, out uint vk))
        {
            ShowTimedTooltip("Invalid deafen hotkey: " + _config.DeafenHotkey, 5000);
            return;
        }

        if (NativeMethods.RegisterHotKey(Handle, HOTKEY_ID_DEAFEN, mods, vk))
        {
            _deafenHotkeyRegistered = true;
        }
        else
        {
            // A8-F07 / A8-F12: capture Win32 error immediately.
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Warn($"RegisterDeafenHotkey failed for '{_config.DeafenHotkey}': Win32 error {err} (0x{err:X4})");
            ShowTimedTooltip("Could not register deafen hotkey.", 5000);
        }
    }

    private void UnregisterDeafenHotkey()
    {
        if (_deafenHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_DEAFEN);
            _deafenHotkeyRegistered = false;
        }
    }

    private void RegisterIndicatorHotkey()
    {
        UnregisterIndicatorHotkey();

        if (string.IsNullOrEmpty(_config.IndicatorEditHotkey))
            return;

        if (!Config.ParseHotkey(_config.IndicatorEditHotkey, out uint mods, out uint vk))
        {
            ShowTimedTooltip("Invalid indicator hotkey: " + _config.IndicatorEditHotkey, 5000);
            return;
        }

        if (NativeMethods.RegisterHotKey(Handle, HOTKEY_ID_INDICATOR_EDIT, mods, vk))
        {
            _indicatorHotkeyRegistered = true;
        }
        else
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Warn($"RegisterIndicatorHotkey failed for '{_config.IndicatorEditHotkey}': Win32 error {err} (0x{err:X4})");
            ShowTimedTooltip("Could not register indicator hotkey: " + Config.HotkeyToReadable(_config.IndicatorEditHotkey), 5000);
        }
    }

    private void UnregisterIndicatorHotkey()
    {
        if (_indicatorHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_INDICATOR_EDIT);
            _indicatorHotkeyRegistered = false;
        }
    }

    // ── Status Indicator ─────────────────────────────────────────────────

    private void ToggleIndicatorEditMode()
    {
        if (!_config.IndicatorEnabled)
        {
            ShowTimedTooltip("Status indicator is turned off.", 2000);
            return;
        }
        _indicator.ToggleEditMode();
        if (_indicator.EditMode)
            ShowTimedTooltip("Drag the indicator. Press " + Config.HotkeyToReadable(_config.IndicatorEditHotkey) +
                             " or right-click it to save\nPosition for: " + DescribeProfile(_indicator.CurrentProfile), 4000);
        else
            ShowTimedTooltip("Position saved for: " + DescribeProfile(_indicator.LastSavedProfile), 2500);
        BuildTrayMenu();
    }

    private static string DescribeProfile(string profile) =>
        profile == Config.IndicatorDefaultProfile ? "default (all apps)" : profile;

    private void SetIndicatorOption(Action<Config> change)
    {
        change(_config);
        if (!_config.Save())
            ShowTimedTooltip("Settings couldn't be saved.\nCheck permissions on MicMute.ini.", 4000);
        _indicator.ApplyConfig();
        BuildTrayMenu();
    }

    // ── WndProc ──────────────────────────────────────────────────────────

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_ID_MAIN)
            {
                // WM_HOTKEY only fires in Toggle mode now — PTT always runs
                // through the polling path. The mode guard defends against a
                // mode-switch race where a WM_HOTKEY is already queued when
                // we flip to PTT.
                if (_config.Mode != "push-to-talk")
                    ToggleMute();
            }
            else if (id == HOTKEY_ID_DEAFEN)
            {
                ToggleDeafen();
            }
            else if (id == HOTKEY_ID_INDICATOR_EDIT)
            {
                ToggleIndicatorEditMode();
            }
            else
            {
                // A8-F15: log stray WM_HOTKEY IDs — helps diagnose hotkey
                // conflicts registered by other apps or leaked IDs.
                Log.Warn($"WM_HOTKEY: unknown id={id}; wParam=0x{m.WParam:X} lParam=0x{m.LParam:X}");
            }
        }
        else if (_wmTaskbarCreated != 0 && m.Msg == (int)_wmTaskbarCreated)
        {
            // Explorer restarted — re-show tray icon and re-promote so we don't
            // get exiled to overflow. Capture a fresh baseline first; Explorer's
            // per-icon registry cache got nuked by the restart so subkeys may
            // transit through the orphan state again.
            var recoveryBaseline = TrayIconPromoter.CaptureBaseline();
            _trayIcon.Visible = true;
            SyncTrayIcon();
            StartTrayIconPromotion(recoveryBaseline);
        }

        base.WndProc(ref m);
    }

    // ── Push-to-Talk (polling path) ──────────────────────────────────────
    // PTT mode always uses continuous GetAsyncKeyState polling — no
    // RegisterHotKey, no keyboard hook. 30 Hz is imperceptible for press
    // timing (human reaction ~200 ms) and CPU cost is a couple of syscalls
    // per tick. No hook = no anti-cheat signature = no risk of collateral
    // bans. Works over fullscreen-exclusive games and accepts bare modifier
    // keys (RCtrl alone, etc.) the way Discord does PTT.

    private System.Windows.Forms.Timer _pttUniversalTimer;
    private bool _pttUniversalDown;
    private uint _pttUniversalVk;
    private uint _pttUniversalMods;

    // Sticky PTT override: user left-clicked the tray while in PTT mode.
    // Polling is paused, mic is unmuted, a persistent OSD stays up until the
    // user left-clicks again (or changes mode, or exits). Lets the user hold
    // the mic open during a long conversation without rubber-banding on the
    // hotkey. See ToggleStickyPttOverride.
    private bool _pttStickyUnmuted;

    private void StartUniversalPttPoll(uint vk, uint mods)
    {
        // Idempotent — always drop any prior poll first. Covers back-to-back
        // RegisterMainHotkey calls from Settings-apply + mic-unplug races that
        // could otherwise leave a stale timer ticking against the old VK.
        StopUniversalPttPoll();

        // No mic → nothing to poll for. Sync-timer recovery path will
        // re-call RegisterMainHotkey once an endpoint reappears.
        if (!_audio.HasEndpoint) return;

        _pttUniversalVk = vk;
        _pttUniversalMods = mods;
        // Seed the edge-tracker from the key's CURRENT state. If the user is
        // already holding the key when we start polling (e.g. hit Apply while
        // holding PTT), the first tick would otherwise see an up→down edge
        // from nothing and fire a phantom unmute. Starting "down" means we
        // wait for a genuine release before the next press-edge triggers.
        _pttUniversalDown = (NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0;
        if (_pttUniversalTimer == null)
        {
            _pttUniversalTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _pttUniversalTimer.Tick += OnUniversalPttPoll;
        }
        _pttUniversalTimer.Start();
    }

    private void StopUniversalPttPoll()
    {
        if (_pttUniversalTimer == null) return;
        bool wasDown = _pttUniversalDown;
        _pttUniversalTimer.Stop();
        _pttUniversalDown = false;
        // Safety: if we stop while the user is still holding the key, the
        // natural release edge will never arrive — re-mute defensively.
        if (!_shuttingDown && wasDown && _audio.HasEndpoint && !_muted)
        {
            if (!SetMuteState(true, true))
                Log.Warn("Universal PTT safety re-mute failed during stop");
        }
    }

    private void OnUniversalPttPoll(object sender, EventArgs e)
    {
        // A1-F10: guard against a dispose race where the timer fires one last
        // time after _pttUniversalTimer.Stop() was called from Dispose().
        if (_shuttingDown) return;

        try
        {
            // Mic unplugged mid-hold or during idle? Stop cleanly. Sync timer
            // will re-arm the hotkey path when a mic comes back.
            if (!_audio.HasEndpoint)
            {
                StopUniversalPttPoll();
                return;
            }

            bool keyDown = (NativeMethods.GetAsyncKeyState((int)_pttUniversalVk) & 0x8000) != 0;

            if (!_pttUniversalDown && keyDown && ModifiersMatch(_pttUniversalMods, _pttUniversalVk))
            {
                _pttUniversalDown = true;
                if (!SetMuteState(false, true))
                {
                    Log.Error("Universal PTT unmute failed");
                    ShowTimedTooltip(
                        "Couldn't unmute microphone.\nTry Tray \u2192 Reinit Mic.", 4000);
                }
            }
            else if (_pttUniversalDown && !keyDown)
            {
                _pttUniversalDown = false;
                if (!SetMuteState(true, true))
                {
                    Log.Error("Universal PTT re-mute failed — mic may still be hot");
                    ShowTimedTooltip(
                        "Couldn't re-mute microphone after PTT.\n" +
                        "Mic may still be open \u2014 verify in Windows.", 4000);
                }
            }
        }
        catch (Exception ex)
        {
            // Stop on exception — otherwise a steady-state fault spams the log
            // every 30 ms on steady-state faults.
            Log.Error("OnUniversalPttPoll", ex);
            _pttUniversalTimer?.Stop();

            // If the fault fired AFTER we unmuted for a press, the release
            // edge will never arrive — defensively re-mute so the mic doesn't
            // stay hot. This is the "silent hot mic" class of bug MicMute
            // exists to prevent.
            if (_pttUniversalDown && _audio.HasEndpoint && !_muted)
            {
                try
                {
                    if (SetMuteState(true, true))
                    {
                        _pttUniversalDown = false;
                    }
                    else
                    {
                        Log.Warn("Universal PTT exception-path re-mute failed — mic may be hot");
                        ShowTimedTooltip(
                            "Couldn't re-mute microphone after PTT error.\n" +
                            "Mic may be open \u2014 verify in Windows.", 4000);
                    }
                }
                catch (Exception inner)
                {
                    Log.Error("Exception-path re-mute threw", inner);
                }
            }
        }
    }

    private static bool ModifiersMatch(uint required, uint targetVk)
    {
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        bool targetCtrl  = targetVk is 0xA2 or 0xA3;
        bool targetAlt   = targetVk is 0xA4 or 0xA5;
        bool targetShift = targetVk is 0xA0 or 0xA1;
        bool targetWin   = targetVk is 0x5B or 0x5C;

        if (!CheckMod(required, NativeMethods.MOD_CONTROL, targetCtrl,  VK_CONTROL)) return false;
        if (!CheckMod(required, NativeMethods.MOD_ALT,     targetAlt,   VK_MENU))    return false;
        if (!CheckMod(required, NativeMethods.MOD_SHIFT,   targetShift, VK_SHIFT))   return false;
        if (!CheckModWin(required, targetWin, VK_LWIN, VK_RWIN)) return false;
        return true;
    }

    private static bool CheckMod(uint required, uint flag, bool targetIsModifier, int genericVk)
    {
        // If the target key IS this modifier, it's inherently down during a
        // press — don't fail the match on its own side-channel state.
        if (targetIsModifier) return true;
        bool wanted = (required & flag) != 0;
        bool down = (NativeMethods.GetAsyncKeyState(genericVk) & 0x8000) != 0;
        return wanted == down;
    }

    private static bool CheckModWin(uint required, bool targetIsWin, int vkLWin, int vkRWin)
    {
        if (targetIsWin) return true;
        bool wanted = (required & NativeMethods.MOD_WIN) != 0;
        bool down = ((NativeMethods.GetAsyncKeyState(vkLWin) & 0x8000) != 0)
                 || ((NativeMethods.GetAsyncKeyState(vkRWin) & 0x8000) != 0);
        return wanted == down;
    }

    // ── Deafen Mode ──────────────────────────────────────────────────────

    private void ToggleDeafen()
    {
        if (!_audio.HasEndpoint)
            return;

        if (!_deafened)
        {
            // Enter deafen — CRITICAL: if the mic-mute step fails, ABORT.
            // We must never show "DEAFENED — mic + speakers muted" while the
            // mic is actually live. Speaker-mute is skipped and _deafened stays
            // false so the tooltip tells the truth and the user can retry.
            try { _speakerWasMuted = AudioManager.GetSpeakerMute(); }
            catch (Exception ex) { Log.Warn("GetSpeakerMute failed, assuming unmuted: " + ex.Message); _speakerWasMuted = false; }

            if (!_muted && !SetMuteState(true))
            {
                Log.Error("Deafen aborted: mic mute failed — NOT muting speakers, NOT setting deafen flag");
                ShowTimedTooltip(
                    "Couldn't mute microphone \u2014 deafen aborted.\nTry Tray \u2192 Reinit Mic.", 5000);
                return;
            }

            try { AudioManager.SetSpeakerMute(true); }
            catch (Exception ex) { Log.Error("SetSpeakerMute(true) failed during deafen-enter", ex); }

            _deafened = true;
            _tooltipDirty = true;
            SetTrayIcon();
            ShowTimedTooltip("DEAFENED \u2014 mic + speakers muted", 3000);
        }
        else
        {
            // Exit deafen — best-effort. If mic-unmute fails the user still
            // wants speakers restored, and they'll see a warning about the mic.
            bool micUnmuted = SetMuteState(false);
            try { AudioManager.SetSpeakerMute(_speakerWasMuted); }
            catch (Exception ex) { Log.Error("SetSpeakerMute restore failed during deafen-exit", ex); }

            _deafened = false;
            _tooltipDirty = true;
            SetTrayIcon();
            if (micUnmuted)
                ShowTimedTooltip("Undeafened \u2014 audio restored", 3000);
            else
                ShowTimedTooltip(
                    "Undeafened, but microphone unmute failed.\nTry Tray \u2192 Reinit Mic.", 5000);
        }
    }

    // ── Mode Switching ───────────────────────────────────────────────────

    private void SetMode(string newMode)
    {
        // Any mode change invalidates sticky PTT — the override only makes
        // sense inside PTT mode. Clear it before the mode flips so the
        // persistent OSD doesn't linger under toggle-mode state.
        bool wasSticky = _pttStickyUnmuted;
        ClearStickyPttOverride();

        // A8-F05: if the sticky override was active the mic is currently
        // unmuted. The polling timer is about to be torn down, so there is no
        // release-edge coming to re-mute it. Force-mute now regardless of the
        // target mode to guarantee the mic is not left hot.
        if (wasSticky && _audio.HasEndpoint && !_muted)
        {
            if (!SetMuteState(true, true))
                Log.Warn("SetMode: sticky-PTT force-mute failed after mode change — mic may be hot");
        }

        _config.Mode = newMode;
        if (newMode == "push-to-talk")
        {
            // Best-effort prep-mute on mode switch — a silent failure here
            // would be caught by the next sync tick anyway, and the user
            // hasn't pressed the PTT key yet, so no "lying UI" risk.
            if (!SetMuteState(true, true))
                Log.Warn("Prep-mute for PTT mode switch failed; sync timer will reconcile");
        }
        RegisterMainHotkey();
        BuildTrayMenu();
        _tooltipDirty = true;
        SetTrayIcon();
        if (!_config.Save())
            ShowTimedTooltip("Mode changed, but settings couldn't be saved.\nCheck permissions on MicMute.ini.", 4000);

        if (_config.SoundFeedback)
            PlayModeChirp(newMode);

        string modeName = newMode == "push-to-talk" ? "Push-to-Talk" : "Toggle";
        ShowTimedTooltip("Mode: " + modeName, 3000);
    }

    // ── Tray Menu ────────────────────────────────────────────────────────

    private void BuildTrayMenu()
    {
        // Dispose old menu items before clearing to prevent GDI/memory leaks
        DisposeMenuItems();

        string hotkeyReadable = Config.HotkeyToReadable(_config.Hotkey);

        // Reusable bold font for title item
        _menuTitleFont?.Dispose();
        _menuTitleFont = new Font(_trayMenu.Font, FontStyle.Bold);

        // Title — program name + version. Non-interactive header. Tag tells
        // MenuRenderer to paint this row with TitlebarBg (brand blue + white
        // text), matching the dialog titlebar treatment so the tray menu
        // chrome reads as the same surface family as Settings/Help/Update.
        // Enabled stays true so the white text isn't disabled-greyed — the
        // header has no Click handler, so leaving it Enabled is harmless.
        var titleItem = new ToolStripMenuItem("MicMute v" + Config.Version)
        {
            Font = _menuTitleFont,
            TextAlign = ContentAlignment.MiddleCenter,
            Tag = MenuRenderer.TitleItemTag,
        };
        _trayMenu.Items.Add(titleItem);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Hotkey — the combo is the label and the action: click toggles the mic
        // (matches left-click on the tray icon). Unbound state falls back to
        // "click to set" which opens Settings.
        bool hasHotkey = !string.IsNullOrEmpty(_config.Hotkey);
        string hotkeyDisplay = hasHotkey
            ? hotkeyReadable
            : "(no hotkey bound \u2014 click to set)";
        var hotkeyItem = new ToolStripMenuItem(hotkeyDisplay) { Font = _menuTitleFont };
        if (hasHotkey)
            hotkeyItem.Click += (_, _) => HandleUserToggleClick();
        else
            hotkeyItem.Click += (_, _) => ShowSettingsDialog();
        _trayMenu.Items.Add(hotkeyItem);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Mode submenu
        string modeLabel = "Mode: " + (_config.Mode == "push-to-talk" ? "Push-to-Talk" : "Toggle");
        var modeItem = new ToolStripMenuItem(modeLabel);
        // Submenu drop-down is a separate ToolStripDropDown \u2014 give it
        // matching chrome so it inherits BackColor/ForeColor when the
        // renderer's color table is queried for paths we don't override
        // (drop shadow, scroll arrows on long submenus).
        ThemeDropDown(modeItem);
        var toggleModeItem = new ToolStripMenuItem("Toggle");
        toggleModeItem.Checked = _config.Mode == "toggle";
        toggleModeItem.Click += (_, _) => SetMode("toggle");
        var pttModeItem = new ToolStripMenuItem("Push-to-Talk");
        pttModeItem.Checked = _config.Mode == "push-to-talk";
        pttModeItem.Click += (_, _) => SetMode("push-to-talk");
        modeItem.DropDownItems.Add(toggleModeItem);
        modeItem.DropDownItems.Add(pttModeItem);
        _trayMenu.Items.Add(modeItem);

        // Device submenu (lazy-loaded)
        _deviceMenuItem = new ToolStripMenuItem("Mic Source");
        ThemeDropDown(_deviceMenuItem);
        var loadingItem = new ToolStripMenuItem("Loading\u2026") { Enabled = false };
        _deviceMenuItem.DropDownItems.Add(loadingItem);
        _deviceMenuItem.DropDownOpening += OnDeviceMenuOpening;
        _deviceMenuPopulated = false;
        _trayMenu.Items.Add(_deviceMenuItem);

        // Status indicator submenu
        var indicatorItem = new ToolStripMenuItem("Status Indicator");
        ThemeDropDown(indicatorItem);
        var indEnabled = new ToolStripMenuItem("Show indicator while muted") { Checked = _config.IndicatorEnabled };
        indEnabled.Click += (_, _) => SetIndicatorOption(c => c.IndicatorEnabled = !c.IndicatorEnabled);
        var indIcon = new ToolStripMenuItem("Use mic icon instead of dot")
        {
            Checked = _config.IndicatorStyle == "icon",
            Enabled = _config.IndicatorEnabled,
        };
        indIcon.Click += (_, _) => SetIndicatorOption(c => c.IndicatorStyle = c.IndicatorStyle == "icon" ? "dot" : "icon");
        var indLive = new ToolStripMenuItem("Also show while live")
        {
            Checked = _config.IndicatorShowWhenLive,
            Enabled = _config.IndicatorEnabled,
        };
        indLive.Click += (_, _) => SetIndicatorOption(c => c.IndicatorShowWhenLive = !c.IndicatorShowWhenLive);
        var indEdit = new ToolStripMenuItem(_indicator?.EditMode == true ? "Save position" : "Move indicator…")
        {
            Enabled = _config.IndicatorEnabled,
            ShortcutKeyDisplayString = string.IsNullOrEmpty(_config.IndicatorEditHotkey)
                ? null : Config.HotkeyToReadable(_config.IndicatorEditHotkey),
        };
        indEdit.Click += (_, _) => ToggleIndicatorEditMode();
        var indReset = _indicatorResetItem = new ToolStripMenuItem("Reset position for " + DescribeProfile(_indicator?.CurrentProfile ?? Config.IndicatorDefaultProfile))
        {
            Enabled = _config.IndicatorEnabled,
        };
        indReset.Click += (_, _) => _indicator.ResetCurrentProfile();
        indicatorItem.DropDownItems.Add(indEnabled);
        indicatorItem.DropDownItems.Add(indIcon);
        indicatorItem.DropDownItems.Add(indLive);
        indicatorItem.DropDownItems.Add(new ToolStripSeparator());
        indicatorItem.DropDownItems.Add(indEdit);
        indicatorItem.DropDownItems.Add(indReset);
        _trayMenu.Items.Add(indicatorItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        // Reinit
        var reinitItem = new ToolStripMenuItem("Reinit Mic");
        reinitItem.Click += (_, _) => ReinitMic();
        _trayMenu.Items.Add(reinitItem);

        // Sound settings
        var soundItem = new ToolStripMenuItem("Sound Settings");
        soundItem.Click += (_, _) =>
        {
            // A1-F06: wrap in try/catch — ms-settings: URIs can fail on locked-down
            // builds (Kiosk mode, policy restrictions) and should degrade gracefully.
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:sound",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open Sound Settings: {ex.Message}");
                ShowTimedTooltip("Couldn't open Sound Settings.\nOpen Settings \u2192 System \u2192 Sound manually.", 5000);
            }
        };
        _trayMenu.Items.Add(soundItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        // Settings — bracketed by separators so it sits in its own section
        // right above Exit, away from the action items.
        var settingsItem = new ToolStripMenuItem("Settings\u2026");
        settingsItem.Click += (_, _) => ShowSettingsDialog();
        _trayMenu.Items.Add(settingsItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitItem);
    }

    /// <summary>
    /// Match a submenu drop-down to the parent menu's chrome so it inherits
    /// theme colours for paint paths the renderer doesn't override (shadow
    /// edge, scroll arrows on long submenus).
    /// </summary>
    private static void ThemeDropDown(ToolStripMenuItem item)
    {
        item.DropDown.BackColor = Theme.BgColor;
        item.DropDown.ForeColor = Theme.FgColor;
    }

    /// <summary>
    /// Dispose all current menu items before rebuilding the menu.
    /// ToolStripItemCollection.Clear() does NOT dispose items.
    /// </summary>
    private void DisposeMenuItems()
    {
        for (int i = _trayMenu.Items.Count - 1; i >= 0; i--)
        {
            var item = _trayMenu.Items[i];
            // Dispose sub-items recursively for ToolStripMenuItems with dropdowns
            if (item is ToolStripMenuItem menuItem)
            {
                for (int j = menuItem.DropDownItems.Count - 1; j >= 0; j--)
                    menuItem.DropDownItems[j].Dispose();
            }
            item.Dispose();
        }
        _trayMenu.Items.Clear();
    }

    private void OnDeviceMenuOpening(object sender, EventArgs e)
    {
        if (_deviceMenuPopulated)
            return;
        PopulateDeviceMenu();
    }

    private void PopulateDeviceMenu()
    {
        // Dispose old placeholder items
        for (int i = _deviceMenuItem.DropDownItems.Count - 1; i >= 0; i--)
            _deviceMenuItem.DropDownItems[i].Dispose();
        _deviceMenuItem.DropDownItems.Clear();

        var defaultItem = new ToolStripMenuItem("System Default");
        defaultItem.Checked = string.IsNullOrEmpty(_config.DeviceId);
        defaultItem.Click += (_, _) => SelectDevice("");
        _deviceMenuItem.DropDownItems.Add(defaultItem);
        _deviceMenuItem.DropDownItems.Add(new ToolStripSeparator());

        var devices = AudioManager.EnumerateCaptureDevices();
        foreach (var dev in devices)
        {
            string label = dev.Name.Length > 40 ? dev.Name[..37] + "\u2026" : dev.Name;
            var item = new ToolStripMenuItem(label);
            item.Checked = _config.DeviceId == dev.Id;
            string devId = dev.Id; // capture for closure
            item.Click += (_, _) => SelectDevice(devId);
            _deviceMenuItem.DropDownItems.Add(item);
        }

        _deviceMenuPopulated = true;
    }

    private void SelectDevice(string deviceId)
    {
        _config.DeviceId = deviceId;
        _audio.Release();
        if (_audio.Initialize(deviceId))
        {
            bool? m = _audio.GetMute();
            _muted = m ?? false;
        }
        else
        {
            _muted = false;
        }
        SyncTrayIcon();
        BuildTrayMenu();
        if (!_config.Save())
            ShowTimedTooltip("Mic source changed, but settings couldn't be saved.\nCheck permissions on MicMute.ini.", 4000);

        ShowTimedTooltip(string.IsNullOrEmpty(deviceId)
            ? "Using system default microphone."
            : "Switched microphone.", 3000);
    }

    // ── Reinitialize Mic ─────────────────────────────────────────────────

    private void ReinitMic()
    {
        _audio.Release();
        if (_audio.Initialize(_config.DeviceId))
        {
            bool? m = _audio.GetMute();
            _muted = m ?? false;
            SyncTrayIcon();
            ShowTimedTooltip("Microphone reinitialised.", 3000);
        }
        else
        {
            ShowTimedTooltip("No microphone found.", 5000);
        }
    }

    // ── Startup Mute Preference ──────────────────────────────────────────

    private void ApplyStartupMutePreference()
    {
        // Attempt the startup state change, then re-read from the device so
        // the UI doesn't claim a mute state the hardware didn't actually
        // accept (rare, but possible on a device that's initialized but
        // flaky). Sync timer will eventually correct on its own; this keeps
        // the tray/OSD consistent from the first frame.
        bool targetKnown = true;
        bool target = _muted;

        switch (_config.StartMuted)
        {
            case "yes" when !_muted:
                target = true;
                break;
            case "unmuted" when _muted:
                target = false;
                break;
            case "last" when _config.LastMuteState != _muted:
                target = _config.LastMuteState;
                break;
            default:
                // PTT mode implies mic-off-until-held. Without an explicit
                // StartMuted preference, a hot mic at launch means the first
                // PTT press is an invisible no-op (SetMuteState short-circuits
                // on already-unmuted). Force-mute so PTT behaves from frame 1.
                if (_config.Mode == "push-to-talk" && !_muted)
                    target = true;
                else
                    targetKnown = false;
                break;
        }

        if (!targetKnown)
            return;

        if (_audio.SetMute(target))
            _muted = target;
        else
            _muted = _audio.GetMute() ?? _muted;
    }

    // ── Dialogs ──────────────────────────────────────────────────────────

    // Tray-menu "change hotkey" click routes straight into the Settings
    // dialog's Hotkeys section — inline capture lives there. No separate
    // HotkeyDialog window as of v2.1.9.

    private void ShowSettingsDialog()
    {
        if (_settingsDialog != null && !_settingsDialog.IsDisposed)
        {
            _settingsDialog.Show();
            _settingsDialog.BringToFront();
            return;
        }

        _settingsDialog = new SettingsDialog(_config, OnSettingsApplied);
        _settingsDialog.FormClosed += (_, _) => _settingsDialog = null;
        _settingsDialog.Show();
    }

    // Re-entry guard: rapid Apply→Save (or Apply→Apply) clicks would
    // otherwise spawn two children racing for the single-instance mutex.
    // Set once we commit to spawning; never cleared because we're exiting.
    private bool _themeRestartInFlight;

    private void OnSettingsApplied()
    {
        // Theme is restart-to-apply (static GDI caches in OsdForm and
        // MenuRenderer captured Theme.* at first class load and can't be
        // invalidated without a process restart). If the new pref resolves
        // to a different is-dark than the one we booted with, auto-restart
        // so the user doesn't have to. All other settings stay instant-apply.
        bool newIsDark = Theme.ResolveIsDark(_config.ThemeMode);
        if (newIsDark != Theme.IsDark && !_themeRestartInFlight)
        {
            _themeRestartInFlight = true;
            if (TryAutoRestartForTheme())
            {
                // Replacement process has been launched; we're about to exit.
                // Skip the rest of OnSettingsApplied — the new instance will
                // re-load icons, re-register hotkeys, and rebuild the menu
                // from its own ctor with the new theme.
                return;
            }
            // Spawn failed (locked exe, AV scan, missing path) — INI was
            // already written with the new theme, so the next manual launch
            // will pick it up. Surface a toast so the user understands the
            // theme didn't visibly change THIS session and they need to
            // restart. Without this the user sees Settings close + no
            // visible theme change + nothing in the log they can read.
            _themeRestartInFlight = false;
            ShowTimedTooltip(
                "Theme saved — applies next time you launch MicMute.\n" +
                "(Auto-restart didn't fire — restart manually to see it now.)",
                5000);
        }

        // Any Apply path that touches hotkey config invalidates sticky PTT —
        // RegisterMainHotkey below will restart polling from a clean state.
        ClearStickyPttOverride();

        // Load new icons first, then swap and dispose old ones
        var oldActive = _iconActive;
        var oldMuted = _iconMuted;

        LoadIcons();

        // Update tray icon to reference the new icons before disposing old ones
        SetTrayIcon();

        // Now safe to dispose old icons
        oldActive?.Dispose();
        oldMuted?.Dispose();

        // Defensive re-mute if the user was holding PTT during Apply — the
        // new hotkey registration has no release event waiting for us, so
        // guarantee the mic isn't hot across the rebind.
        if (_config.Mode == "push-to-talk" && _audio.HasEndpoint && !_muted)
        {
            if (!SetMuteState(true, true))
                Log.Warn("Settings-apply safety re-mute failed — mic may stay hot across rebind");
        }
        RegisterMainHotkey();
        RegisterDeafenHotkey();
        RegisterIndicatorHotkey();
        _indicator?.ApplyConfig();

        // Refresh tray
        _tooltipDirty = true;
        BuildTrayMenu();
        SetTrayIcon();
    }

    /// <summary>
    /// Spawn a replacement MicMute.exe with --after-theme-restart, then call
    /// Application.Exit on success. ApplySettings has already persisted the
    /// new ThemeMode by the time this is called, so the replacement process
    /// loads it on startup and Theme.Initialize picks up the new palette.
    ///
    /// Order matters: spawn BEFORE exit. If Process.Start throws (locked exe,
    /// AV scan, missing path), return false and fall back to the manual-restart
    /// OSD — the user is never left without a tray.
    /// </summary>
    private static bool TryAutoRestartForTheme()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("MicMute: theme auto-restart skipped — Environment.ProcessPath was null/empty");
            return false;
        }
        try
        {
            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- exePath is Environment.ProcessPath (our own executable, not user-supplied)
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                Arguments = "--after-theme-restart",
                UseShellExecute = true,
            });
            if (p == null)
            {
                Log.Warn("MicMute: theme auto-restart — Process.Start returned null; staying open for manual restart");
                return false;
            }
            Application.Exit();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"MicMute: theme auto-restart failed ({ex.GetType().Name}: {ex.Message}) — staying open for manual restart");
            return false;
        }
    }

    // ── Tray Click Handling ──────────────────────────────────────────────

    private void OnTrayMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            HandleUserToggleClick();
        }
        else if (e.Button == MouseButtons.Middle && _config.MiddleClickToggle)
        {
            SetMode(_config.Mode == "toggle" ? "push-to-talk" : "toggle");
        }
    }

    /// <summary>
    /// Unified entry point for left-click on the tray icon AND the
    /// "Toggle Mute" menu title item. In toggle mode it's a plain flip;
    /// in PTT mode it toggles the sticky override so the user can hold
    /// the mic open without holding the hotkey.
    /// </summary>
    private void HandleUserToggleClick()
    {
        if (_config.Mode == "push-to-talk")
            ToggleStickyPttOverride();
        else
            ToggleMute();
    }

    /// <summary>
    /// Sticky PTT: in PTT mode, left-clicking the tray unmutes the mic and
    /// pauses the polling hotkey until the next left-click. A persistent OSD
    /// stays up the whole time so the user can't forget the mic is live.
    /// Exiting the mode (or the app) clears the override and re-mutes.
    /// </summary>
    private void ToggleStickyPttOverride()
    {
        if (!_audio.HasEndpoint)
        {
            ShowTimedTooltip("No microphone available.\nTry Tray \u2192 Reinitialise Mic.", 5000);
            return;
        }

        if (_pttStickyUnmuted)
        {
            // Exit sticky — re-mute and re-arm polling so PTT works again.
            _pttStickyUnmuted = false;
            _osdForm?.HidePersistent();
            bool remuted = SetMuteState(true, true);
            if (!remuted)
            {
                // A8-F08: re-mute on sticky-exit failed — surface to user so they
                // know the mic may still be open.
                Log.Warn("Sticky-PTT exit re-mute failed — mic may still be hot");
                ShowTimedTooltip(
                    "\u26a0 Couldn't re-mute mic after leaving sticky PTT.\nMic may still be open \u2014 check Windows.", 5000);
            }
            RegisterMainHotkey();
            ShowTimedTooltip("Push-to-Talk \u2014 hold your hotkey to talk.", 2000);
        }
        else
        {
            // Enter sticky — stop polling first so a PTT release edge while
            // the user is walking their hand to the mouse doesn't race us
            // back to muted. The stop path's own safety re-mute only fires
            // if the PTT key is currently down, which it won't be here.
            StopUniversalPttPoll();
            if (!SetMuteState(false, true))
            {
                // Unmute failed — don't enter sticky state, and restart
                // polling so the user isn't stranded without a hotkey.
                Log.Error("Sticky-PTT unmute failed — aborting, re-arming hotkey");
                RegisterMainHotkey();
                ShowTimedTooltip("Couldn't unmute microphone.\nTry Tray \u2192 Reinitialise Mic.", 4000);
                return;
            }
            _pttStickyUnmuted = true;
            _osdForm.ShowPersistent("PTT \u2014 mic listening", isMuted: false);
        }
    }

    /// <summary>
    /// Clear any active sticky-PTT state. Called from SetMode (mode change),
    /// ExitApplication, and OnSettingsApplied when a setting change would
    /// invalidate the sticky pretense (hotkey rebind, LL-PTT toggle). Does
    /// NOT re-mute — caller owns the follow-on mic state.
    /// </summary>
    private void ClearStickyPttOverride()
    {
        if (!_pttStickyUnmuted) return;
        _pttStickyUnmuted = false;
        _osdForm?.HidePersistent();
    }

    // ── Timed Notification ────────────────────────────────────────────────

    private void ShowTimedTooltip(string text, int durationMs)
    {
        // Show notification with actual text in the OSD. Duration is
        // whatever the caller asked for — important messages (hotkey
        // conflict, mic disconnected, startup errors) need longer than
        // the 3s cap that used to be here.
        // Null-guard: _osdForm may not exist yet during early ctor paths.
        _osdForm?.ShowNotification(text, _muted, durationMs);
    }

    // ── Exit & Cleanup ───────────────────────────────────────────────────

    private void ExitApplication()
    {
        // BUG-004 / A2-F01: idempotency guard. Double-click on Exit or a race
        // between the menu click and an in-flight sync tick would otherwise run
        // COM I/O against an already-released endpoint.
        if (_shuttingDown) return;

        // Claim ownership of exit-time mute state before Dispose chain runs.
        // UnregisterMainHotkey skips its PTT re-mute safety net when this is set.
        _shuttingDown = true;

        // Hide the sticky-PTT persistent OSD so it doesn't linger visually
        // past Application.Exit while Dispose tears the form down.
        ClearStickyPttOverride();

        // Unmute on exit (F-10) + restore speakers if deafened (F-20)
        if (_deafened)
        {
            try { AudioManager.SetSpeakerMute(_speakerWasMuted); }
            catch (Exception ex) { Log.Warn("SetSpeakerMute restore on exit failed: " + ex.Message); }
        }

        if (_audio.HasEndpoint && _muted)
            _audio.SetMute(false);

        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Belt-and-suspenders: if something disposes us without going
            // through ExitApplication, the shutdown guard still applies.
            _shuttingDown = true;

            // BUG-002: unsubscribe before any teardown so the resume handler
            // can't fire against a half-disposed object.
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { }

            // A2-F07: null-guard every field — ctor may have thrown before all
            // fields were assigned, leaving some null.

            // Unregister hotkeys
            try { UnregisterMainHotkey(); } catch { }
            try { UnregisterDeafenHotkey(); } catch { }
            try { UnregisterIndicatorHotkey(); } catch { }

            // Stop and dispose timers
            if (_syncTimer != null) { try { _syncTimer.Stop(); _syncTimer.Dispose(); } catch { } }
            if (_flashTimer != null) { try { _flashTimer.Stop(); _flashTimer.Dispose(); } catch { } }

            if (_pttUniversalTimer != null)
            {
                try { _pttUniversalTimer.Stop(); _pttUniversalTimer.Dispose(); } catch { }
                _pttUniversalTimer = null;
            }

            // Dispose OSD + indicator
            try { _osdForm?.Dispose(); } catch { }
            try { _indicator?.Dispose(); } catch { }

            // Dispose tray icon (set invisible first)
            if (_trayIcon != null)
            {
                try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            }

            // Dispose menu items then menu
            try { DisposeMenuItems(); } catch { }
            try { _trayMenu?.Dispose(); } catch { }
            try { _menuTitleFont?.Dispose(); } catch { }
            // MenuRenderer holds only static GDI brushes — process-lifetime
            // caches, Win32 reclaims handles on process exit. No explicit
            // dispose needed. If the renderer ever grows instance GDI state,
            // make it IDisposable and add the dispose call here.

            // Dispose icons
            try { DisposeIcons(); } catch { }

            // Dispose audio
            try { _audio?.Dispose(); } catch { }

            // Dispose dialogs
            try { _settingsDialog?.Dispose(); } catch { }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
