using fuseraft.Web.Services;

namespace FuseraftWeb.Tests;

public class InfoCommandTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("--urls", "http://localhost:5199", "--version")]
    public void Version_PrintsTheStampedVersion(params string[] args)
    {
        var output = new StringWriter();

        Assert.Equal(0, InfoCommand.TryRun(args, output));
        Assert.Equal(InfoCommand.Version, output.ToString().Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+", InfoCommand.Version);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_PrintsUsage(string flag)
    {
        var output = new StringWriter();

        Assert.Equal(0, InfoCommand.TryRun([flag], output));
        Assert.Contains(InfoCommand.Version, output.ToString());
        Assert.Contains("fuseraft-web users", output.ToString());
        Assert.Contains("--version", output.ToString());
    }

    [Theory]
    [InlineData()]
    [InlineData("--urls", "http://localhost:5199")]
    [InlineData("--Fuseraft:DataDirectory=/tmp/x")]
    public void OtherArguments_AreLeftForTheServer(params string[] args)
    {
        var output = new StringWriter();

        Assert.Null(InfoCommand.TryRun(args, output));
        Assert.Empty(output.ToString());
    }
}
