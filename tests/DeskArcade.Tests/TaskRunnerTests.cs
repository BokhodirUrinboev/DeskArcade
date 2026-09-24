using Xunit;

namespace DeskArcade.Tests;

public class TaskRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneArgumentRunsAsTyped(bool windows) =>
        Assert.Equal("npm test && npm run build", TaskRunner.ShellLine(new[] { "npm test && npm run build" }, windows));

    [Fact]
    public void QuotesArgumentsForCmd() =>
        Assert.Equal(@"dotnet test --filter ""Name~a b"" ""C:\My Files\\"" ""say \""hi\""""",
            TaskRunner.ShellLine(new[] { "dotnet", "test", "--filter", "Name~a b", @"C:\My Files\", "say \"hi\"" }, true));

    [Fact]
    public void QuotesArgumentsForSh() =>
        Assert.Equal("git commit -m 'it'\\''s done' ''",
            TaskRunner.ShellLine(new[] { "git", "commit", "-m", "it's done", "" }, false));

    [Theory]
    [InlineData("  dotnet test  ", "dotnet test")]
    [InlineData("make\tall\nclean", "make all clean")]
    [InlineData("npm run build && npm run test:e2e", "npm run build && npm run te…")]
    public void LabelsFitOnTheScoreboard(string line, string label) =>
        Assert.Equal(label, TaskRunner.Label(line));
}
