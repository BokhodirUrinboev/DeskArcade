using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeskArcade.Dev;
using Xunit;

namespace DeskArcade.Tests;

public class AgentSessionTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    static AgentSignal Claude(string state, string session = "s1", string folder = "C:/code/api", string kind = "", string target = "", string note = "") =>
        new(state, "claude", session, folder, kind, target, note);

    [Fact]
    public void APromptStartsATurnAndStopEndsItWithHowLongItTook()
    {
        var t = new AgentSessions();
        Assert.Equal(AgentChangeKind.Started, t.Apply(Claude("working"), T0).Kind);
        var s = Assert.Single(t.All);
        Assert.Equal("api", t.NameOf(s));
        Assert.Equal("api · working 3:12", t.Line(s, T0.AddSeconds(192)));
        t.AddPlayed(40, T0.AddSeconds(100));
        var done = t.Apply(Claude("done"), T0.AddMinutes(5));
        Assert.Equal(AgentChangeKind.Finished, done.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), done.Turn!.Took);
        Assert.Equal(40, done.Turn.Played);
        Assert.Equal("api · done after 5:00", t.Line(s, T0.AddMinutes(6)));
    }

    [Fact]
    public void SeveralSessionsRunSideBySideEachWithItsOwnTimer()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working", "a", "C:/code/api"), T0);
        t.Apply(Claude("working", "b", "/home/me/web"), T0.AddMinutes(1));
        t.Apply(new AgentSignal("working", "codex", "c", "/home/me/docs"), T0.AddMinutes(2));
        Assert.Equal(3, t.WorkingCount(T0.AddMinutes(3)));
        t.Apply(Claude("attention", "b", "/home/me/web"), T0.AddMinutes(4));
        var lines = t.All.Select(s => t.Line(s, T0.AddMinutes(5))).ToList();
        Assert.Equal(new[] { "api · working 5:00", "web · needs you", "docs · Codex · working 3:00" }, lines);
        Assert.Equal(DevLook.Attention, t.Urgent(T0.AddMinutes(5))); // the pill's one dot shows the most urgent
    }

    [Fact]
    public void TwoSessionsInOneFolderAreNumbered()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working", "a"), T0);
        t.Apply(Claude("working", "b"), T0);
        Assert.Equal(new[] { "api", "api 2" }, t.All.Select(t.NameOf).ToArray());
    }

    [Fact]
    public void TheOldHookConfigWithNothingButTheStateIsOneSession()
    {
        var t = new AgentSessions();
        t.Apply(new AgentSignal("working", "claude"), T0);
        t.Apply(new AgentSignal("done", "claude"), T0.AddMinutes(1));
        t.Apply(new AgentSignal("working", "claude"), T0.AddMinutes(2));
        var s = Assert.Single(t.All);
        Assert.Equal("Claude", t.NameOf(s));
        Assert.Equal(AgentState.Working, s.State);
    }

    [Fact]
    public void SessionEndRemovesItAndAQuietOneGoesGreyThenIsForgotten()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working", "a"), T0);
        t.Apply(Claude("working", "b", "C:/code/web"), T0);
        Assert.Equal(AgentChangeKind.Removed, t.Apply(Claude("end", "a"), T0.AddMinutes(1)).Kind);
        var web = Assert.Single(t.All);
        Assert.Equal(DevLook.Working, web.Look(T0.AddMinutes(59)));
        Assert.Equal(DevLook.Stale, web.Look(T0 + AgentSessions.StaleAfter));
        Assert.StartsWith("web · quiet since", t.Line(web, T0 + AgentSessions.StaleAfter));
        Assert.Equal(0, t.WorkingCount(T0 + AgentSessions.StaleAfter)); // a quiet one no longer counts as working
        Assert.False(t.Prune(T0.AddHours(11)));
        Assert.True(t.Prune(T0 + AgentSessions.ForgetAfter));
        Assert.Empty(t.All);
    }

    [Fact]
    public void TheIdleReminderAfterATurnIsNoNews()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working"), T0);
        t.Apply(Claude("done"), T0.AddMinutes(1));
        Assert.Equal(AgentChangeKind.None, t.Apply(Claude("attention", note: "idle_prompt"), T0.AddMinutes(2)).Kind);
        Assert.Equal(AgentState.Done, t.All[0].State);
    }

    [Fact]
    public void StepsShowUnderTheLineAndTheTurnListsFilesAndCommands()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working"), T0);
        t.Apply(Claude("step", kind: "edit", target: "C:/code/api/src/Program.cs"), T0.AddSeconds(5));
        Assert.Equal("Editing Program.cs", AgentSessions.StepLine(t.All[0], T0.AddSeconds(6)));
        t.Apply(Claude("did", kind: "edit", target: "C:/code/api/src/Program.cs"), T0.AddSeconds(7));
        t.Apply(Claude("did", kind: "edit", target: "c:/code/api/src/program.cs"), T0.AddSeconds(8)); // the same file
        t.Apply(Claude("did", kind: "run", target: "dotnet test"), T0.AddSeconds(9));
        var turn = t.Apply(Claude("done"), T0.AddMinutes(1)).Turn!;
        Assert.Single(turn.Files);
        Assert.Equal(new[] { "dotnet test" }, turn.Commands);
        Assert.Equal("", AgentSessions.StepLine(t.All[0], T0.AddMinutes(1))); // done: no step
    }

    [Fact]
    public void WithoutTheStepSettingStepsAreIgnored()
    {
        var t = new AgentSessions();
        Assert.Equal(AgentChangeKind.None, t.Apply(Claude("step", kind: "run", target: "ls"), T0, steps: false).Kind);
        Assert.Empty(t.All);
    }

    [Fact]
    public void AStepAfterApprovalResumesAndALateStepStartsNothing()
    {
        var t = new AgentSessions();
        t.Apply(Claude("working"), T0);
        t.Apply(Claude("attention"), T0.AddMinutes(1));
        // a PostToolUse arriving a moment after the turn asked for you belongs to what came before
        Assert.Equal(AgentChangeKind.Step, t.Apply(Claude("did", kind: "read", target: "a.cs"), T0.AddMinutes(1).AddSeconds(1)).Kind);
        Assert.Equal(AgentState.Attention, t.All[0].State);
        Assert.Equal(AgentChangeKind.Resumed, t.Apply(Claude("step", kind: "run", target: "npm test"), T0.AddMinutes(2)).Kind);
        Assert.Equal(AgentState.Working, t.All[0].State);
    }

    [Fact]
    public void AStepFromASessionNeverSeenStartsItsTurn()
    {
        var t = new AgentSessions();
        Assert.Equal(AgentChangeKind.Started, t.Apply(Claude("step", kind: "read", target: "x.md"), T0).Kind);
        Assert.Equal(AgentState.Working, t.All[0].State);
    }

    [Fact]
    public void TheTableKeepsAtMostTwelveDroppingTheQuietestFirst()
    {
        var t = new AgentSessions();
        for (int i = 0; i < AgentSessions.MaxSessions; i++) t.Apply(Claude("working", "s" + i, "C:/p" + i), T0.AddSeconds(i));
        t.Apply(Claude("done", "s3", "C:/p3"), T0.AddMinutes(1));
        t.Apply(Claude("working", "new", "C:/new"), T0.AddMinutes(2));
        Assert.Equal(AgentSessions.MaxSessions, t.Count);
        Assert.DoesNotContain(t.All, s => s.SessionId == "s3"); // the one not working went first
    }
}

public class AgentStepTests
{
    [Theory]
    [InlineData("Edit", "file_path", "C:\\code\\api\\src\\Program.cs", "Editing Program.cs")]
    [InlineData("Write", "file_path", "/home/me/web/README.md", "Writing README.md")]
    [InlineData("Read", "file_path", "notes.txt", "Reading notes.txt")]
    [InlineData("Bash", "command", "npm test", "Running npm test")]
    [InlineData("Grep", "pattern", "TODO", "Searching TODO")]
    [InlineData("WebFetch", "url", "https://learn.microsoft.com/dotnet/", "Reading learn.microsoft.com")]
    [InlineData("run_shell_command", "command", "make -j8", "Running make -j8")]
    [InlineData("view", "path", "src/app.py", "Reading app.py")]
    public void ToolCallsBecomeShortLabels(string tool, string field, string value, string label)
    {
        var (kind, target) = AgentStep.FromTool(tool, new Dictionary<string, string> { [field] = value });
        Assert.Equal(label, AgentStep.Label(kind, target));
    }

    [Fact]
    public void LongPathsKeepTheirExtensionAndLongCommandsAreCut()
    {
        string label = AgentStep.Label("edit", "/src/AVeryVeryLongComponentNameThatGoesOnAndOn.tsx");
        Assert.True(label.Length <= AgentStep.MaxLabel);
        Assert.EndsWith("….tsx", label);
        string run = AgentStep.Label("run", "dotnet test tests/DeskArcade.Tests --filter FullyQualifiedName~Something --logger console");
        Assert.True(run.Length <= AgentStep.MaxLabel);
        Assert.EndsWith("…", run);
        Assert.Equal("git add . …", AgentStep.Command("git add .\ngit commit -m x"));
    }

    [Fact]
    public void McpAndPatchAndPlanToolsAreNamed()
    {
        Assert.Equal(("tool", "github create_issue"), AgentStep.FromTool("mcp__github__create_issue", null));
        Assert.Equal("src/app.py", AgentStep.PatchedFile("*** Begin Patch\n*** Update File: src/app.py\n@@\n-x\n+y\n*** End Patch"));
        Assert.Equal("Planning", AgentStep.Label("plan", ""));
        Assert.Equal("Running a command", AgentStep.Label("run", ""));
        Assert.Equal("", AgentStep.Label("nonsense", "x"));
    }
}

public class AgentSignalTests
{
    [Fact]
    public void ALineSurvivesThePipeCleanedOfSeparatorsAndBreaks()
    {
        var s = new AgentSignal("step", "claude", "abc", "C:/a|b", "run", "echo 1\necho 2", "");
        var back = AgentSignal.Parse(s.ToMessage())!;
        Assert.Equal("C:/a b", back.Folder);
        Assert.Equal("echo 1 echo 2", back.Target);
        Assert.Equal("run", back.Kind);
        Assert.Null(AgentSignal.Parse("agent:bogus|claude"));
        Assert.Null(AgentSignal.Parse("status:running|x"));
    }

    [Fact]
    public void ClaudeCodesHookPayloadNamesTheSessionFolderAndStep()
    {
        string json = """{"session_id":"s-42","transcript_path":"x","cwd":"C:\\code\\api","hook_event_name":"PreToolUse","tool_name":"Edit","tool_input":{"file_path":"C:\\code\\api\\a.cs","old_string":"x","new_string":"y"}}""";
        var s = AgentSignal.FromHook("step", null, json)!;
        Assert.Equal(("claude", "s-42", "C:\\code\\api", "edit", "C:\\code\\api\\a.cs"), (s.Agent, s.Session, s.Folder, s.Kind, s.Target));
    }

    [Fact]
    public void AutoTakesTheStateFromTheEventAndNotificationsThatNeedNobodyAreDropped()
    {
        Assert.Equal("working", AgentSignal.FromHook("auto", null, """{"hook_event_name":"UserPromptSubmit","session_id":"a"}""")!.State);
        Assert.Equal("done", AgentSignal.FromHook("auto", "codex", """{"type":"agent-turn-complete","thread-id":"t1"}""")!.State);
        Assert.Null(AgentSignal.FromHook("auto", null, """{"hook_event_name":"SubagentStart"}"""));
        Assert.Null(AgentSignal.FromHook("attention", null, """{"hook_event_name":"Notification","notification_type":"auth_success"}"""));
        Assert.NotNull(AgentSignal.FromHook("attention", null, """{"hook_event_name":"Notification","notification_type":"permission_prompt"}"""));
    }

    [Fact]
    public void CursorsPayloadGivesItselfAwayAndNamesItsWorkspace()
    {
        var s = AgentSignal.FromHook("did", "claude", """{"conversation_id":"c9","cursor_version":"1.7","workspace_roots":["/home/me/web"],"hook_event_name":"afterFileEdit","file_path":"/home/me/web/app.ts"}""")!;
        Assert.Equal(("cursor", "c9", "/home/me/web", "edit", "/home/me/web/app.ts"), (s.Agent, s.Session, s.Folder, s.Kind, s.Target));
    }

    [Fact]
    public void APayloadCutOffPartWayStillGivesWhatCameBefore()
    {
        string json = """{"session_id":"s1","cwd":"/p","hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":"/p/big.txt","content":"aaaaaaaaaa""";
        var s = AgentSignal.FromHook("step", null, json)!;
        Assert.Equal(("s1", "/p", "write", "/p/big.txt"), (s.Session, s.Folder, s.Kind, s.Target));
    }

    [Fact]
    public void AnAgentOtherThanClaudeWithoutAFolderIsNamedAfterTheFolderItRanIn()
    {
        Assert.Equal("/home/me/proj", AgentSignal.FromHook("done", "aider", null, "/home/me/proj")!.Folder);
        Assert.Equal("", AgentSignal.FromHook("done", null, null, "/home/me/proj")!.Folder); // Claude without a payload: the old single session
    }

    [Fact]
    public void TheCommandLineReadsTheLastArgumentOrStdinForThePayload()
    {
        string? fromArg = DevCli.Message("done", new[] { "--signal", "done", "--agent", "codex", """{"type":"agent-turn-complete","thread-id":"t7"}""" }, _ => throw new InvalidOperationException("stdin"));
        Assert.StartsWith("agent:done|codex|t7|", fromArg);
        string? fromStdin = DevCli.Message("working", new[] { "--signal", "working" }, _ => """{"session_id":"s5","cwd":"/w"}""");
        Assert.StartsWith("agent:working|claude|s5|/w|", fromStdin);
    }
}

public class AgentConfigTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex-hooks")]
    [InlineData("cursor")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("windsurf")]
    public void JsonConfigsParseAndRunTheProgram(string id)
    {
        foreach (bool windows in new[] { true, false })
            foreach (bool steps in new[] { false, true })
            {
                string exe = windows ? "C:\\Program Files\\Desk Arcade\\DeskArcade.exe" : "/usr/bin/deskarcade";
                var config = AgentConfigs.Build(id, exe, "", steps, windows);
                using var doc = JsonDocument.Parse(config.Text); // valid JSON
                Assert.True(doc.RootElement.TryGetProperty("hooks", out _));
                Assert.Contains("--signal", config.Text);
                Assert.Equal(steps, config.Text.Contains("--signal step") || config.Text.Contains("--signal did") || config.Text.Contains("\"did\""));
                // Git Bash eats backslashes, so shell commands use forward slashes; PowerShell (Gemini) and no shell (Copilot) keep the system's
                if (windows && id is not ("copilot" or "gemini")) Assert.Contains("C:/Program Files/Desk Arcade/DeskArcade.exe", config.Text);
                if (windows && id == "gemini") Assert.Contains("& 'C:\\\\Program Files", config.Text); // JSON escapes the backslash
            }
    }

    [Fact]
    public void ClaudeCodesConfigHasTheFiveHooksAndRunsInTheBackground()
    {
        using var doc = JsonDocument.Parse(AgentConfigs.Build("claude", "/usr/bin/deskarcade", "", false, false).Text);
        var hooks = doc.RootElement.GetProperty("hooks");
        foreach (var name in new[] { "UserPromptSubmit", "Stop", "PermissionRequest", "Notification", "SessionEnd" }) Assert.True(hooks.TryGetProperty(name, out _), name);
        Assert.False(hooks.TryGetProperty("PreToolUse", out _));
        Assert.True(hooks.GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("async").GetBoolean());
    }

    [Fact]
    public void CodexNotifyIsATomlListAndAiderAYamlLine()
    {
        string codex = AgentConfigs.Build("codex", "C:\\Apps\\DeskArcade.exe", "", false, true).Text;
        Assert.Contains("notify = [\"C:\\\\Apps\\\\DeskArcade.exe\", \"--signal\", \"done\", \"--agent\", \"codex\"]", codex);
        string aider = AgentConfigs.Build("aider", "/usr/bin/deskarcade", "", false, false).Text;
        Assert.Contains("notifications-command: '\"/usr/bin/deskarcade\" --signal done --agent aider'", aider);
    }

    [Fact]
    public void AProfileIsPassedOn()
    {
        Assert.Contains("--profile test --signal working", AgentConfigs.Build("claude", "/usr/bin/deskarcade", "test", false, false).Text);
    }
}

public class StatusLaneTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheCommandLineMakesTheLineTheExtensionAlsoWrites()
    {
        var (m, error) = StatusMessage.FromArgs(new[] { "--status", "deploy", "running", "--note", "staging", "--source", "vscode" }, 0);
        Assert.Null(error);
        Assert.Equal("status:running|deploy|staging|vscode", m!.ToLine());
        Assert.Equal(m, StatusMessage.Parse(m.ToLine()));
        Assert.Equal("deploy", StatusMessage.FromArgs(new[] { "--status", "running", "deploy" }, 0).Message!.Name); // forgiven
        Assert.NotNull(StatusMessage.FromArgs(new[] { "--status", "deploy" }, 0).Error);
        Assert.NotNull(StatusMessage.FromArgs(new[] { "--status", "deploy", "bogus" }, 0).Error);
        Assert.Null(StatusMessage.Parse("status:running||x"));
        Assert.Equal("a b c", StatusMessage.Parse("status:passed|n|note|a|b|c")!.Source); // a "|" left in the source
    }

    [Fact]
    public void ALaneRunsThenPassesOrFailsWithHowLongItTook()
    {
        var lanes = new StatusLanes();
        Assert.Equal(LaneChangeKind.Started, lanes.Apply(new StatusMessage("running", "tests"), T0).Kind);
        lanes.AddPlayed(30, T0.AddSeconds(10));
        Assert.Equal("tests · running 1:05", StatusLanes.Line(lanes.All[0], T0.AddSeconds(65)));
        var done = lanes.Apply(new StatusMessage("failed", "tests", "3 failed"), T0.AddSeconds(90));
        Assert.Equal(LaneChangeKind.Finished, done.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), done.Took);
        Assert.Equal(30, lanes.All[0].Played);
        Assert.Equal("tests · failed 1:30 · 3 failed", StatusLanes.Line(lanes.All[0], T0.AddSeconds(91)));
        Assert.Equal(LaneChangeKind.Cleared, lanes.Apply(new StatusMessage("clear", "TESTS"), T0.AddSeconds(92)).Kind);
        Assert.Empty(lanes.All);
    }

    [Fact]
    public void ALaneWithNoWordForHalfAnHourGoesGrey()
    {
        var lanes = new StatusLanes();
        lanes.Apply(new StatusMessage("running", "watch"), T0);
        Assert.Equal(DevLook.Running, lanes.All[0].Look(T0.AddMinutes(29)));
        Assert.Equal(DevLook.Stale, lanes.All[0].Look(T0.AddMinutes(30)));
        Assert.Equal(0, lanes.RunningCount(T0.AddMinutes(30)));
    }

    [Fact]
    public void MoreThanThreeLanesFoldIntoOneChipWithACount()
    {
        var lanes = new StatusLanes();
        lanes.Apply(new StatusMessage("running", "build"), T0);
        lanes.Apply(new StatusMessage("running", "lint"), T0);
        lanes.Apply(new StatusMessage("running", "deploy"), T0);
        Assert.False(lanes.Folded);
        lanes.Apply(new StatusMessage("running", "docs"), T0);
        lanes.Apply(new StatusMessage("running", "e2e"), T0);
        lanes.Apply(new StatusMessage("passed", "build"), T0.AddMinutes(1));
        lanes.Apply(new StatusMessage("failed", "lint"), T0.AddMinutes(1));
        Assert.True(lanes.Folded);
        Assert.Equal("5 lanes · 3 running · 1 failed · 1 passed", lanes.FoldedLine(T0.AddMinutes(2)));
        Assert.Equal(DevLook.Failed, lanes.Urgent(T0.AddMinutes(2)));
        Assert.Equal(5, lanes.AllLines(T0.AddMinutes(2)).Split('\n').Length);
    }
}

public class RepoWatchTests
{
    static string Data(string name) => File.ReadAllText(Path.Combine(TranslationCoverageTests.RepoRoot(), "tests", "DeskArcade.Tests", "Data", name));

    [Theory]
    [InlineData("https://github.com/BokhodirUrinboev/DeskArcade.git", RepoHost.GitHub, "github.com", "BokhodirUrinboev/DeskArcade")]
    [InlineData("git@github.com:BokhodirUrinboev/DeskArcade.git", RepoHost.GitHub, "github.com", "BokhodirUrinboev/DeskArcade")]
    [InlineData("ssh://git@gitlab.example.com:2222/team/sub/app.git", RepoHost.GitLab, "gitlab.example.com", "team/sub/app")]
    [InlineData("https://gitlab.com/group/project", RepoHost.GitLab, "gitlab.com", "group/project")]
    public void OriginsNameTheirHostAndPath(string url, RepoHost host, string server, string path) =>
        Assert.Equal(new RemoteRepo(host, server, path), RemoteRepo.Parse(url));

    [Fact]
    public void OtherHostsAndNonsenseAreNotFollowed()
    {
        Assert.Null(RemoteRepo.Parse("https://bitbucket.org/a/b.git"));
        Assert.Null(RemoteRepo.Parse("not a url"));
        Assert.Null(RemoteRepo.Parse(""));
    }

    [Fact]
    public void RecordedRunsOfThisRepositoryReadAsTheLatestCommitsCi()
    {
        var run = GitHubJson.LatestRun(Data("gh-runs.json"))!;
        Assert.Equal(CiState.Passed, run.State);
        Assert.Equal(40, run.Key.Length); // the commit
        Assert.NotNull(run.StartUtc);
        Assert.True(run.EndUtc > run.StartUtc);
        Assert.Contains("CI", run.Names);
    }

    [Fact]
    public void ACommitWithOneRunStillGoingIsRunningAndOneFailureFailsIt()
    {
        string running = """[{"headSha":"a","status":"in_progress","conclusion":"","workflowName":"CI","createdAt":"2026-10-01T09:00:00Z","updatedAt":"2026-10-01T09:01:00Z"},{"headSha":"a","status":"completed","conclusion":"success","workflowName":"Lint","createdAt":"2026-10-01T09:00:00Z","updatedAt":"2026-10-01T09:02:00Z"},{"headSha":"old","status":"completed","conclusion":"failure"}]""";
        var r = GitHubJson.LatestRun(running)!;
        Assert.Equal(CiState.Running, r.State);
        Assert.Null(r.EndUtc);
        Assert.Equal("CI, Lint", r.Names);
        string failed = running.Replace("\"in_progress\",\"conclusion\":\"\"", "\"completed\",\"conclusion\":\"failure\"");
        Assert.Equal(CiState.Failed, GitHubJson.LatestRun(failed)!.State);
        Assert.Equal(CiState.None, GitHubJson.LatestRun("[]")!.State);
        Assert.Null(GitHubJson.LatestRun("not json"));
    }

    [Fact]
    public void RecordedPullRequestsAreReadAndNothingOnThemIsNews()
    {
        var mine = GitHubJson.Mine(Data("gh-mine.json"))!;
        Assert.NotEmpty(mine);
        Assert.All(mine, p => Assert.True(p.Number > 0 && p.Title.Length > 0));
        Assert.Contains(mine, p => p.Merged);
        Assert.Empty(PrDiff.Between(mine, mine));
        Assert.Empty(GitHubJson.Reviews(Data("gh-reviews.json"))!);
    }

    [Fact]
    public void OnlyOthersCommentsAndReviewsCount()
    {
        string json = """[{"number":5,"title":"x","state":"OPEN","reviewDecision":"","url":"u","author":{"login":"me"},"comments":[{"author":{"login":"me"}},{"author":{"login":"ann"}}],"reviews":[{"author":{"login":"bob"},"state":"APPROVED","body":""}]}]""";
        var p = Assert.Single(GitHubJson.Mine(json)!);
        Assert.Equal(1, p.Comments);
        Assert.Equal(1, p.Approvals);
        Assert.True(p.Approved);
    }

    [Fact]
    public void BetweenTwoPollsAMergeAnApprovalOrAComment()
    {
        var before = new List<PullRequest> { new(1, "a", "open", false, 0, 0, ""), new(2, "b", "open", false, 0, 0, ""), new(3, "c", "open", false, 0, 1, "") };
        var after = new List<PullRequest> { new(1, "a", "merged", true, 1, 0, ""), new(2, "b", "open", true, 1, 0, ""), new(3, "c", "open", false, 0, 2, ""), new(4, "new", "open", false, 0, 5, "") };
        var events = PrDiff.Between(before, after);
        Assert.Equal(new[] { PrEventKind.Merged, PrEventKind.Approved, PrEventKind.Commented }, events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void GitLabPipelinesAndMergeRequestsAreRead()
    {
        var pipe = GitLabJson.LatestPipeline("""[{"id":991,"status":"running","created_at":"2026-10-01T09:00:00Z","web_url":"https://gitlab.com/g/p/-/pipelines/991"}]""")!;
        Assert.Equal((CiState.Running, "991"), (pipe.State, pipe.Key));
        Assert.Equal(CiState.Failed, GitLabJson.LatestPipeline("""[{"id":1,"status":"failed"}]""")!.State);
        Assert.Equal(CiState.Passed, GitLabJson.LatestPipeline("""[{"id":1,"status":"success"}]""")!.State);
        var mrs = GitLabJson.Mine("""[{"iid":7,"title":"t","state":"opened","user_notes_count":3,"web_url":"w"}]""", new Dictionary<int, int> { [7] = 2 })!;
        Assert.Equal((7, "open", 3, 2), (mrs[0].Number, mrs[0].State, mrs[0].Comments, mrs[0].Approvals));
        Assert.Equal(2, GitLabJson.Approvals("""{"approved_by":[{"user":{}},{"user":{}}]}"""));
        Assert.Equal("ann", GitLabJson.User("""{"username":"ann"}"""));
    }

    [Fact]
    public async Task TheFirstPollOnlyTakesNoteAndTheNextReportsWhatChanged()
    {
        string dir = Directory.CreateTempSubdirectory("da-repo-").FullName;
        var old = Cli.Runner;
        try
        {
            string runs = """[{"headSha":"a","status":"in_progress","conclusion":"","workflowName":"CI","createdAt":"2026-10-01T09:00:00Z","updatedAt":"2026-10-01T09:00:00Z"}]""";
            string mine = """[{"number":3,"title":"Fix it","state":"OPEN","reviewDecision":"","url":"u","author":{"login":"me"},"comments":[],"reviews":[]}]""";
            Cli.Runner = (tool, args, folder, timeout, ct) => Task.FromResult(tool switch
            {
                "git" when args[0] == "rev-parse" => new CliResult(0, "main\n", ""),
                "git" => new CliResult(0, "git@github.com:me/app.git\n", ""),
                _ when args.Contains("run") => new CliResult(0, runs, ""),
                _ when args.Contains("--author") => new CliResult(0, mine, ""),
                _ => new CliResult(0, "[]", ""),
            });
            if (!Cli.Has("gh")) return; // the watcher checks gh is installed before it asks it anything
            var watch = new RepoWatch(dir);
            var first = await watch.PollAsync();
            Assert.True(first.First);
            Assert.Equal(CiState.Running, first.Snapshot.Ci!.State);
            Assert.Null(first.CiFinished);
            runs = runs.Replace("\"in_progress\",\"conclusion\":\"\"", "\"completed\",\"conclusion\":\"success\"");
            mine = mine.Replace("\"reviewDecision\":\"\"", "\"reviewDecision\":\"APPROVED\"");
            var second = await watch.PollAsync();
            Assert.False(second.First);
            Assert.Equal(CiState.Passed, second.CiFinished!.State);
            Assert.Equal(PrEventKind.Approved, Assert.Single(second.Events).Kind);
            var third = await watch.PollAsync();
            Assert.Null(third.CiFinished); // a finished run is news once
            Assert.Empty(third.Events);
        }
        finally
        {
            Cli.Runner = old;
            Directory.Delete(dir, true);
        }
    }
}

public class IpcCaseTests
{
    [Theory]
    [InlineData("three:Review the PR|Write Notes", "three:Review the PR|Write Notes")]
    [InlineData("status:running|Deploy|Staging EU|vscode", "status:running|Deploy|Staging EU|vscode")]
    [InlineData("agent:working|claude|S1|C:/Code/Api||||", "agent:working|claude|S1|C:/Code/Api||||")]
    [InlineData("repo-add:C:/Code/Api", "repo-add:C:/Code/Api")]
    [InlineData("  SHOW  ", "show")]
    public void TextAfterAKnownPrefixKeepsItsSpelling(string line, string expected) => Assert.Equal(expected, Ipc.Normalize(line));
}
