namespace MicMute;

using System.Text;

/// <summary>
/// Singleton resizable help window. Renders a structured, typographically
/// styled view of <see cref="s_helpText"/> via a <see cref="RichTextBox"/>.
/// </summary>
internal sealed class HelpWindow : Form
{
    private static HelpWindow s_instance = null!;
    private readonly RichTextBox _textBox;

    // A1-F07: static shared fonts — eliminates partial-ctor leak class entirely.
    // Statics live for process lifetime; no per-instance tracking or Dispose needed.
    // A5-F02: use UiTokens for font family and sizes where tokens exist.
    //         HelpBodySize is not yet in UiTokens — using 9.75f inline until C6 adds it.
    private static readonly Font s_titleFont  = new(UiTokens.PrimaryFont, UiTokens.HelpTitleSize, FontStyle.Bold);
    private static readonly Font s_headerFont = new(UiTokens.SemiboldFont, UiTokens.HelpHeaderSize, FontStyle.Bold);
    private static readonly Font s_bodyFont   = new(UiTokens.PrimaryFont, 9.75f);  // HelpBodySize pending C6

    private static readonly string s_helpText = @"MICMUTE — Global Microphone Mute Toggle

MicMute lets you mute and unmute your microphone system-wide using a hotkey or the tray icon. It works at the Windows audio level, so it affects all apps at once — Zoom, Discord, Teams, etc.

Green tray icon = mic is active (unmuted)
Red tray icon = mic is muted

——— BASIC USAGE ———————————————————

• Left-click the tray icon to toggle mute.
• Press your hotkey (default: Win+Shift+Ctrl+A) to toggle from anywhere.
• Right-click the tray icon for the full menu (change mode, pick a mic, open settings, etc.).
• Change your hotkey anytime via Tray → ""Change Hotkey…"" in the menu.

——— MODES ————————————————————————

Toggle (default): Press the hotkey once to mute, press again to unmute.

Push-to-Talk: Hold the hotkey to unmute. Releasing it mutes you again. Useful for noisy environments where you only want to be heard while actively speaking.

Switch modes via the tray menu (Mode → Toggle / Push-to-Talk), or enable ""Middle-click tray icon to toggle"" in Settings to quickly swap between them.

——— DEAFEN MODE ———————————————————

Deafen mutes both your microphone AND your speakers at the same time. Useful for stepping away or silencing everything quickly. Assign a hotkey in Settings under the Hotkeys section. Press again to undeafen (restores both to their previous state).

——— SETTINGS —————————————————————

Sound feedback: Plays a short tone when you mute or unmute.

On-screen display (OSD): Shows a small dark floating bubble above the taskbar when you toggle mute. The Duration setting controls how long it stays visible (minimum 500 ms).

Mute Lock: Prevents other applications from silently unmuting or muting your mic.

Auto-mute when idle: Mutes your mic after the set number of minutes without keyboard or mouse input, but only while a call app such as Discord is using a microphone. A soft chime warns 15 seconds first, so moving the mouse cancels it. It works in Toggle mode and doesn't unmute automatically.

Middle-click toggle: When enabled, middle-clicking the tray icon swaps between Toggle and Push-to-Talk mode.

Run at startup: Creates a Windows startup shortcut so MicMute launches automatically when you log in.

Mic mode On Startup: Controls what happens to your mic when MicMute starts:
  • Don't change — leaves your mic however it was.
  • Always muted — forces mic muted on launch.
  • Always unmuted — forces mic unmuted on launch.
  • Remember last — restores the mute state from your last session.

——— HOTKEYS —————————————————————

Set your Toggle Mute and Deafen hotkeys in the Settings window — click the field, press the combo.

Both support Windows key combinations (like Win+Shift+D). The INI stores combos in shorthand:
  # = Win,  ^ = Ctrl,  ! = Alt,  + = Shift
  Example: #+d means Win+Shift+D

——— CUSTOM FILES ——————————————————

Muted icon / Active icon: Replace the default red/green tray icons with your own .ico files.

Mute sound / Unmute sound: Replace the default beep tones with your own .wav files for audio feedback.

Use Browse to pick a file, or Clear to revert to the defaults.

——— MIC SOURCE ———————————————————

Right-click the tray icon → ""Mic Source"" to choose which microphone MicMute controls. By default it uses your Windows system default. If you switch mics or plug in a new one, MicMute auto-detects the change and reconnects.";

    private HelpWindow()
    {
        Text = "MicMute v" + Config.Version + " \u2014 Help";
        TopMost = true;
        BackColor = Theme.BgColor;
        ForeColor = Theme.FgColor;
        ClientSize = new Size(540, 560);
        MinimumSize = new Size(440, 360);
        StartPosition = FormStartPosition.CenterScreen;
        // DPI scaling is explicit (OnLoad scales ClientSize + the text-box bounds) — see
        // SettingsDialog for why AutoScaleMode.Dpi is not relied on under PerMonitorV2. The
        // point-based fonts grow on their own; only the pixel box needs scaling.
        AutoScaleMode = AutoScaleMode.None;

        // A5-F12: named constants for margin / gap eliminate magic numbers.
        const int margin = 18;
        const int topGap = 14;

        _textBox = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.BgColor,
            ForeColor = Theme.FgColor,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            WordWrap = true,
            TabStop = false,
            Location = new Point(margin, topGap),
            Size = new Size(ClientSize.Width - 2 * margin, ClientSize.Height - topGap - margin),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_textBox);

        RenderHelp();

        // Kill the default "all text selected on show" behaviour.
        Shown += (_, _) =>
        {
            _textBox.SelectionStart = 0;
            _textBox.SelectionLength = 0;
            _textBox.DeselectAll();
            ActiveControl = null;
        };

        FormClosed += (_, _) => s_instance = null!;
    }

    private void RenderHelp()
    {
        // A5-F02: use UiTokens colors instead of inline Color.FromArgb literals.
        var titleColor  = UiTokens.TitleColor;
        var headerColor = UiTokens.HeaderColor;
        var bodyColor   = UiTokens.BodyColor;

        _textBox.Clear();

        var body = new StringBuilder();

        void FlushBody()
        {
            if (body.Length == 0) return;
            // Collapse leading blank lines so sections don't have stacked gaps.
            var text = body.ToString().TrimStart('\r', '\n');
            body.Clear();
            if (text.Length == 0) return;
            _textBox.SelectionFont = s_bodyFont;
            _textBox.SelectionColor = bodyColor;
            _textBox.AppendText(text);
        }

        var lines = s_helpText.Replace("\r\n", "\n").Split('\n');
        bool titleWritten = false;

        foreach (var raw in lines)
        {
            if (!titleWritten)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                _textBox.SelectionFont = s_titleFont;
                _textBox.SelectionColor = titleColor;
                _textBox.AppendText(raw.Trim() + "\n\n");
                titleWritten = true;
                continue;
            }

            if (raw.StartsWith("\u2014\u2014\u2014") || raw.StartsWith("---"))
            {
                FlushBody();
                var title = raw.Trim().Trim('\u2014', '-', ' ');
                if (title.Length == 0) continue;
                _textBox.AppendText("\n");
                _textBox.SelectionFont = s_headerFont;
                _textBox.SelectionColor = headerColor;
                _textBox.AppendText(title + "\n\n");
                continue;
            }

            body.AppendLine(raw);
        }
        FlushBody();

        _textBox.SelectionStart = 0;
        _textBox.SelectionLength = 0;
    }

    // A1-F07: no per-instance font Dispose — statics live for process lifetime.
    // base.Dispose handles the RichTextBox and other managed controls.
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Scale the (only) pixel box — window size + the text-box inset — by the device
        // factor so 150% is exactly 100% x factor. The RichTextBox is anchored on all sides,
        // so this also sets its resize baseline. Point-fonts already grow on their own.
        if (DeviceDpi == 96) return;
        int m = LogicalToDeviceUnits(18);
        int t = LogicalToDeviceUnits(14);
        MinimumSize = new Size(LogicalToDeviceUnits(440), LogicalToDeviceUnits(360));
        ClientSize = new Size(LogicalToDeviceUnits(540), LogicalToDeviceUnits(560));
        _textBox.Bounds = new Rectangle(m, t, ClientSize.Width - 2 * m, ClientSize.Height - t - m);
    }

    public static void ShowInstance()
    {
        if (s_instance != null && !s_instance.IsDisposed)
        {
            s_instance.Show();
            s_instance.BringToFront();
            return;
        }
        s_instance = new HelpWindow();
        s_instance.Show();
    }
}
