namespace MicMute;

/// <summary>
/// Settings GUI. Rebuilt on the relational <see cref="UiLayout"/> container kit
/// (flat sections + AutoSize) so it is DPI-correct by construction — 100% and 150%
/// display scale are proportionally identical. No control has a literal position; the
/// form AutoSizes to its content stack at every DPI. Behaviour (hotkey capture,
/// validation, atomic apply, theme restart) is unchanged from the absolute-pixel version.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly Config _config;
    private readonly Action _onApply;

    // Behavior
    private readonly CheckBox _chkSoundFeedback;
    private readonly CheckBox _chkOsd;
    private readonly NumericUpDown _edtOsdDuration;
    private readonly CheckBox _chkMuteLock;
    private readonly CheckBox _chkAutoMute;
    private readonly NumericUpDown _edtAutoMuteMinutes;
    private readonly CheckBox _chkMiddleClick;
    private readonly CheckBox _chkRunAtStartup;
    private readonly ComboBox _ddlStartMuted;

    // Appearance — restart-to-apply theme pin (System / Dark / Light).
    private readonly ComboBox _ddlTheme;

    // Hotkeys — captured values update via the compact-cell helper.
    private string _capturedMainHK = "";
    private string _capturedDeafenHK = "";
    // Cached "use it anyway" confirmations — set by ValidateHotkeysBeforeApply,
    // committed to _config by ApplySettings. Prevents re-warning the user on
    // repeated Apply/Save when the hotkey is unchanged.
    private string _pendingAckedMainHk;
    private string _pendingAckedDeafenHk;

    // When a hotkey row is in capture mode, these hold callbacks that
    // ProcessCmdKey below invokes instead of letting Esc/Enter trigger
    // CancelButton/AcceptButton. Null when no row is capturing.
    private Action _capturingCancel;
    private Action _capturingCommit;

    // Custom files — paths held as string fields (updated by closure callbacks
    // from BuildFileCell). Display TextBoxes named _lbl* show only the filename.
    //
    // Mute / Unmute custom sounds are intentionally NOT exposed in the GUI. The
    // Config.MuteSound / UnmuteSound fields still persist via the INI for power
    // users who want to hand-edit MicMute.ini.
    private string _pathIconMuted  = "";
    private string _pathIconActive = "";
    private readonly TextBox _lblIconMuted;
    private readonly TextBox _lblIconActive;
    private ToolTip _fileRowTooltip;

    // The relational content stack — the dialog sizes its ClientSize to this in OnLoad.
    private readonly UiLayout.Stack _stack;

    // Design (96-DPI) client width. The dialog fixes its width to this (scaled to the
    // device DPI) and fits its height to the content at that width.
    private const int DesignClientWidth = 510;

    // Tracks in-flight reject-animation timers (the 1800ms tints that fire
    // when the user tries to capture a bare modifier outside PTT mode). Dispose()
    // sweeps any survivors so the WinForms.Timer's HWND-bound handle isn't leaked.
    private readonly List<System.Windows.Forms.Timer> _activeRejectTimers = new();

    // RegisterHotKey IDs used by the conflict-probe in ValidateHotkeysBeforeApply.
    // These same IDs are unregistered defensively in Dispose in case the probe
    // throws between Register and Unregister.
    private const int PROBE_ID_MAIN = 0x7A1D;
    private const int PROBE_ID_DEAFEN = 0x7A1E;

    public SettingsDialog(Config config, Action onApply)
    {
        _config = config;
        _onApply = onApply;

        Text = "MicMute v" + Config.Version + " — Settings";
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.BgColor;
        ForeColor = Theme.FgColor;
        // DPI scaling is done explicitly in OnLoad (UiLayout.ApplyDpi + content-fit) rather
        // than by the framework: AutoScaleMode.Dpi under PerMonitorV2 scales point-fonts but
        // NOT the pixel margins/field widths inside layout containers (and inconsistently
        // between forms), which left 150% proportionally tighter than 100% — clipped fields,
        // compressed spacing. None = no framework scaling; point-fonts still grow on their
        // own, and ApplyDpi scales every pixel literal so 150% is exactly 100% x 1.5.
        AutoScaleMode = AutoScaleMode.None;
        Font = UiLayout.BodyFont;

        _stack = new UiLayout.Stack(this);
        var stack = _stack;

        // ── Hotkeys (two compact cells side by side) ──
        _capturedMainHK = config.Hotkey;
        _capturedDeafenHK = config.DeafenHotkey;
        var hotkeys = stack.NewSection("Hotkeys");
        hotkeys.Grid2(
            BuildHotkeyCell("Toggle Mute", () => _capturedMainHK, v => _capturedMainHK = v,
                bareKeysAllowed: () => _config.Mode == "push-to-talk"),
            BuildHotkeyCell("Deafen Mute", () => _capturedDeafenHK, v => _capturedDeafenHK = v,
                bareKeysAllowed: () => false));
        hotkeys.Hint("Toggle Mute: mutes / unmutes your mic.\nIn Push-to-Talk mode, hold to talk.");
        hotkeys.Hint("Deafen: mutes your mic AND your speakers at the same time.");

        // ── Behavior ──
        var behavior = stack.NewSection("Behavior");
        _chkSoundFeedback = behavior.Check(Fields.Check("Sound feedback on mute/unmute"));
        _chkSoundFeedback.Checked = config.SoundFeedback;

        // OSD row: checkbox (left) + "Duration (ms):" label + numeric (right edge).
        _chkOsd = Fields.Check("On-screen display bubble on mute/unmute");
        _chkOsd.Checked = config.OsdEnabled;
        _edtOsdDuration = Fields.Numeric(500, 10000, config.OsdDuration, 100, UiTokens.OsdDurationWidth);
        behavior.EdgeRow(_chkOsd, UiLayout.LabelBefore("Duration (ms):", _edtOsdDuration, dim: true));

        _chkMuteLock = behavior.Check(Fields.Check("Mute Lock"));
        _chkMuteLock.Checked = config.MuteLock;
        behavior.Hint("Reverts external mute changes every 15 seconds (not instant).", indentPx: 14);

        // Auto-mute row: checkbox (left) + "After (min):" label + numeric (right edge).
        _chkAutoMute = Fields.Check("Auto-mute when idle during a call");
        _chkAutoMute.Checked = config.AutoMuteEnabled;
        _edtAutoMuteMinutes = Fields.Numeric(AutoMute.MinMinutes, AutoMute.MaxMinutes,
            config.AutoMuteMinutes, 1, UiTokens.OsdDurationWidth);
        behavior.EdgeRow(_chkAutoMute, UiLayout.LabelBefore("After (min):", _edtAutoMuteMinutes, dim: true));
        behavior.Hint("Mutes after no keyboard or mouse input while a call app uses the mic.\nToggle mode only. It doesn't unmute automatically.", indentPx: 14);

        _chkMiddleClick = behavior.Check(Fields.Check("Middle-click tray icon to toggle Toggle/PTT mode"));
        _chkMiddleClick.Checked = config.MiddleClickToggle;

        // Run-at-startup (left) + Mic-mode-on-startup dropdown (right edge).
        string startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MicMute.lnk");
        _chkRunAtStartup = Fields.Check("Run at startup");
        _chkRunAtStartup.Checked = File.Exists(startupPath);
        var startWrap = Fields.Combo(out _ddlStartMuted, UiTokens.DropdownWidth,
            "Don't change", "Always muted", "Always unmuted", "Remember last");
        _ddlStartMuted.SelectedIndex = config.StartMuted switch
        {
            "yes" => 1,
            "unmuted" => 2,
            "last" => 3,
            _ => 0,
        };
        behavior.EdgeRow(_chkRunAtStartup, UiLayout.LabelBefore("Mic mode On Startup:", startWrap));

        // PTT mode force-mutes on startup regardless of the dropdown — say so.
        if (config.Mode == "push-to-talk")
            behavior.Hint("Push-to-Talk mode always starts muted.");

        // ── Appearance (restart-to-apply theme pin) ──
        var appearance = stack.NewSection("Appearance");
        var themeWrap = Fields.Combo(out _ddlTheme, UiTokens.DropdownWidth, "System", "Dark", "Light");
        // FindStringExact is case-INSENSITIVE; the null/empty guard avoids the
        // ArgumentNullException a corrupt/partial-write ThemeMode could trigger.
        int themeIdx = string.IsNullOrEmpty(config.ThemeMode)
            ? -1
            : _ddlTheme.FindStringExact(config.ThemeMode);
        _ddlTheme.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;
        appearance.EdgeRow(
            new Label { Text = "Theme:", AutoSize = true, ForeColor = Theme.FgColor, Margin = new Padding(0, 4, 0, 0) },
            themeWrap);

        // ── Custom Files (two compact cells) ──
        var customFiles = stack.NewSection("Custom Files");
        _pathIconMuted = config.IconMuted;
        _pathIconActive = config.IconActive;
        customFiles.Grid2(
            BuildFileCell("Muted icon", () => _pathIconMuted, v => _pathIconMuted = v,
                "Icon files (*.ico)|*.ico", out _lblIconMuted),
            BuildFileCell("Active icon", () => _pathIconActive, v => _pathIconActive = v,
                "Icon files (*.ico)|*.ico", out _lblIconActive));

        // ── Footer (nav links left, action buttons right) ──
        var lnkGitHub = Fields.Nav("GitHub");
        lnkGitHub.LinkClicked += (_, _) =>
        {
            // Process.Start with UseShellExecute=true throws when no default browser is
            // registered, the URL is blocked by Group Policy, or ShellExecute errors.
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/itsnateai/MicMute",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception
                   or InvalidOperationException
                   or System.IO.FileNotFoundException
                   or UnauthorizedAccessException)
            {
                Log.Warn("Open GitHub URL failed: " + ex.Message);
                // Passing `this` as owner while mid-dispose produces a broken modal.
                IWin32Window owner = (IsHandleCreated && !IsDisposed) ? (IWin32Window)this : null;
                MessageBox.Show(owner,
                    "Couldn’t open the GitHub page in your browser.\n\n" +
                    "Details: " + ex.Message,
                    "MicMute — Open URL",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var lnkHelp = Fields.Nav("Help");
        lnkHelp.LinkClicked += (_, _) => HelpWindow.ShowInstance();
        var lnkUpdate = Fields.Nav("Check for updates");
        lnkUpdate.LinkClicked += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };

        var btnOK = Fields.Action("Save");
        btnOK.Click += (_, _) => { ApplySettings(); Close(); };
        AcceptButton = btnOK;
        var btnApply = Fields.Action("Apply");
        btnApply.Click += (_, _) => ApplySettings();
        var btnCancel = Fields.Action("Cancel");
        btnCancel.Click += (_, _) => Close();
        CancelButton = btnCancel;

        stack.Add(Bars.Split(
            new Control[] { lnkGitHub, lnkHelp, lnkUpdate },
            new Control[] { btnOK, btnApply, btnCancel }));

        // Placeholder client size; OnLoad sets the real one — width = the design width
        // scaled to the device, height = the laid-out content height — with real handle
        // font metrics, before the first paint (no flicker, no ctor-time underestimate
        // that would clip the footer). See OnLoad + UiLayout.ApplyDpi.
        ClientSize = new Size(DesignClientWidth, 400);
    }

    private void ApplySettings()
    {
        // Validate FIRST — atomic commit semantics. Either everything saves or nothing
        // does (pre-v2.2.5 a validation reject could leave a silent partial commit).
        if (!ValidateHotkeysBeforeApply())
            return;

        _config.SoundFeedback = _chkSoundFeedback.Checked;
        _config.OsdEnabled = _chkOsd.Checked;

        // NumericUpDown clamps + validates inherently.
        _config.OsdDuration = (int)_edtOsdDuration.Value;
        _config.MuteLock = _chkMuteLock.Checked;
        _config.AutoMuteEnabled = _chkAutoMute.Checked;
        _config.AutoMuteMinutes = (int)_edtAutoMuteMinutes.Value;
        _config.MiddleClickToggle = _chkMiddleClick.Checked;

        _config.StartMuted = _ddlStartMuted.SelectedIndex switch
        {
            1 => "yes",
            2 => "unmuted",
            3 => "last",
            _ => "no",
        };

        // Startup shortcut
        string startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MicMute.lnk");
        if (_chkRunAtStartup.Checked && !File.Exists(startupPath))
        {
            try { ShortcutHelper.CreateShortcut(startupPath, Environment.ProcessPath ?? ""); }
            catch (Exception ex)
            {
                Log.Warn("Create startup shortcut failed: " + ex.Message);
                MessageBox.Show(this,
                    "Couldn’t create the startup shortcut in your Startup folder.\n\n" +
                    "“Run at startup” is enabled in Settings but the .lnk couldn’t " +
                    "be written. MicMute won’t auto-start until this is resolved.\n\n" +
                    "Details: " + ex.Message,
                    "MicMute — Startup shortcut",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else if (!_chkRunAtStartup.Checked && File.Exists(startupPath))
            try { File.Delete(startupPath); }
            catch (Exception ex)
            {
                Log.Warn("Delete startup shortcut failed: " + ex.Message);
                MessageBox.Show(this,
                    "Couldn’t remove the startup shortcut from your Startup folder.\n\n" +
                    "MicMute will still start with Windows until the shortcut is removed manually.\n\n" +
                    "Details: " + ex.Message,
                    "MicMute — Startup shortcut",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

        // Hotkeys — committed post-validation. ValidateHotkeysBeforeApply ran at the
        // very top (atomic-commit guarantee); pending sentinels carry its ack state.
        _config.Hotkey = _capturedMainHK;
        _config.DeafenHotkey = _capturedDeafenHK;
        if (_pendingAckedMainHk != null) _config.AckedMainHkConflict = _pendingAckedMainHk;
        if (_pendingAckedDeafenHk != null) _config.AckedDeafenHkConflict = _pendingAckedDeafenHk;

        // Custom files — SanitizePath is belt-and-braces (ValidateCustomFile already
        // rejects UNC at file-pick; Save-time sanitization keeps Config the single
        // source of truth). MuteSound / UnmuteSound are NOT touched here.
        _config.IconMuted = Config.SanitizePath((_pathIconMuted ?? "").Trim());
        _config.IconActive = Config.SanitizePath((_pathIconActive ?? "").Trim());

        // Appearance — theme pin is restart-to-apply; TrayApp.OnSettingsApplied detects
        // the is-dark flip post-Save and spawns a replacement process.
        _config.ThemeMode = (_ddlTheme.SelectedItem as string) ?? "System";

        bool saved = _config.Save();
        _onApply();
        if (!saved)
        {
            MessageBox.Show(this,
                "Settings were applied to the current session, but couldn't be written to MicMute.ini. " +
                "Your changes will be lost on next launch.\n\n" +
                "Check that MicMute has permission to write to its config folder.",
                "MicMute — Settings not saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Run all hotkey validation checks before ApplySettings commits. Returns
    /// false if the user aborted (and Apply should back out).
    /// Checks: parse-ability, duplicate between Toggle+Deafen, RegisterHotKey
    /// conflict with other apps (best-effort), risky bindings in PTT mode.
    /// </summary>
    private bool ValidateHotkeysBeforeApply()
    {
        bool pttMode = _config.Mode == "push-to-talk";

        // Reset the pending-ack sentinels at the top of every Validate run so a stale
        // value can't survive across Apply attempts when the user reverts a captured
        // combo back to its prior _config value (the deferred-commit pattern below:
        // null = leave alone, "" = clear on Apply, value = set on Apply).
        _pendingAckedMainHk = null;
        _pendingAckedDeafenHk = null;

        // Parse check — reject invalid combos up front.
        if (!string.IsNullOrEmpty(_capturedMainHK) &&
            !Config.ParseHotkey(_capturedMainHK, out uint mMods, out uint mVk, allowBare: pttMode))
        {
            MessageBox.Show(this,
                "Toggle Mute hotkey \"" + Config.HotkeyToReadable(_capturedMainHK) + "\" isn’t a valid binding.\n\n" +
                (pttMode
                    ? "In Push-to-Talk mode, bare keys are allowed only if they’re modifiers (LCtrl, RShift, etc.) or function keys."
                    : "Bare keys aren’t allowed in Toggle mode — add at least one of Ctrl, Alt, Shift, or Win, or switch to Push-to-Talk."),
                "MicMute — Invalid hotkey",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (!string.IsNullOrEmpty(_capturedDeafenHK) &&
            !Config.ParseHotkey(_capturedDeafenHK, out uint dMods, out uint dVk, allowBare: false))
        {
            MessageBox.Show(this,
                "Deafen Mute hotkey \"" + Config.HotkeyToReadable(_capturedDeafenHK) + "\" isn’t a valid binding.\n\n" +
                "Deafen uses Windows’ global hotkey system, which needs at least one of Ctrl, Alt, Shift, or Win.",
                "MicMute — Invalid hotkey",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Duplicate check — same combo on both hotkeys silently fights at registration.
        // Compare by parsed (mods, vk) so "^F1" and "ctrl+F1" count as the same binding.
        if (!string.IsNullOrEmpty(_capturedMainHK) &&
            !string.IsNullOrEmpty(_capturedDeafenHK) &&
            Config.ParseHotkey(_capturedMainHK, out uint dupMM, out uint dupMV, allowBare: pttMode) &&
            Config.ParseHotkey(_capturedDeafenHK, out uint dupDM, out uint dupDV, allowBare: false) &&
            (dupMM & ~NativeMethods.MOD_NOREPEAT) == (dupDM & ~NativeMethods.MOD_NOREPEAT) && dupMV == dupDV)
        {
            MessageBox.Show(this,
                "Toggle Mute and Deafen are both set to \"" + Config.HotkeyToReadable(_capturedMainHK) + "\".\n\n" +
                "Windows can’t route one key press to two different actions — pick a different combo for one of them.",
                "MicMute — Duplicate hotkey",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Best-effort conflict probe using RegisterHotKey on this dialog's HWND.
        // Limitation: apps that intercept keys via low-level keyboard hooks (Discord PTT,
        // PowerToys) won't be detected. Courtesy warning, not a hard block.
        // Self-conflict guard: skip the probe when the combo matches the one TrayApp
        // already owns (RegisterHotKey is unique per (mod, vk) per process — a second
        // call would fail and produce a false "claimed by another app" warning).
        bool mainUnchanged = _capturedMainHK.Equals(_config.Hotkey, StringComparison.OrdinalIgnoreCase);
        bool deafenUnchanged = _capturedDeafenHK.Equals(_config.DeafenHotkey, StringComparison.OrdinalIgnoreCase);
        if (!mainUnchanged &&
            !string.IsNullOrEmpty(_capturedMainHK) &&
            Config.ParseHotkey(_capturedMainHK, out uint probeMMods, out uint probeMVk, allowBare: pttMode))
        {
            bool ok = false;
            try   { ok = NativeMethods.RegisterHotKey(Handle, PROBE_ID_MAIN, probeMMods, probeMVk); }
            finally { if (ok) NativeMethods.UnregisterHotKey(Handle, PROBE_ID_MAIN); }
            if (ok)
            {
                // Probe succeeded — clear any stale ack so a future real conflict re-warns.
                _pendingAckedMainHk = "";
            }
            else if (_capturedMainHK.Equals(_config.AckedMainHkConflict, StringComparison.OrdinalIgnoreCase))
            {
                // User already confirmed "use it anyway" for this exact combo; stay silent.
                _pendingAckedMainHk = _capturedMainHK;
            }
            else
            {
                var res = MessageBox.Show(this,
                    "Toggle Mute hotkey \"" + Config.HotkeyToReadable(_capturedMainHK) + "\" appears to be claimed by another app " +
                    "(detected via RegisterHotKey probe).\n\n" +
                    "Note: apps using low-level keyboard hooks (e.g. Discord PTT, PowerToys) won’t be detected by this probe.\n\n" +
                    "MicMute may lose the race, or both apps may fire at once. Use it anyway?",
                    "MicMute — Hotkey conflict",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (res != DialogResult.Yes) return false;
                _pendingAckedMainHk = _capturedMainHK;
            }
        }
        if (!deafenUnchanged &&
            !string.IsNullOrEmpty(_capturedDeafenHK) &&
            Config.ParseHotkey(_capturedDeafenHK, out uint probeDMods, out uint probeDVk, allowBare: false))
        {
            bool ok = false;
            try   { ok = NativeMethods.RegisterHotKey(Handle, PROBE_ID_DEAFEN, probeDMods, probeDVk); }
            finally { if (ok) NativeMethods.UnregisterHotKey(Handle, PROBE_ID_DEAFEN); }
            if (ok)
            {
                _pendingAckedDeafenHk = "";
            }
            else if (_capturedDeafenHK.Equals(_config.AckedDeafenHkConflict, StringComparison.OrdinalIgnoreCase))
            {
                _pendingAckedDeafenHk = _capturedDeafenHK;
            }
            else
            {
                var res = MessageBox.Show(this,
                    "Deafen Mute hotkey \"" + Config.HotkeyToReadable(_capturedDeafenHK) + "\" appears to be claimed by another app.\n\n" +
                    "Note: low-level keyboard hook apps won’t be detected by this probe.\n\n" +
                    "Use it anyway?",
                    "MicMute — Hotkey conflict",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (res != DialogResult.Yes) return false;
                _pendingAckedDeafenHk = _capturedDeafenHK;
            }
        }

        // Risky-key check — only for the Toggle hotkey in PTT mode (bare letters, Space,
        // common Ctrl-shortcuts that fire during normal typing).
        if (pttMode && !string.IsNullOrEmpty(_capturedMainHK) &&
            Config.ParseHotkey(_capturedMainHK, out uint riskMods, out uint riskVk, allowBare: true) &&
            Config.IsRiskyHotkey(riskMods, riskVk))
        {
            var res = MessageBox.Show(this,
                "\"" + Config.HotkeyToReadable(_capturedMainHK) + "\" is a key you’ll press during normal use.\n\n" +
                "In Push-to-Talk mode, your mic will open every time you press it — in every app, not just voice chat. Use it anyway?",
                "MicMute — Risky PTT key",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (res != DialogResult.Yes) return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a compact "[label] [display] [×]" hotkey cell with inline capture —
    /// clicking the display box enters "recording" mode (yellow background), pressing
    /// a key/combo captures in place, Escape cancels, clicking elsewhere or Tab commits.
    /// Returns the cell for placement in the Hotkeys grid.
    /// </summary>
    private Control BuildHotkeyCell(
        string labelText,
        Func<string> getCaptured,
        Action<string> setCaptured,
        Func<bool> bareKeysAllowed)
    {
        var display = Fields.Display();
        var btnClear = Fields.Icon("×");

        _fileRowTooltip ??= UiFactory.MakeThemedToolTip();
        _fileRowTooltip.SetToolTip(btnClear, "Clear hotkey");
        _fileRowTooltip.SetToolTip(display, "Click and press a key combo to record");

        bool captureMode = false;
        string preCaptureValue = null;
        // Per-cell in-flight rejection animation timer. Tracking one per cell so a new
        // press cancels the previous animation cleanly (rapid bare-modifier mashing
        // would otherwise stack timers that tick over each other).
        System.Windows.Forms.Timer rowRejectTimer = null;

        void Refresh()
        {
            string v = getCaptured();
            bool empty = string.IsNullOrEmpty(v);
            string readable = empty ? "(not set)" : Config.HotkeyToReadable(v);
            display.Text = readable;
            display.ForeColor = empty ? UiTokens.GreyTextColor : Theme.FgColor;
            display.BackColor = captureMode ? UiTokens.FocusYellow : Theme.EditBgColor;
            btnClear.Enabled = !empty;
        }
        Refresh();

        void EnterCapture()
        {
            if (captureMode) return;
            captureMode = true;
            preCaptureValue = getCaptured();
            // Register form-level Esc/Enter interception — otherwise Esc fires
            // CancelButton (closes Settings) and Enter fires OK.
            _capturingCancel = CancelCapture;
            _capturingCommit = () => { ActiveControl = null; };
            Refresh();
        }

        void ExitCapture()
        {
            if (!captureMode) return;
            captureMode = false;
            preCaptureValue = null;
            _capturingCancel = null;
            _capturingCommit = null;
            Refresh();
        }

        void CancelCapture()
        {
            if (!captureMode) return;
            if (preCaptureValue != null) setCaptured(preCaptureValue);
            captureMode = false;
            preCaptureValue = null;
            _capturingCancel = null;
            _capturingCommit = null;
            Refresh();
            ActiveControl = null; // blur off the field
        }

        display.Click += (_, _) => EnterCapture();
        display.Enter += (_, _) => EnterCapture();
        display.Leave += (_, _) => ExitCapture();

        display.KeyDown += (s, e) =>
        {
            // SuppressKeyPress only inside capture mode — outside capture, Escape, Enter,
            // and Tab must fall through to the form's default handling.
            if (!captureMode) return;
            e.SuppressKeyPress = true;

            // Escape cancels and restores the pre-capture value.
            if (e.KeyCode == Keys.Escape)
            {
                CancelCapture();
                return;
            }
            // Enter commits and blurs.
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                ActiveControl = null;
                return;
            }
            // Tab — suppress the typed tab char but let WinForms advance focus normally;
            // Leave will commit the current capture value.
            if (e.KeyCode == Keys.Tab)
            {
                e.SuppressKeyPress = false;
                return;
            }

            bool bare = bareKeysAllowed();

            // Bare modifier-only press (RCtrl alone, LShift alone, etc.)
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            {
                if (!bare)
                {
                    // Briefly tint the display red to signal that bare modifiers aren't
                    // accepted outside Push-to-Talk mode.
                    display.BackColor = UiTokens.ErrorTint;
                    string prevText = display.Text;
                    display.Text = "Bare modifiers need Push-to-Talk mode";
                    // Cancel any in-flight rejection animation on THIS cell before starting
                    // a new one (rapid key-mashing otherwise stacks ticking timers).
                    if (rowRejectTimer != null)
                    {
                        rowRejectTimer.Stop();
                        _activeRejectTimers.Remove(rowRejectTimer);
                        rowRejectTimer.Dispose();
                        rowRejectTimer = null;
                    }
                    var rejectTimer = new System.Windows.Forms.Timer
                    {
                        Interval = UiTokens.RejectAnimDurationMs,
                    };
                    rowRejectTimer = rejectTimer;
                    _activeRejectTimers.Add(rejectTimer);
                    rejectTimer.Tick += (_, _) =>
                    {
                        // Three races to defend against:
                        // (1) Dispose ran between WM_TIMER post and dequeue — the list no
                        //     longer contains rejectTimer; Stop on a disposed timer throws.
                        // (2) User pressed Escape during the 1800ms window — CancelCapture
                        //     re-painted the field, so restoring FocusYellow here would
                        //     strand the cell in capture-mode yellow.
                        // (3) The display TextBox was disposed — IsDisposed catches it.
                        if (!_activeRejectTimers.Remove(rejectTimer))
                            return; // Dispose-sweep or multiple-active-guard already handled it.
                        rejectTimer.Stop();
                        rejectTimer.Dispose();
                        if (rowRejectTimer == rejectTimer)
                            rowRejectTimer = null;
                        if (display.IsDisposed || !captureMode)
                            return;
                        display.Text = prevText;
                        display.BackColor = UiTokens.FocusYellow;
                    };
                    rejectTimer.Start();
                    return;
                }
                const int VK_RSHIFT = 0xA1, VK_RCONTROL = 0xA3, VK_RMENU = 0xA5;
                string side = null;
                if (e.KeyCode == Keys.ControlKey)
                    side = (NativeMethods.GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0 ? "RCtrl" : "LCtrl";
                else if (e.KeyCode == Keys.ShiftKey)
                    side = (NativeMethods.GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0 ? "RShift" : "LShift";
                else if (e.KeyCode == Keys.Menu)
                    side = (NativeMethods.GetAsyncKeyState(VK_RMENU) & 0x8000) != 0 ? "RAlt" : "LAlt";
                else if (e.KeyCode == Keys.LWin) side = "LWin";
                else if (e.KeyCode == Keys.RWin) side = "RWin";

                if (side != null)
                {
                    setCaptured(side);
                    Refresh();
                }
                return;
            }

            // Regular combo — build AHK prefix from modifiers + key name.
            string prefix = "";
            if (e.Modifiers.HasFlag(Keys.Control)) prefix += "^";
            if (e.Modifiers.HasFlag(Keys.Alt)) prefix += "!";
            if (e.Modifiers.HasFlag(Keys.Shift)) prefix += "+";
            if ((NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0)
                prefix = "#" + prefix;

            string keyName = Config.KeyCodeToName(e.KeyCode);
            if (string.IsNullOrEmpty(keyName)) return;

            setCaptured(prefix + keyName);
            Refresh();
        };

        btnClear.Click += (_, _) =>
        {
            setCaptured("");
            if (captureMode) preCaptureValue = ""; // don't re-populate on Escape
            Refresh();
        };

        return UiLayout.CompactCell(labelText, display, btnClear);
    }

    /// <summary>
    /// Builds a compact "[label] [filename] [×]" custom-file cell. The display box is its
    /// own browse button — ReadOnly, hand cursor, click opens OpenFileDialog. Path state is
    /// owned by the caller via getPath/setPath callbacks. Returns the cell; the display
    /// TextBox is surfaced via <paramref name="display"/>.
    /// </summary>
    private Control BuildFileCell(
        string labelText,
        Func<string> getPath, Action<string> setPath,
        string filter,
        out TextBox display)
    {
        bool hasFile = !string.IsNullOrEmpty(getPath());

        var fileDisplay = Fields.Display();
        fileDisplay.Text = FileLabel(getPath());
        fileDisplay.ForeColor = hasFile ? Theme.FgColor : UiTokens.GreyTextColor;

        var btnClear = Fields.Icon("×");
        btnClear.Enabled = hasFile;

        _fileRowTooltip ??= UiFactory.MakeThemedToolTip();
        _fileRowTooltip.SetToolTip(btnClear, "Reset to default");
        _fileRowTooltip.SetToolTip(fileDisplay, "Click to choose a custom file");

        void RunBrowse()
        {
            using var ofd = new OpenFileDialog { Filter = filter };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            // Validate before accepting. Hard failures block; soft warnings let the
            // user override with OK.
            var (hardFail, message) = ValidateCustomFile(ofd.FileName, filter);
            if (message != null)
            {
                if (hardFail)
                {
                    MessageBox.Show(this, message,
                        "MicMute — Can’t use this file",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var res = MessageBox.Show(this, message + "\n\nUse it anyway?",
                    "MicMute — Large file",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (res != DialogResult.OK) return;
            }

            setPath(ofd.FileName);
            fileDisplay.Text = FileLabel(ofd.FileName);
            fileDisplay.ForeColor = Theme.FgColor;
            btnClear.Enabled = true;
        }

        fileDisplay.Click += (_, _) => RunBrowse();
        // Keyboard support — ReadOnly textboxes still fire KeyDown; Enter/Space opens browse.
        fileDisplay.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.SuppressKeyPress = true;
                RunBrowse();
            }
        };

        btnClear.Click += (_, _) =>
        {
            setPath("");
            fileDisplay.Text = FileLabel("");
            fileDisplay.ForeColor = UiTokens.GreyTextColor;
            btnClear.Enabled = false;
        };

        display = fileDisplay;
        return UiLayout.CompactCell(labelText, fileDisplay, btnClear);
    }

    private static string FileLabel(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "default";
        return Path.GetFileName(path);
    }

    /// <summary>
    /// Validate a user-picked custom file before we commit it to config.
    /// Returns (true, message) for hard failures that should block selection,
    /// (false, message) for soft warnings the user can override, or
    /// (false, null) if everything looks fine.
    /// </summary>
    internal static (bool hardFail, string message) ValidateCustomFile(string path, string filter)
    {
        const long IconSoftCapBytes  = 2L * 1024 * 1024;   // 2 MB
        const long WavSoftCapBytes   = 10L * 1024 * 1024;  // 10 MB

        bool isIcon = filter.Contains(".ico", StringComparison.OrdinalIgnoreCase);
        bool isWav  = filter.Contains(".wav", StringComparison.OrdinalIgnoreCase);

        // Reject UNC / file:// before any I/O. Touching `\\server\share\foo.ico` via
        // FileInfo / new Icon(...) / SoundPlayer triggers SMB auth, leaking an NTLMv2
        // challenge. Same gate as Config.SanitizePath — keep the rules colocated there.
        if (Config.SanitizePath(path).Length == 0)
            return (true,
                "Network paths (UNC, file://) are not allowed for security reasons. " +
                "Pick a local file.");

        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) { return (true, "Couldn’t read this file: " + ex.Message); }

        if (!info.Exists)
            return (true, "That file no longer exists. Pick another.");

        if (isIcon)
        {
            // Hard check — try to load as a Windows icon. Handles bogus extensions and
            // truncated .ico files with bad image-directory entries.
            try
            {
                using var test = new Icon(path);
                _ = test.Width;
            }
            catch (Exception ex)
            {
                return (true,
                    "This file isn’t a valid Windows icon (.ico).\n\n" +
                    "MicMute needs a real multi-size Windows icon. Try converting " +
                    "your image with a tool like GIMP or an online .ico converter.\n\n" +
                    "Details: " + ex.Message);
            }

            if (info.Length > IconSoftCapBytes)
            {
                return (false,
                    $"This icon is {info.Length / 1024:N0} KB — larger than the 2 MB soft cap.\n\n" +
                    "Tray icons get rasterized down to the taskbar size anyway, so " +
                    "anything over a couple hundred KB is wasted memory.");
            }
            return (false, null);
        }

        if (isWav)
        {
            // Hard check — parse the RIFF/WAVE header + fmt chunk format code.
            // System.Media.SoundPlayer only handles PCM (1) or IEEE float (3);
            // anything else (MP3, ADPCM, µ-law) plays as silence with no error.
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> header = stackalloc byte[12];
                if (fs.Read(header) < 12 ||
                    header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' ||
                    header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E')
                {
                    return (true,
                        "This file isn’t a standard WAV (missing RIFF/WAVE header).\n\n" +
                        "MicMute uses Windows' built-in sound player, which only handles " +
                        "uncompressed PCM WAV files. Convert your audio to 16-bit PCM WAV " +
                        "(e.g. via Audacity → Export → WAV).");
                }

                // Walk chunks looking for 'fmt '. Bail if the file is malformed.
                Span<byte> chunk = stackalloc byte[8];
                Span<byte> fmtBuf = stackalloc byte[2];
                int guard = 0;
                while (fs.Read(chunk) == 8 && guard++ < 16)
                {
                    string id = System.Text.Encoding.ASCII.GetString(chunk.Slice(0, 4));
                    int chunkSize = BitConverter.ToInt32(chunk.Slice(4, 4));
                    if (id == "fmt ")
                    {
                        if (fs.Read(fmtBuf) != 2)
                            return (true, "WAV fmt chunk is truncated — file may be corrupt.");
                        int formatCode = BitConverter.ToUInt16(fmtBuf);
                        if (formatCode != 1 && formatCode != 3)
                        {
                            string fmtName = formatCode switch
                            {
                                2   => "Microsoft ADPCM",
                                6   => "A-law",
                                7   => "µ-law",
                                17  => "IMA ADPCM",
                                85  => "MP3-in-WAV",
                                _   => $"format code {formatCode}",
                            };
                            return (true,
                                $"This WAV uses {fmtName} compression, which Windows’ " +
                                "built-in sound player can’t decode.\n\n" +
                                "Re-export as 16-bit PCM WAV (Audacity: Export → WAV → " +
                                "WAV signed 16-bit PCM).");
                        }
                        goto FormatOk;
                    }
                    if (chunkSize <= 0 || chunkSize > 500_000_000)
                        return (true, "WAV has an implausible chunk size — file may be corrupt.");
                    fs.Seek(chunkSize, SeekOrigin.Current);
                }
                return (true, "Couldn’t find the WAV fmt chunk — file may be corrupt.");

                FormatOk:;
            }
            catch (Exception ex)
            {
                return (true, "Couldn’t read this WAV file: " + ex.Message);
            }

            if (info.Length > WavSoftCapBytes)
            {
                return (false,
                    $"This sound is {info.Length / (1024.0 * 1024.0):F1} MB — larger than the 10 MB soft cap.\n\n" +
                    "MicMute loads the whole file into memory each time it plays. For " +
                    "a mute/unmute toast, anything over ~500 KB is wildly oversized.");
            }
            return (false, null);
        }

        return (false, null);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Scale every pixel literal (margins, paddings, fixed field/button widths) by the
        // device factor so the whole layout is 100% x factor — nothing reflows, it just
        // scales. Then fit the dialog: width = the scaled design width; height = the
        // laid-out content height at that width (ground truth — PreferredSize underestimates
        // a Percent-column TLP). Done before the first paint → no visible resize.
        UiLayout.ApplyDpi(_stack.Root);
        int w = LogicalToDeviceUnits(DesignClientWidth);
        ClientSize = new Size(w, ClientSize.Height);
        _stack.Root.PerformLayout();
        ClientSize = new Size(w, _stack.Root.Height);
    }

    /// <summary>
    /// Intercepts Esc/Enter at the form level when a hotkey cell is in capture mode, so
    /// Esc cancels the capture (instead of closing Settings via CancelButton) and Enter
    /// commits + blurs (instead of firing OK).
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturingCancel != null && keyData == Keys.Escape)
        {
            _capturingCancel();
            return true;
        }
        if (_capturingCommit != null &&
            (keyData == Keys.Enter || keyData == Keys.Return))
        {
            _capturingCommit();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Defensively unregister probe IDs in case ValidateHotkeysBeforeApply threw
            // between RegisterHotKey and UnregisterHotKey. No-ops if not registered.
            if (IsHandleCreated)
            {
                NativeMethods.UnregisterHotKey(Handle, PROBE_ID_MAIN);
                NativeMethods.UnregisterHotKey(Handle, PROBE_ID_DEAFEN);
            }
            _fileRowTooltip?.Dispose();
            // Sweep any in-flight reject-animation timers — covers the narrow race where
            // the user dismisses Settings during the 1800ms tint window. Iterate a copy
            // because Stop()->Tick could mutate the list during the sweep.
            foreach (var t in _activeRejectTimers.ToArray())
            {
                t.Stop();
                t.Dispose();
            }
            _activeRejectTimers.Clear();
        }
        base.Dispose(disposing);
    }
}
