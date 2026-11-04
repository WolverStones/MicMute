using System.Media;
using System.Text;

namespace MicMute;

/// <summary>
/// Short, soft chimes for idle auto-mute, generated in memory so no audio files ship.
/// An ascending pair warns before muting, and a descending trio confirms the mute.
/// </summary>
internal static class AutoMuteSounds
{
    private const int SampleRate = 44100;
    private const double Volume = 0.5;
    private const double AttackMs = 6;

    private static readonly Lazy<SoundPlayer> s_warning = new(() =>
        CreatePlayer(BuildWav((659.25, 170), (880.0, 240))));

    private static readonly Lazy<SoundPlayer> s_muted = new(() =>
        CreatePlayer(BuildWav((880.0, 130), (659.25, 130), (493.88, 240))));

    public static void PlayWarning() => Play(s_warning);

    public static void PlayMuted() => Play(s_muted);

    private static SoundPlayer CreatePlayer(byte[] wav)
    {
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    private static void Play(Lazy<SoundPlayer> player)
    {
        try
        {
            player.Value.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Auto-mute sound failed: " + ex.Message);
        }
    }

    /// <summary>Build a 16-bit mono PCM WAV from consecutive notes that fade out.</summary>
    public static byte[] BuildWav(params (double Hz, int Ms)[] notes)
    {
        int sampleCount = 0;
        foreach (var note in notes)
            sampleCount += SampleRate * note.Ms / 1000;

        int dataBytes = sampleCount * 2;
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);

        foreach (var note in notes)
        {
            int noteSamples = SampleRate * note.Ms / 1000;
            for (int i = 0; i < noteSamples; i++)
            {
                double seconds = (double)i / SampleRate;
                double attack = Math.Min(1.0, seconds * 1000 / AttackMs);
                double progress = (double)i / noteSamples;
                double decay = Math.Exp(-4.0 * progress) * (1.0 - progress);
                double tone = (Math.Sin(2 * Math.PI * note.Hz * seconds)
                    + 0.2 * Math.Sin(4 * Math.PI * note.Hz * seconds)) / 1.2;
                writer.Write((short)(tone * attack * decay * Volume * short.MaxValue));
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
