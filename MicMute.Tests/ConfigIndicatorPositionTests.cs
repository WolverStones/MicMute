using System.Drawing;

namespace MicMute.Tests;

[TestClass]
public class ConfigIndicatorPositionTests
{
    [TestMethod]
    public void ExeProfile_RoundTrips()
    {
        string line = Config.FormatIndicatorPosition("cs2.exe", new Point(120, -40));
        Assert.IsTrue(Config.TryParseIndicatorPosition(line, out string profile, out Point pos));
        Assert.AreEqual("cs2.exe", profile);
        Assert.AreEqual(new Point(120, -40), pos);
    }

    [TestMethod]
    public void DefaultProfile_Parses()
    {
        Assert.IsTrue(Config.TryParseIndicatorPosition("* = 6, 540", out string profile, out Point pos));
        Assert.AreEqual("*", profile);
        Assert.AreEqual(new Point(6, 540), pos);
    }

    [TestMethod]
    public void Malformed_Rejected()
    {
        Assert.IsFalse(Config.TryParseIndicatorPosition("", out _, out _));
        Assert.IsFalse(Config.TryParseIndicatorPosition("=1,2", out _, out _));
        Assert.IsFalse(Config.TryParseIndicatorPosition("game.exe=1", out _, out _));
        Assert.IsFalse(Config.TryParseIndicatorPosition("game.exe=a,b", out _, out _));
        Assert.IsFalse(Config.TryParseIndicatorPosition("; comment", out _, out _));
    }
}
