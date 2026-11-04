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
        Assert.AreEqual(2000u, AutoMute.IdleMilliseconds(1000u, uint.MaxValue - 998u));
    }
}
