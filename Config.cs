using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MicMute;

/// <summary>
/// Reads and writes MicMute.ini configuration using the Windows INI API.
/// </summary>
internal sealed class Config
{
    public static readonly string Version = typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Settings with defaults
    public string Hotkey = "#^+a";
    public bool SoundFeedback = false;
    public string Mode = "toggle"; // "toggle" or "push-to-talk"
    public string DeviceId = "";
    public string IconMuted = "";
    public string IconActive = "";
    public string MuteSound = "";
    public string UnmuteSound = "";
    public bool MuteLock;
    public bool OsdEnabled;
    public int OsdDuration = 800;
    public string DeafenHotkey = "";
    // User-acknowledged hotkey conflicts — if the captured hotkey matches these
    // exactly, skip the "claimed by another app" warning on Apply/Save.
    public string AckedMainHkConflict = "";
    public string AckedDeafenHkConflict = "";
    public bool MiddleClickToggle = true;
    public string StartMuted = "no"; // "no", "yes", "unmuted", "last"
    public bool LastMuteState;
    // "System" (default — follow Windows), "Dark", or "Light". Unknown values
    // resolve to System via Theme.ResolveIsDark. Affects window chrome only
    // (Settings, Help, Update, OSD, tooltips, tray menu) — tray icons always
    // render against the user's actual taskbar regardless of this setting.
    public string ThemeMode = "System";
    // Always-on status indicator (red dot while muted). Positions are stored
    // per foreground executable in the [IndicatorPositions] section so the dot
    // can dodge each game's HUD; "*" is the fallback for everything else.
    public bool IndicatorEnabled = true;
    public bool IndicatorShowWhenLive;
    public int IndicatorSize = 18;
    // "icon" draws the mic_on/mic_off (or custom) icon, "dot" the plain coloured dot.
    public string IndicatorStyle = "icon";
    public string IndicatorEditHotkey = "#^+i";
    public Dictionary<string, Point> IndicatorPositions = new(StringComparer.OrdinalIgnoreCase);
    public const string IndicatorDefaultProfile = "*";
    private const string IndicatorSection = "IndicatorPositions";
    // Opt-in: poll-based PTT via GetAsyncKeyState instead of RegisterHotKey.
    // Works over fullscreen-exclusive games and supports bare-modifier keys
    // (LCtrl / RCtrl / RShift / etc.). No keyboard hook is installed — the
    // key state is read passively the same way games themselves read it,
    // so there's no anti-cheat signature.

    private readonly string _iniPath;

    // Pre-compiled regex for hotkey parsing
    // s_modifierPrefix captures recognized modifier symbols for parsing
    // s_stripModifiers strips ALL AHK prefix symbols including ~*$ (passthrough/wildcard/hook)
    private static readonly Regex s_modifierPrefix = new(@"^[<>#^!+~*$]+", RegexOptions.Compiled);
    private static readonly Regex s_stripModifiers = new(@"^[<>#^!+~*$]+", RegexOptions.Compiled);

    // Signal from Load() → Save() that a legacy-format key was rewritten
    // mid-load and should be persisted. One Save() handles all migrations
    // rather than one WriteIni per key.
    private bool _migrationPending;

    // Serializes concurrent Save() calls (e.g. MuteLock fight-back racing
    // Settings Apply). The unique-per-call tmp suffix below means parallel
    // calls won't clobber each other's temp file even without the lock, but
    // the lock ensures the final File.Move is also serialized.
    private static readonly object _saveLock = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(
        string lpAppName, string lpKeyName, string lpDefault,
        StringBuilder lpReturnedString, uint nSize, string lpFileName);

    public Config()
    {
        _iniPath = ResolveIniPath();
    }

    private static bool IsDirectoryWritable(string dir)
    {
        string probe = Path.Combine(dir, $".micmute_write_test_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveIniPath()
    {
        const string iniName = "MicMute.ini";

        string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(exeDir))
            exeDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Existing ini next to exe — backwards compat for portable users
        string portablePath = Path.Combine(exeDir, iniName);
        if (File.Exists(portablePath))
            return portablePath;

        // 2. Existing ini in %APPDATA%\MicMute\
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMute");
        string appDataPath = Path.Combine(appDataDir, iniName);
        if (File.Exists(appDataPath))
            return appDataPath;

        // 3. Winget install — create in %APPDATA%\MicMute\
        if (UpdateDialog.IsWingetManaged())
        {
            try { Directory.CreateDirectory(appDataDir); }
            catch (Exception ex)
            {
                Log.Warn($"ResolveIniPath: could not create AppData dir '{appDataDir}': {ex.Message}; falling back to portable path");
                return portablePath;
            }
            return appDataPath;
        }

        // 4. Traditional portable — create next to exe, but only if the
        //    directory is writable. If not (e.g. Program Files without
        //    elevation), fall back to %APPDATA%\MicMute\ silently.
        if (!string.IsNullOrEmpty(exeDir) && IsDirectoryWritable(exeDir))
            return portablePath;

        // Fall back: exeDir not writable (Program Files / read-only share)
        try { Directory.CreateDirectory(appDataDir); }
        catch (Exception ex)
        {
            Log.Warn($"ResolveIniPath: could not create AppData dir '{appDataDir}': {ex.Message}; using portable path as last resort");
            return portablePath;
        }
        Log.Info($"ResolveIniPath: exe dir not writable, redirecting config to '{appDataPath}'");
        return appDataPath;
    }

    public void Load()
    {
        if (!File.Exists(_iniPath))
            return;

        SweepOrphanTmps();
        FixEncoding();

        Hotkey = MigrateLegacyHotkey(ReadIni("Hotkey", Hotkey));
        SoundFeedback = ReadIni("SoundFeedback", "0") == "1";
        string rawMode = ReadIni("Mode", Mode).Trim();
        if (rawMode == "toggle" || rawMode == "push-to-talk")
            Mode = rawMode;
        else
        {
            Log.Warn($"Config: Mode value '{rawMode}' invalid, using 'toggle'");
            Mode = "toggle";
        }
        DeviceId = ReadIni("DeviceId", "").Trim();
        IconMuted = SanitizePath(ReadIni("IconMuted", "").Trim());
        IconActive = SanitizePath(ReadIni("IconActive", "").Trim());
        MuteSound = SanitizePath(ReadIni("MuteSound", "").Trim());
        UnmuteSound = SanitizePath(ReadIni("UnmuteSound", "").Trim());
        MuteLock = ReadIni("MuteLock", "0") == "1";
        OsdEnabled = ReadIni("OSD_Enabled", "0") == "1";
        string rawOsdDur = ReadIni("OSD_Duration", "800");
        if (int.TryParse(rawOsdDur, out int dur))
            OsdDuration = Math.Clamp(dur, 500, 10000);
        else
            Log.Warn($"Config: OSD_Duration value '{rawOsdDur}' invalid, using default 800");
        DeafenHotkey = MigrateLegacyHotkey(ReadIni("DeafenHotkey", "").Trim());
        AckedMainHkConflict = ReadIni("AckedMainHkConflict", "").Trim();
        AckedDeafenHkConflict = ReadIni("AckedDeafenHkConflict", "").Trim();
        MiddleClickToggle = ReadIni("MiddleClickToggle", "1") == "1";
        string rawStartMuted = ReadIni("StartMuted", "no").Trim().ToLowerInvariant();
        if (rawStartMuted == "no" || rawStartMuted == "yes" || rawStartMuted == "unmuted" || rawStartMuted == "last")
            StartMuted = rawStartMuted;
        else
        {
            Log.Warn($"Config: StartMuted value '{rawStartMuted}' invalid, using 'no'");
            StartMuted = "no";
        }
        LastMuteState = ReadIni("LastMuteState", "0") == "1";

        // ThemeMode: canonicalise case so the SettingsDialog dropdown's
        // case-sensitive IndexOf("System"/"Dark"/"Light") stays honest after
        // a hand-edited `ThemeMode=dark`. Unknown values fall back to System.
        string rawTheme = ReadIni("ThemeMode", "System").Trim();
        if (string.Equals(rawTheme, "Dark", StringComparison.OrdinalIgnoreCase))
            ThemeMode = "Dark";
        else if (string.Equals(rawTheme, "Light", StringComparison.OrdinalIgnoreCase))
            ThemeMode = "Light";
        else
            ThemeMode = "System";

        IndicatorEnabled = ReadIni("Indicator_Enabled", "1") == "1";
        IndicatorShowWhenLive = ReadIni("Indicator_ShowWhenLive", "0") == "1";
        if (int.TryParse(ReadIni("Indicator_Size", "18"), out int indSize))
            IndicatorSize = Math.Clamp(indSize, 6, 64);
        IndicatorStyle = ReadIni("Indicator_Style", "icon").Trim().ToLowerInvariant() == "dot" ? "dot" : "icon";
        IndicatorEditHotkey = ReadIni("Indicator_EditHotkey", IndicatorEditHotkey).Trim();
        LoadIndicatorPositions();

        // Persist any v2.1.5 → v2.1.6 bare-hotkey migrations so the next
        // launch reads the already-valid value (and so the user's "change
        // hotkey" flow doesn't revert to the original legacy string).
        if (_migrationPending)
        {
            if (Save())
                _migrationPending = false;
            else
                Log.Warn("Migration save failed; will retry on next launch");
        }
    }

    /// <summary>
    /// v2.1.6 tightened ParseHotkey to reject modifier-less bindings (which
    /// would hijack that key globally). This breaks v2.1.5 users whose INI
    /// had bare hotkeys like "Pause" or "PrintScreen". On first load of a
    /// legacy INI, rewrite these with a safe "Ctrl+Shift+" prefix so their
    /// hotkey keeps working instead of silently falling back to tray-only.
    /// </summary>
    private string MigrateLegacyHotkey(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (ParseHotkey(value, out _, out _))
            return value; // already valid under v2.1.6 rules

        // Try prepending Ctrl+Shift. If that parses, the original was just
        // missing modifiers — salvageable. If it still fails, the config is
        // genuinely unparseable and RegisterMainHotkey will surface an error.
        string migrated = "^+" + value;
        if (!ParseHotkey(migrated, out _, out _))
            return value;

        _migrationPending = true;
        return migrated;
    }

    /// <summary>
    /// Serialize the full config as a canonical INI and replace the file
    /// atomically (write to .tmp, File.Move swap). Returns true on success.
    /// v2.1.6 and earlier used 14 sequential WritePrivateProfileString
    /// calls per Save(), which could corrupt the INI if the system crashed
    /// mid-sequence — leaving users with truncated config on reboot.
    /// </summary>
    public bool Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[General]");
        sb.AppendLine("Hotkey=" + Hotkey);
        sb.AppendLine("SoundFeedback=" + (SoundFeedback ? "1" : "0"));
        sb.AppendLine("Mode=" + Mode);
        sb.AppendLine("DeviceId=" + DeviceId);
        sb.AppendLine("IconMuted=" + IconMuted);
        sb.AppendLine("IconActive=" + IconActive);
        sb.AppendLine("MuteSound=" + MuteSound);
        sb.AppendLine("UnmuteSound=" + UnmuteSound);
        sb.AppendLine("MuteLock=" + (MuteLock ? "1" : "0"));
        sb.AppendLine("OSD_Enabled=" + (OsdEnabled ? "1" : "0"));
        sb.AppendLine("OSD_Duration=" + OsdDuration.ToString());
        sb.AppendLine("DeafenHotkey=" + DeafenHotkey);
        sb.AppendLine("AckedMainHkConflict=" + AckedMainHkConflict);
        sb.AppendLine("AckedDeafenHkConflict=" + AckedDeafenHkConflict);
        sb.AppendLine("MiddleClickToggle=" + (MiddleClickToggle ? "1" : "0"));
        sb.AppendLine("StartMuted=" + StartMuted);
        sb.AppendLine("LastMuteState=" + (LastMuteState ? "1" : "0"));
        sb.AppendLine("ThemeMode=" + ThemeMode);
        sb.AppendLine("Indicator_Enabled=" + (IndicatorEnabled ? "1" : "0"));
        sb.AppendLine("Indicator_ShowWhenLive=" + (IndicatorShowWhenLive ? "1" : "0"));
        sb.AppendLine("Indicator_Size=" + IndicatorSize.ToString());
        sb.AppendLine("Indicator_Style=" + IndicatorStyle);
        sb.AppendLine("Indicator_EditHotkey=" + IndicatorEditHotkey);
        sb.AppendLine();
        sb.AppendLine("[" + IndicatorSection + "]");
        foreach (var kv in IndicatorPositions)
            sb.AppendLine(FormatIndicatorPosition(kv.Key, kv.Value));

        lock (_saveLock)
        {
            return WriteAtomic(sb.ToString());
        }
    }

    public bool SaveLastMuteState(bool muted)
    {
        if (StartMuted != "last")
            return true;
        LastMuteState = muted;
        return Save();
    }

    /// <summary>
    /// Reads the [IndicatorPositions] section ("game.exe=x,y" per line). The
    /// INI API has no clean "enumerate keys" call, so the section is parsed by
    /// hand — FixEncoding has already normalised the file to UTF-8 by now.
    /// </summary>
    private void LoadIndicatorPositions()
    {
        IndicatorPositions.Clear();
        try
        {
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(_iniPath))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = string.Equals(line, "[" + IndicatorSection + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inSection && TryParseIndicatorPosition(line, out string profile, out Point pos))
                    IndicatorPositions[profile] = pos;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("LoadIndicatorPositions failed: " + ex.Message);
        }
    }

    internal static bool TryParseIndicatorPosition(string line, out string profile, out Point pos)
    {
        profile = "";
        pos = Point.Empty;
        int eq = line.LastIndexOf('=');
        if (eq <= 0)
            return false;
        profile = line[..eq].Trim();
        string[] parts = line[(eq + 1)..].Split(',');
        if (profile.Length == 0 || parts.Length != 2 ||
            !int.TryParse(parts[0].Trim(), out int x) || !int.TryParse(parts[1].Trim(), out int y))
            return false;
        pos = new Point(x, y);
        return true;
    }

    internal static string FormatIndicatorPosition(string profile, Point pos) =>
        profile + "=" + pos.X.ToString() + "," + pos.Y.ToString();

    /// <summary>
    /// Removes orphaned .tmp files left by interrupted saves (e.g. AV lock,
    /// process crash). Only removes files older than 10 seconds so an in-
    /// flight write from a concurrent instance is not deleted mid-write.
    /// </summary>
    private void SweepOrphanTmps()
    {
        try
        {
            string dir = Path.GetDirectoryName(_iniPath) ?? "";
            string baseName = Path.GetFileName(_iniPath);
            if (string.IsNullOrEmpty(dir))
                return;
            foreach (string f in Directory.GetFiles(dir, baseName + ".tmp*"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds > 10)
                        fi.Delete();
                }
                catch { /* best-effort per-file */ }
            }
        }
        catch { /* best-effort sweep */ }
    }

    private bool WriteAtomic(string content)
    {
        // Unique suffix per call so parallel invocations (before _saveLock
        // was added or if called directly) never clobber each other's tmp.
        string tmp = $"{_iniPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            string dir = Path.GetDirectoryName(_iniPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Write + flush to disk before the rename so a crash after
            // File.Move leaves a complete file, not a partial one.
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // Retry the Move up to 3 times on transient AV/indexer locks.
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Move(tmp, _iniPath, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
            return true; // last attempt succeeded (no exception escaped loop)
        }
        catch (Exception ex)
        {
            Log.Warn($"Config save failed ({_iniPath}): {ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* cleanup best-effort */ }
            return false;
        }
    }

    /// <summary>
    /// Converts AHK hotkey string to human-readable form.
    /// e.g. "#+a" → "Win + Shift + A"
    /// </summary>
    public static string HotkeyToReadable(string hk)
    {
        if (string.IsNullOrEmpty(hk))
            return "(none)";

        var prefixMatch = s_modifierPrefix.Match(hk);
        string prefix = prefixMatch.Success ? prefixMatch.Value : "";
        string key = s_modifierPrefix.Replace(hk, "");

        string side = prefix.Contains('<') ? "L" : prefix.Contains('>') ? "R" : "";
        var sb = new StringBuilder();
        if (prefix.Contains('#')) sb.Append(side).Append("Win+");
        if (prefix.Contains('^')) sb.Append(side).Append("Ctrl+");
        if (prefix.Contains('!')) sb.Append(side).Append("Alt+");
        if (prefix.Contains('+')) sb.Append(side).Append("Shift+");
        sb.Append(PrettifyKeyName(key));
        return sb.ToString();
    }

    private static string PrettifyKeyName(string key) => key.ToUpperInvariant() switch
    {
        "LCTRL" or "LCONTROL" => "LCtrl",
        "RCTRL" or "RCONTROL" => "RCtrl",
        "LSHIFT" => "LShift",
        "RSHIFT" => "RShift",
        "LALT" or "LMENU" => "LAlt",
        "RALT" or "RMENU" => "RAlt",
        "LWIN" => "LWin",
        "RWIN" => "RWin",
        _ => key.ToUpperInvariant(),
    };

    /// <summary>
    /// Extracts the key name from an AHK hotkey string (strips modifier symbols).
    /// </summary>
    public static string ExtractKeyName(string hk)
    {
        return s_stripModifiers.Replace(hk, "");
    }

    /// <summary>
    /// Maps a WinForms <see cref="Keys"/> value to the AHK-style key name
    /// used in our INI format. Returns empty string for keys we don't
    /// bind (media keys, browser keys, etc.). Used by the inline hotkey
    /// capture path in <see cref="SettingsDialog"/>.
    /// </summary>
    public static string KeyCodeToName(Keys key)
    {
        if (key is >= Keys.A and <= Keys.Z)
            return ((char)key).ToString().ToLowerInvariant();
        if (key is >= Keys.D0 and <= Keys.D9)
            return ((char)('0' + (key - Keys.D0))).ToString();
        if (key is >= Keys.F1 and <= Keys.F24)
            return "F" + (key - Keys.F1 + 1);
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
            return "Numpad" + (key - Keys.NumPad0);

        return key switch
        {
            Keys.Space => "Space",
            Keys.Enter or Keys.Return => "Enter",
            Keys.Tab => "Tab",
            Keys.Escape => "Escape",
            Keys.Back => "Backspace",
            Keys.Delete => "Delete",
            Keys.Insert => "Insert",
            Keys.Home => "Home",
            Keys.End => "End",
            Keys.PageUp => "PgUp",
            Keys.PageDown => "PgDn",
            Keys.Up => "Up",
            Keys.Down => "Down",
            Keys.Left => "Left",
            Keys.Right => "Right",
            Keys.CapsLock => "CapsLock",
            Keys.NumLock => "NumLock",
            Keys.Scroll => "ScrollLock",
            Keys.PrintScreen => "PrintScreen",
            Keys.Pause => "Pause",
            Keys.Add => "NumpadAdd",
            Keys.Subtract => "NumpadSub",
            Keys.Multiply => "NumpadMult",
            Keys.Divide => "NumpadDiv",
            Keys.Decimal => "NumpadDot",
            Keys.OemPeriod => ".",
            Keys.Oemcomma => ",",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemBackslash or Keys.OemPipe => @"\",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.Oemtilde => "`",
            Keys.OemQuestion => "/",
            _ => "",
        };
    }

    /// <summary>
    /// Parses AHK modifier symbols into Win32 modifier flags + virtual key code.
    /// Returns false if the key cannot be mapped. Bare keys (no modifier) are
    /// rejected by default because they would hijack every press globally; set
    /// <paramref name="allowBare"/> to true only on paths that use polling
    /// (Low-latency PTT) instead of <c>RegisterHotKey</c>.
    /// </summary>
    public static bool ParseHotkey(string hk, out uint modifiers, out uint vk, bool allowBare = false)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrEmpty(hk))
            return false;

        var prefixMatch = s_modifierPrefix.Match(hk);
        string prefix = prefixMatch.Success ? prefixMatch.Value : "";
        string keyName = s_modifierPrefix.Replace(hk, "");

        if (prefix.Contains('#')) modifiers |= NativeMethods.MOD_WIN;
        if (prefix.Contains('^')) modifiers |= NativeMethods.MOD_CONTROL;
        if (prefix.Contains('!')) modifiers |= NativeMethods.MOD_ALT;
        if (prefix.Contains('+')) modifiers |= NativeMethods.MOD_SHIFT;

        uint realMods = modifiers; // before MOD_NOREPEAT is added
        modifiers |= NativeMethods.MOD_NOREPEAT;

        vk = KeyNameToVk(keyName);
        if (vk == 0)
            return false;

        // Bare keys are only acceptable on the polling path (allowBare).
        // RegisterHotKey either rejects bare modifiers outright or — worse —
        // would hijack a generic key globally. Function keys are an exception:
        // they're rarely typed during normal use and RegisterHotKey handles
        // them. Everything else requires a modifier unless allowBare is set.
        bool isFunctionKey = vk >= 0x70 && vk <= 0x87; // VK_F1..VK_F24
        if (realMods == 0 && !isFunctionKey && !allowBare)
            return false;

        return true;
    }

    /// <summary>
    /// True when a hotkey would reasonably fire during ordinary app use
    /// (bare letter/digit/Space/Enter/Tab/Esc/Backspace, Shift+letter such
    /// as every capital letter typed, or ANY Ctrl-containing combo with a
    /// plain letter — Ctrl+A/C/V/X/Z/S/F are every-app shortcuts and so are
    /// Ctrl+Shift+letter and Ctrl+Alt+letter). L/R modifiers, function keys,
    /// and Win-combos are NOT risky (they're the happy path).
    /// </summary>
    public static bool IsRiskyHotkey(uint modifiers, uint vk)
    {
        uint realMods = modifiers & ~NativeMethods.MOD_NOREPEAT;
        bool isLetter = vk >= 'A' && vk <= 'Z';
        bool isDigit = vk >= '0' && vk <= '9';

        // Bare keys — no modifier held.
        if (realMods == 0)
        {
            if (IsBareModifierVk(vk)) return false;
            if (vk >= 0x70 && vk <= 0x87) return false; // F1-F24
            if (isLetter || isDigit) return true;
            if (vk is 0x20 or 0x0D or 0x09 or 0x1B or 0x08) return true; // Space/Enter/Tab/Esc/Backspace
            return false;
        }

        // Shift+letter — every capital letter you type.
        if (realMods == NativeMethods.MOD_SHIFT && isLetter)
            return true;

        // Any Ctrl-containing combo on a plain letter. Covers Ctrl+A, Ctrl+C,
        // Ctrl+Shift+A (VS Code), Ctrl+Alt+letter (some intl keyboards / AltGr).
        // Win-combos are intentionally NOT flagged — the user chose them.
        if ((realMods & NativeMethods.MOD_CONTROL) != 0 &&
            (realMods & NativeMethods.MOD_WIN) == 0 &&
            isLetter)
            return true;

        return false;
    }

    /// <summary>
    /// Whether the given VK is a distinct left/right modifier key. These
    /// are permitted as bare hotkeys because (a) they're unlikely to
    /// conflict with typing (nobody types "right ctrl" into a document)
    /// and (b) the low-latency PTT path specifically supports them so
    /// users can match their Discord binding.
    /// </summary>
    internal static bool IsBareModifierVk(uint vk) =>
        vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private static uint KeyNameToVk(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
            return 0;

        // Single character: A-Z, 0-9
        if (keyName.Length == 1)
        {
            char c = char.ToUpperInvariant(keyName[0]);
            if (c is >= 'A' and <= 'Z')
                return (uint)c;
            if (c is >= '0' and <= '9')
                return (uint)c;
        }

        // Function keys F1-F24
        if (keyName.StartsWith('F') && int.TryParse(keyName.AsSpan(1), out int fNum) && fNum is >= 1 and <= 24)
            return (uint)(0x6F + fNum); // VK_F1=0x70

        // Named keys
        return keyName.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" or "BS" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,
            "PRINTSCREEN" => 0x2C,
            "PAUSE" => 0x13,
            // Side-specific modifiers — usable as bare hotkeys in low-latency
            // PTT mode (Discord-style "right ctrl alone" bindings).
            "LCTRL" or "LCONTROL" => 0xA2,
            "RCTRL" or "RCONTROL" => 0xA3,
            "LSHIFT" => 0xA0,
            "RSHIFT" => 0xA1,
            "LALT" or "LMENU" => 0xA4,
            "RALT" or "RMENU" => 0xA5,
            "LWIN" => 0x5B,
            "RWIN" => 0x5C,
            "NUMPAD0" => 0x60,
            "NUMPAD1" => 0x61,
            "NUMPAD2" => 0x62,
            "NUMPAD3" => 0x63,
            "NUMPAD4" => 0x64,
            "NUMPAD5" => 0x65,
            "NUMPAD6" => 0x66,
            "NUMPAD7" => 0x67,
            "NUMPAD8" => 0x68,
            "NUMPAD9" => 0x69,
            "NUMPADADD" or "NUMPAD+" => 0x6B,
            "NUMPADSUB" or "NUMPAD-" => 0x6D,
            "NUMPADMULT" or "NUMPAD*" => 0x6A,
            "NUMPADDIV" or "NUMPAD/" => 0x6F,
            "NUMPADDOT" or "NUMPAD." => 0x6E,
            "NUMPADENTER" => 0x0D, // same VK, distinguishable by extended flag
            // VK_OEM_* punctuation — SettingsDialog inline capture returns these as bare keys
            // (US layout — other layouts map the same VK to different glyphs)
            ";" or ":" => 0xBA,      // VK_OEM_1
            "=" or "+" => 0xBB,      // VK_OEM_PLUS
            "," or "<" => 0xBC,      // VK_OEM_COMMA
            "-" or "_" => 0xBD,      // VK_OEM_MINUS
            "." or ">" => 0xBE,      // VK_OEM_PERIOD
            "/" or "?" => 0xBF,      // VK_OEM_2
            "`" or "~" => 0xC0,      // VK_OEM_3
            "[" or "{" => 0xDB,      // VK_OEM_4
            "\\" or "|" => 0xDC,     // VK_OEM_5
            "]" or "}" => 0xDD,      // VK_OEM_6
            "'" or "\"" => 0xDE,     // VK_OEM_7
            _ => 0,
        };
    }

    /// <summary>
    /// Reject path forms that reach network shares or devices — these can
    /// leak NTLM credentials over SMB (`\\server\share`, `\\?\UNC\...`)
    /// or hit NT object namespace (`\\.\pipe\...`). Also reject the
    /// `file://` URI form since SoundPlayer/Icon happily dereferences it
    /// back into the same UNC paths we're trying to block.
    /// </summary>
    internal static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        string trimmed = path.TrimStart();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return "";                                      // `\\server\share` UNC, `\\?\UNC\...`, `\\.\device`
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "";                                      // forward-slash UNC variant
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return "";                                      // `file://server/share/...` → SMB auth
        return path;
    }

    private string ReadIni(string key, string defaultValue)
    {
        // Start at 4096 and double on truncation (return == bufferSize - 1).
        // Cap at 32768 to prevent runaway on a pathological INI.
        uint bufferSize = 4096;
        const uint maxBufferSize = 32768;
        while (true)
        {
            var sb = new StringBuilder((int)bufferSize);
            uint written = GetPrivateProfileString("General", key, defaultValue, sb, bufferSize, _iniPath);
            if (written < bufferSize - 1 || bufferSize >= maxBufferSize)
                return sb.ToString().Trim();
            bufferSize = Math.Min(bufferSize * 2, maxBufferSize);
        }
    }

    /// <summary>
    /// Fix UTF-16 LE without BOM encoding issue (mirrors AHK FixIniEncoding).
    /// Rewrites atomically via .tmp swap so a crash mid-rewrite can't corrupt
    /// the INI beyond recovery.
    /// Also detects truncated or structurally empty INI files and backs them
    /// up as .corrupted so the app can start cleanly with defaults.
    /// </summary>
    private void FixEncoding()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(_iniPath);

            // Detect truncated / empty INI — too short to contain [General]
            // (minimum meaningful INI is ~20 bytes). Back it up and let Load()
            // proceed with field defaults rather than silently resetting them.
            if (bytes.Length < 20)
            {
                Log.Warn($"Config file appears truncated ({bytes.Length} bytes); backing up and using defaults.");
                TryBackupCorrupted();
                return;
            }

            // Validate [General] section header as a structural sanity check.
            // GetPrivateProfileString silently returns defaults when the section
            // is missing, which looks identical to a fresh install. Surface it.
            string textProbe = Encoding.UTF8.GetString(bytes);
            if (!textProbe.Contains("[General]", StringComparison.OrdinalIgnoreCase))
            {
                // Could be UTF-16; check before giving up.
                textProbe = Encoding.Unicode.GetString(bytes);
                if (!textProbe.Contains("[General]", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("Config file missing [General] section; backing up and using defaults.");
                    TryBackupCorrupted();
                    return;
                }
            }

            // Short-circuit: UTF-16 LE BOM present — the INI API won't read
            // it, but the content is intact. Fall through to re-encode below.
            bool hasBom = bytes[0] == 0xFF && bytes[1] == 0xFE;

            if (!hasBom)
            {
                // Stricter UTF-16LE-without-BOM heuristic: in UTF-16LE ASCII
                // text, even-indexed bytes are the character value (non-zero
                // for printable ASCII) and odd-indexed bytes are the high byte
                // (0x00 for Basic Latin). Require at least 4 of the first 8
                // even bytes non-zero AND at least 4 of the first 8 odd bytes
                // zero. This avoids misfiring on any UTF-8 file that merely
                // happens to have bytes[1] == 0x00 at some position.
                int sampleLen = Math.Min(bytes.Length, 16); // covers 8 pairs
                int evenNonZero = 0, oddZero = 0;
                for (int i = 0; i + 1 < sampleLen; i += 2)
                {
                    if (bytes[i] != 0x00) evenNonZero++;
                    if (bytes[i + 1] == 0x00) oddZero++;
                }
                bool looksUtf16Le = evenNonZero >= 4 && oddZero >= 4;
                if (!looksUtf16Le)
                    return; // already UTF-8 / ANSI — nothing to do
            }

            // UTF-16 LE (with or without BOM) — re-encode to UTF-8 (no BOM)
            // so the Windows INI API (ANSI) can read it.
            string content = Encoding.Unicode.GetString(bytes);
            if (WriteAtomic(content))
                Log.Info("FixEncoding: re-encoded UTF-16LE INI to UTF-8");
        }
        catch (Exception ex)
        {
            Log.Warn("FixEncoding failed: " + ex.Message);
        }
    }

    private void TryBackupCorrupted()
    {
        try
        {
            string backupPath = _iniPath + ".corrupted";
            File.Copy(_iniPath, backupPath, overwrite: true);
            Log.Info($"FixEncoding: corrupted INI backed up to '{backupPath}'");
        }
        catch (Exception ex)
        {
            Log.Warn("FixEncoding: could not back up corrupted INI: " + ex.Message);
        }
    }
}
