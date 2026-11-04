namespace MicMute.Tests;

[TestClass]
public class AutoMuteTests
{
    [TestMethod]
    public void ParseApps_TrimsAndStripsExe()
    {
        var apps = AutoMute.ParseApps(" Discord.exe , ms-teams,,Zoom.EXE ");
        Assert.AreEqual(3, apps.Count);
        Assert.IsTrue(apps.Contains("discord"));
        Assert.IsTrue(apps.Contains("MS-TEAMS"));
        Assert.IsTrue(apps.Contains("zoom"));
    }

    [TestMethod]
    public void ParseApps_Blank_IsEmpty()
    {
        Assert.AreEqual(0, AutoMute.ParseApps("  ").Count);
    }

    [TestMethod]
    public void DefaultApps_IncludeAllDiscordBuilds()
    {
        var apps = AutoMute.ParseApps(AutoMute.DefaultApps);
        Assert.IsTrue(apps.Contains("Discord"));
        Assert.IsTrue(apps.Contains("DiscordPTB"));
        Assert.IsTrue(apps.Contains("DiscordCanary"));
    }

    [TestMethod]
    public void FindCallApp_ReturnsListedProcess()
    {
        var apps = AutoMute.ParseApps("Discord");
        Assert.AreEqual("Discord", AutoMute.FindCallApp(new[] { "obs64", "Discord" }, apps));
    }

    [TestMethod]
    public void FindCallApp_IgnoresUnlistedProcesses()
    {
        var apps = AutoMute.ParseApps("Discord");
        Assert.IsNull(AutoMute.FindCallApp(new[] { "obs64", "audacity" }, apps));
    }

    [TestMethod]
    public void IdleMilliseconds_SimpleDifference()
    {
        Assert.AreEqual(120000u, AutoMute.IdleMilliseconds(150000u, 30000u));
    }

    [TestMethod]
    public void IdleMilliseconds_SurvivesTickWrap()
    {
        Assert.AreEqual(2000u, AutoMute.IdleMilliseconds(1000u, uint.MaxValue - 999u));
    }

    [TestMethod]
    public void WarningFitsInsideShortestLimit()
    {
        Assert.IsTrue(AutoMute.WarningSeconds < AutoMute.MinMinutes * 60);
    }

    [TestMethod]
    public void BuildWav_WritesConsistentPcmHeader()
    {
        byte[] wav = AutoMuteSounds.BuildWav((440.0, 100));
        int dataBytes = BitConverter.ToInt32(wav, 40);

        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.AreEqual(4410 * 2, dataBytes);
        Assert.AreEqual(wav.Length, 44 + dataBytes);
        Assert.AreEqual(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }

    [TestMethod]
    public void BuildWav_StaysBelowClippingAndFadesOut()
    {
        byte[] wav = AutoMuteSounds.BuildWav((880.0, 200));
        int peak = 0;
        for (int i = 44; i < wav.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));

        Assert.IsTrue(peak > 0);
        Assert.IsTrue(peak <= short.MaxValue * 0.5);
        Assert.AreEqual(0, BitConverter.ToInt16(wav, wav.Length - 2));
    }
}
