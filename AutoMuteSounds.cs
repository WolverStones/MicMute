using System.Media;
using System.Text;

namespace MicMute;

/// <summary>One tone that can glide in pitch, followed by optional silence.</summary>
internal readonly record struct Tone(double StartHz, double EndHz, int Ms, int GapMs = 0, double Decay = 2.5);

/// <summary>
/// Short alert sounds for idle auto-mute, generated in memory so no audio files ship.
/// Three quick high pulses warn before muting, and a falling tone confirms the mute.
/// </summary>
internal static class AutoMuteSounds
{
    private const int SampleRate = 44100;
    private const double Volume = 0.75;
    private const double AttackSeconds = 0.004;
    private const double ReleaseSlope = 12;

    private static readonly Tone s_warningPulse = new(1320, 1320, 85, GapMs: 65);

    private static readonly Lazy<SoundPlayer> s_warning = new(() =>
        CreatePlayer(BuildWav(s_warningPulse, s_warningPulse, s_warningPulse)));

    private static readonly Lazy<SoundPlayer> s_muted = new(() =>
        CreatePlayer(BuildWav(new Tone(880, 175, 520, Decay: 1.2))));

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

    /// <summary>Build a 16-bit mono PCM WAV from consecutive tones.</summary>
    public static byte[] BuildWav(params Tone[] tones)
    {
        int sampleCount = 0;
        foreach (Tone tone in tones)
            sampleCount += (tone.Ms + tone.GapMs) * SampleRate / 1000;

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

        foreach (Tone tone in tones)
        {
            int toneSamples = tone.Ms * SampleRate / 1000;
            double phase = 0;
            for (int i = 0; i < toneSamples; i++)
            {
                double progress = (double)i / toneSamples;
                phase += 2 * Math.PI * tone.StartHz * Math.Pow(tone.EndHz / tone.StartHz, progress) / SampleRate;

                double attack = Math.Min(1.0, i / (SampleRate * AttackSeconds));
                double release = Math.Min(1.0, (1.0 - progress) * ReleaseSlope);
                double envelope = attack * Math.Exp(-tone.Decay * progress) * release;
                double wave = (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase)) / 1.75;
                writer.Write((short)(wave * envelope * Volume * short.MaxValue));
            }

            int gapSamples = (tone.Ms + tone.GapMs) * SampleRate / 1000 - toneSamples;
            for (int i = 0; i < gapSamples; i++)
                writer.Write((short)0);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
