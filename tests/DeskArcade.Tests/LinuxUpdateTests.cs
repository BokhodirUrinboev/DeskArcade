using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DeskArcade.Platform.Linux;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// The display-free parts of Linux in-app updates: install-kind detection, asset names, shell quoting, the wrapper and
/// relaunch scripts (run for real with /bin/sh and fake pkexec, apt-get and dpkg on PATH), and the AppImage swap.
/// </summary>
public class LinuxUpdateTests
{
    static readonly Version V = new(1, 9, 0);

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "deskarcade-tests", "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes an executable script named like a program, so a PATH that starts with its folder finds it first.</summary>
    static string Fake(string dir, string name, string body)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        return path;
    }

    /// <summary>Runs a script with /bin/sh, with <paramref name="pathDir"/> first on PATH, and returns its exit code.</summary>
    static int Run(string script, string? pathDir = null)
    {
        var psi = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (pathDir != null) psi.Environment["PATH"] = pathDir + ":" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    // ------------------------------------------------------------------ install kind

    [Fact]
    public void TheDebIsRecognisedByItsLauncher() =>
        Assert.Equal(InstallKind.Deb, LinuxUpdate.Detect("/usr/bin/deskarcade", "/opt/deskarcade/DeskArcade", null, false, false, null));

    [Theory]
    [InlineData("/opt/deskarcade/DeskArcade")]
    [InlineData("/usr/lib/deskarcade/DeskArcade")]
    public void TheDebIsRecognisedByItsFolder(string processPath) =>
        Assert.Equal(InstallKind.Deb, LinuxUpdate.Detect(processPath, processPath, null, false, false, null));

    [Fact]
    public void AnAppImageIsRecognisedByItsEnvironmentFile()
    {
        const string file = "/home/me/Apps/DeskArcade-1.8.0-x86_64.AppImage";
        // the process runs from the mount, whose usr/lib/deskarcade must not pass for the .deb's folder
        Assert.Equal(InstallKind.AppImage, LinuxUpdate.Detect(file, "/tmp/.mount_DeskArcXYZ/usr/lib/deskarcade/DeskArcade", file, true, false, null));
    }

    [Fact]
    public void AStaleAppImageVariableDoesNotCount() =>
        Assert.Equal(InstallKind.Manual, LinuxUpdate.Detect("/home/me/DeskArcade/DeskArcade", "/home/me/DeskArcade/DeskArcade", "/gone.AppImage", false, false, null));

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "com.imperiumgames.DeskArcade")]
    public void AFlatpakIsRecognisedByTheSandboxMarks(bool infoFile, string? id) =>
        Assert.Equal(InstallKind.Flatpak, LinuxUpdate.Detect("/app/lib/deskarcade/DeskArcade", "/app/lib/deskarcade/DeskArcade", null, false, infoFile, id));

    [Fact]
    public void AnythingElseIsAManualCopy() =>
        Assert.Equal(InstallKind.Manual, LinuxUpdate.Detect("/home/me/DeskArcade/DeskArcade", "/home/me/DeskArcade/DeskArcade", null, false, false, null));

    [Fact]
    public void ThisTestRunIsNotAnInstalledCopy()
    {
        if (!OperatingSystem.IsLinux() || File.Exists("/usr/bin/deskarcade") || File.Exists("/.flatpak-info")) return;
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } || Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 }) return;
        Assert.Equal(InstallKind.Manual, UpdateChecker.InstallKind);
        Assert.False(UpdateChecker.CanInstall);
        Assert.Null(UpdateChecker.AssetNameHere(V));
    }

    // ------------------------------------------------------------------ asset names

    [Theory]
    [InlineData(InstallKind.Deb, Architecture.X64, "deskarcade_1.9.0_amd64.deb")]
    [InlineData(InstallKind.Deb, Architecture.Arm64, "deskarcade_1.9.0_arm64.deb")]
    [InlineData(InstallKind.AppImage, Architecture.X64, "DeskArcade-1.9.0-x86_64.AppImage")]
    [InlineData(InstallKind.AppImage, Architecture.Arm64, "DeskArcade-1.9.0-aarch64.AppImage")]
    [InlineData(InstallKind.WindowsSetup, Architecture.X64, "DeskArcade-Setup-1.9.0.exe")]
    [InlineData(InstallKind.WindowsSetup, Architecture.Arm64, "DeskArcade-Setup-1.9.0-arm64.exe")]
    public void TheAssetFollowsTheKindAndTheCpu(InstallKind kind, Architecture arch, string expected) =>
        Assert.Equal(expected, UpdateChecker.AssetName(kind, arch, V));

    [Fact]
    public void TheStandaloneWindowsSetupHasItsOwnName() =>
        Assert.Equal("DeskArcade-Setup-1.9.0-standalone.exe", UpdateChecker.AssetName(InstallKind.WindowsSetup, Architecture.X64, V, standalone: true));

    [Theory]
    [InlineData(InstallKind.Manual)]
    [InlineData(InstallKind.Flatpak)]
    public void KindsThatCannotInstallHaveNoAsset(InstallKind kind) => Assert.Null(UpdateChecker.AssetName(kind, Architecture.X64, V));

    [Fact]
    public void TheLinuxAssetNamesMatchTheReleaseWorkflow()
    {
        string workflow = File.ReadAllText(Path.Combine(TranslationCoverageTests.RepoRoot(), ".github", "workflows", "release.yml"));
        foreach (var kind in new[] { InstallKind.Deb, InstallKind.AppImage })
            foreach (var arch in new[] { Architecture.X64, Architecture.Arm64 })
                Assert.Contains(UpdateChecker.AssetName(kind, arch, V)!.Replace("1.9.0", "${VERSION}"), workflow);
    }

    [Fact]
    public void TheUpdatesFolderSitsBesideTheSettings() =>
        Assert.Equal(Path.Combine(Settings.DataDirectory, "updates"), UpdateChecker.UpdatesDirectory);

    // ------------------------------------------------------------------ shell quoting

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", "'it'\\''s'")]
    public void ShellQuoteWrapsInSingleQuotes(string text, string expected) => Assert.Equal(expected, LinuxUpdate.ShellQuote(text));

    [Theory]
    [InlineData("/home/o'brien/my updates/deskarcade_1.9.0_amd64.deb")]
    [InlineData("/tmp/a \"quoted\" $HOME `path` \\ with;everything|else")]
    public void ShellQuoteRoundTripsThroughSh(string text)
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), output = Path.Combine(dir, "out");
        Assert.Equal(0, Run($"printf '%s' {LinuxUpdate.ShellQuote(text)} > {LinuxUpdate.ShellQuote(output)}"));
        Assert.Equal(text, File.ReadAllText(output));
    }

    [Fact]
    public void TheInstallCommandTriesAptGetThenDpkgWithTheExactPath()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), log = Path.Combine(dir, "log");
        string deb = Path.Combine(dir, "my updates", "o'brien's deskarcade_1.9.0_amd64.deb");
        // apt-get refuses, so dpkg must run too; both record their arguments one per line
        Fake(dir, "apt-get", $"printf 'apt-get %s\\n' \"$@\" >> {LinuxUpdate.ShellQuote(log)}; exit 100");
        Fake(dir, "dpkg", $"printf 'dpkg %s\\n' \"$@\" >> {LinuxUpdate.ShellQuote(log)}");
        Assert.Equal(0, Run(LinuxUpdate.InstallCommand(deb), dir));
        Assert.Equal(new[] { "apt-get install", "apt-get -y", "apt-get --allow-downgrades", "apt-get " + deb, "dpkg -i", "dpkg " + deb }, File.ReadAllLines(log));
    }

    [Fact]
    public void PkexecGetsTheInstallCommandAsOneShellArgument()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), log = Path.Combine(dir, "log");
        string deb = Path.Combine(dir, "it's here", "deskarcade_1.9.0_amd64.deb");
        Fake(dir, "pkexec", $"printf '%s\\n' \"$@\" > {LinuxUpdate.ShellQuote(log)}; exit 126");
        Assert.Equal(126, Run(LinuxUpdate.PkexecCommand(deb), dir));
        Assert.Equal(new[] { "sh", "-c", LinuxUpdate.InstallCommand(deb) }, File.ReadAllLines(log));
        Assert.StartsWith("sudo sh -c ", LinuxUpdate.SudoCommand(deb));
    }

    // ------------------------------------------------------------------ relaunch

    [Fact]
    public void TheRelaunchCommandWaitsForThisProcessAndPassesTheProfile()
    {
        string withProfile = LinuxUpdate.RelaunchCommand("/usr/bin/deskarcade", "b", 4242);
        Assert.Contains("kill -0 4242", withProfile);
        Assert.Contains("'/usr/bin/deskarcade' --profile 'b'", withProfile);
        Assert.Contains("setsid", withProfile);
        Assert.DoesNotContain("--profile", LinuxUpdate.RelaunchCommand("/usr/bin/deskarcade", "", 4242));
    }

    [Fact]
    public async Task TheRelaunchStartsTheNewCopyOnceTheOldOneIsGone()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), started = Path.Combine(dir, "started");
        string launcher = Fake(dir, "launcher", $"printf '%s\\n' \"$@\" > {LinuxUpdate.ShellQuote(started)}");
        using var old = Process.Start(new ProcessStartInfo("sleep") { ArgumentList = { "1" }, UseShellExecute = false })!;
        Assert.Equal(0, Run(LinuxUpdate.RelaunchCommand(launcher, "b", old.Id)));
        Assert.True(old.HasExited); // the shell waited for it before starting the launcher
        for (int i = 0; i < 100 && !File.Exists(started); i++) await Task.Delay(100);
        Assert.Equal(new[] { "--profile", "b" }, File.ReadAllLines(started));
    }

    // ------------------------------------------------------------------ the wrapper

    [Fact]
    public void TheWrapperKeepsTheDownloadWhenThePromptIsDismissed()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), deb = Path.Combine(dir, "deskarcade_1.9.0_amd64.deb"), status = Path.Combine(dir, "install.status"), marker = Path.Combine(dir, "relaunched");
        File.WriteAllText(deb, "deb");
        // the privileged step is a command that returns, as pkexec does; a bare "exit" would end the wrapper itself
        string script = LinuxUpdate.WrapperScript("sh -c 'exit 126'", deb, status, $"touch {LinuxUpdate.ShellQuote(marker)}", null);
        Assert.Equal(126, Run(script));
        Assert.Equal("126", File.ReadAllText(status).Trim());
        Assert.True(File.Exists(deb));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void TheWrapperCleansUpAndRelaunchesAfterAnInstall()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), deb = Path.Combine(dir, "deskarcade_1.9.0_amd64.deb"), status = Path.Combine(dir, "install.status"), marker = Path.Combine(dir, "relaunched");
        File.WriteAllText(deb, "deb");
        string script = LinuxUpdate.WrapperScript("sh -c 'exit 0'", deb, status, $"touch {LinuxUpdate.ShellQuote(marker)}", null);
        Assert.Equal(0, Run(script));
        Assert.Equal("0", File.ReadAllText(status).Trim());
        Assert.False(File.Exists(deb));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void TheWrapperHoldsATerminalOpenOnlyWhenAsked()
    {
        string held = LinuxUpdate.WrapperScript("true", "/x.deb", "/x.status", "true", "It's over");
        Assert.Contains("read _", held);
        Assert.Contains(LinuxUpdate.ShellQuote("It's over"), held);
        Assert.DoesNotContain("read _", LinuxUpdate.WrapperScript("true", "/x.deb", "/x.status", "true", null));
    }

    // ------------------------------------------------------------------ pkexec, terminals and PATH

    [Theory]
    [InlineData("gnome-terminal", "--")]
    [InlineData("/usr/bin/gnome-terminal", "--")]
    [InlineData("konsole", "-e")]
    [InlineData("x-terminal-emulator", "-e")]
    public void EachTerminalGetsTheProgramItsOwnWay(string terminal, string flag) =>
        Assert.Equal(new[] { flag, "/tmp/install.sh" }, LinuxUpdate.TerminalArguments(terminal, "/tmp/install.sh"));

    [Fact]
    public void FindOnPathWalksTheFoldersInOrder()
    {
        static bool Exists(string p) => p is "/usr/local/bin/pkexec" or "/usr/bin/pkexec";
        Assert.Equal("/usr/local/bin/pkexec", LinuxUpdate.FindOnPath("pkexec", "/opt/bin:/usr/local/bin:/usr/bin", Exists));
        Assert.Equal("/usr/bin/pkexec", LinuxUpdate.FindOnPath("pkexec", "/usr/bin:/usr/local/bin", Exists));
        Assert.Null(LinuxUpdate.FindOnPath("pkexec", "/opt/bin", Exists));
        Assert.Null(LinuxUpdate.FindOnPath("pkexec", null, Exists));
    }

    [Theory]
    [InlineData(0, LinuxUpdate.Outcome.Installed)]
    [InlineData(126, LinuxUpdate.Outcome.Dismissed)]
    [InlineData(127, LinuxUpdate.Outcome.Dismissed)]
    [InlineData(1, LinuxUpdate.Outcome.Failed)]
    [InlineData(100, LinuxUpdate.Outcome.Failed)]
    public void ExitCodesMapToOutcomes(int code, LinuxUpdate.Outcome expected) => Assert.Equal(expected, LinuxUpdate.Classify(code));

    [Fact]
    public void WithoutPkexecOrATerminalNothingStarts()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), deb = Path.Combine(dir, "deskarcade_1.9.0_amd64.deb");
        File.WriteAllText(deb, "deb");
        Assert.Null(LinuxUpdate.StartDebInstall(deb, "", null, path: dir));
        Assert.True(File.Exists(deb));
    }

    [Fact]
    public async Task ADismissedPkexecPromptComesBackAsItsExitCode()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), deb = Path.Combine(dir, "deskarcade_1.9.0_amd64.deb");
        File.WriteAllText(deb, "deb");
        Fake(dir, "pkexec", "exit 126");
        var install = LinuxUpdate.StartDebInstall(deb, "b", null, path: dir + ":" + Environment.GetEnvironmentVariable("PATH"));
        Assert.NotNull(install);
        Assert.Equal("pkexec", install.Runner);
        Assert.False(install.ViaTerminal);
        Assert.Equal(126, await install.ExitCode);
        Assert.Equal("126", File.ReadAllText(Path.Combine(dir, LinuxUpdate.StatusFileName)).Trim());
        Assert.True(File.Exists(deb)); // kept for a manual install
    }

    [Fact]
    public async Task ATerminalReportsThroughTheStatusFile()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir(), deb = Path.Combine(dir, "deskarcade_1.9.0_amd64.deb");
        File.WriteAllText(deb, "deb");
        // a terminal that just runs the program it is handed, and a sudo that refuses
        Fake(dir, "x-terminal-emulator", "[ \"$1\" = -e ] && shift; exec \"$@\"");
        Fake(dir, "sudo", "exit 1");
        var install = LinuxUpdate.StartDebInstall(deb, "", null, path: dir + ":" + Environment.GetEnvironmentVariable("PATH"));
        Assert.NotNull(install);
        Assert.Equal("x-terminal-emulator", install.Runner);
        Assert.True(install.ViaTerminal);
        Assert.Equal(1, await install.ExitCode);
        Assert.True(File.Exists(deb));
    }

    // ------------------------------------------------------------------ progress

    [Theory]
    [InlineData(0, 24, false)]
    [InlineData(0, 25, true)]
    [InlineData(24, 26, true)]
    [InlineData(25, 49, false)]
    [InlineData(25, 50, true)]
    [InlineData(50, 75, true)]
    [InlineData(75, 99, false)]
    [InlineData(75, 100, false)]
    [InlineData(0, 100, false)]
    public void ANoticeGoesOutAtEachQuarterButNotAtTheEnd(int shown, int percent, bool expected) =>
        Assert.Equal(expected, UpdateChecker.CrossedProgressStep(shown, percent));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(50, 200, 25)]
    [InlineData(200, 200, 100)]
    [InlineData(300, 200, 100)]
    public void PercentIsWholeAndClamped(long done, long total, int expected) => Assert.Equal(expected, UpdateChecker.Percent(done, total));

    // ------------------------------------------------------------------ AppImage

    [Fact]
    public void AnAppImageIsReplacedInPlace()
    {
        if (OperatingSystem.IsWindows()) return;
        string apps = TempDir(), downloads = TempDir();
        string running = Path.Combine(apps, "DeskArcade-1.8.0-x86_64.AppImage"), downloaded = Path.Combine(downloads, "DeskArcade-1.9.0-x86_64.AppImage");
        File.WriteAllText(running, "old");
        File.WriteAllText(downloaded, "new");
        var result = LinuxUpdate.InstallAppImage(downloaded, running);
        Assert.True(result.Replaced);
        Assert.Equal(running, result.Path);
        Assert.Equal("new", File.ReadAllText(running));
        Assert.False(File.Exists(downloaded));
        Assert.False(File.Exists(running + ".new"));
        Assert.True(File.GetUnixFileMode(running).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void AnAppImageThatCannotBeReplacedIsKeptBesideItUnderItsOwnName()
    {
        if (OperatingSystem.IsWindows()) return;
        string apps = TempDir(), downloads = TempDir();
        // a folder where the running file should be: renaming over it fails, as it does on a read-only folder
        string running = Path.Combine(apps, "DeskArcade-1.8.0-x86_64.AppImage"), downloaded = Path.Combine(downloads, "DeskArcade-1.9.0-x86_64.AppImage");
        Directory.CreateDirectory(running);
        File.WriteAllText(downloaded, "new");
        var result = LinuxUpdate.InstallAppImage(downloaded, running);
        Assert.False(result.Replaced);
        Assert.Equal(Path.Combine(apps, "DeskArcade-1.9.0-x86_64.AppImage"), result.Path);
        Assert.Equal("new", File.ReadAllText(result.Path));
        Assert.True(File.GetUnixFileMode(result.Path).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.Exists(downloaded));
        Assert.False(File.Exists(running + ".new"));
    }

    // ------------------------------------------------------------------ notices

    [Theory]
    [InlineData("/home/me/.config/DeskArcade/updates", "/home/me", "~/.config/DeskArcade/updates")]
    [InlineData("/home/me", "/home/me", "~")]
    [InlineData("/home/meow/updates", "/home/me", "/home/meow/updates")]
    [InlineData("/opt/updates", null, "/opt/updates")]
    public void TidyShortensTheHomeFolder(string path, string? home, string expected) => Assert.Equal(expected, LinuxUpdate.Tidy(path, home));
}
