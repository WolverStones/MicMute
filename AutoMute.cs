namespace MicMute;

/// <summary>
/// Pure decision logic for idle auto-mute. The Win32 and Core Audio reads live
/// in NativeMethods and AudioManager, so these rules stay unit-testable.
/// </summary>
internal static class AutoMute
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 120;
    public const int DefaultMinutes = 2;
    public const int WarningSeconds = 15;

    // An allow-list rather than "any app using the mic": voice-effect tools and
    // recorders can hold a capture stream open all day without any call running.
    public const string DefaultApps =
        "Discord,DiscordPTB,DiscordCanary,ms-teams,Teams,Zoom,slack,Telegram,WhatsApp,Signal," +
        "chrome,msedge,firefox,brave,opera,vivaldi";

    /// <summary>Parse a comma-separated process list; ".exe" and case are ignored.</summary>
    public static HashSet<string> ParseApps(string apps)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(apps))
            return result;

        foreach (string entry in apps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? entry[..^4]
                : entry;
            if (name.Length > 0)
                result.Add(name);
        }
        return result;
    }

    /// <summary>Return the first process that is a listed call app, or null if none is.</summary>
    public static string FindCallApp(IEnumerable<string> activeProcesses, ISet<string> callApps)
    {
        foreach (string process in activeProcesses)
        {
            if (callApps.Contains(process))
                return process;
        }
        return null;
    }

    /// <summary>
    /// Milliseconds since the last input. Both values are the 32-bit tick count,
    /// so unsigned subtraction stays correct across the 49.7-day wrap.
    /// </summary>
    public static uint IdleMilliseconds(uint nowTicks, uint lastInputTicks) =>
        unchecked(nowTicks - lastInputTicks);
}
