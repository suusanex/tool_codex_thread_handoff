#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:package OpenAI@2.14.0
#:include src/HandoffTool.cs

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using CodexThreadHandoff;

var failures = new List<string>();
await Test(failures, "bare deeplink", BareDeeplink);
await Test(failures, "markdown deeplink", MarkdownDeeplink);
await Test(failures, "malformed deeplink", MalformedDeeplink);
await Test(failures, "api key resolution", ApiKeyResolution);
await Test(failures, "backend and facade path parsing", BackendAndFacadePathParsing);
await Test(failures, "facade request and response", FacadeRequestAndResponse);
await Test(failures, "facade failures", FacadeFailures);
await Test(failures, "facade cancellation and timeout", FacadeCancellationAndTimeout);
await Test(failures, "facade CLI does not require API key", FacadeCliDoesNotRequireApiKey);
await Test(failures, "final agent message and excluded tool output", FilterMessages);
await Test(failures, "long transcript bound", LongTranscript);
await Test(failures, "dump recovers summary omissions", DumpRecoversSummaryOmissions);
await Test(failures, "reread names tool and codex environment", RereadNamesToolAndCodexEnvironment);
await Test(failures, "powershell dump arguments stay literal", PowerShellDumpArgumentsStayLiteral);
await Test(failures, "codex launch resolution", CodexLaunchResolution);
await Test(failures, "schema acceptance and rejection", Schema);
await Test(failures, "git snapshot enters prompt and artifact", GitSnapshot);
await Test(failures, "missing repository still creates handoff", MissingRepository);
await Test(failures, "dump from deeplink and handoff json", DumpBothInputs);
await Test(failures, "app server pagination and missing thread", AppServerProtocol);
await Test(failures, "app server json lines have no bom", AppServerJsonLinesHaveNoBom);
if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    return 1;
}

Console.WriteLine("All tests passed.");
return 0;

static async Task Test(List<string> failures, string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add(name);
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

static Task BareDeeplink()
{
    const string id = "01a1575c-cafe-7eb3-b6d5-73c834e034ac";
    AssertEqual(id, DeeplinkParser.ParseThreadId($"codex://threads/{id}"));
    AssertEqual(id, DeeplinkParser.ParseThreadId($"  codex://threads/{id}  "));
    return Task.CompletedTask;
}

static Task MarkdownDeeplink()
{
    const string id = "01a1575c-cafe-7eb3-b6d5-73c834e034ac";
    var markdown = $"[任意のタイトル](codex://threads/{id})";
    AssertEqual(id, DeeplinkParser.ParseThreadId(markdown));
    AssertEqual(id, DeeplinkParser.ParseThreadId($"{markdown} and again [x](codex://threads/{id})"));
    return Task.CompletedTask;
}

static Task MalformedDeeplink()
{
    AssertThrows("empty", () => DeeplinkParser.ParseThreadId("  "));
    AssertThrows("scheme", () => DeeplinkParser.ParseThreadId("codex://thread/abc"));
    AssertThrows("slash", () => DeeplinkParser.ParseThreadId("codex://threads/abc/def"));
    AssertThrows("blank id", () => DeeplinkParser.ParseThreadId("codex://threads/"));
    AssertThrows("two ids", () => DeeplinkParser.ParseThreadId("codex://threads/aaa codex://threads/bbb"));
    return Task.CompletedTask;
}

static Task ApiKeyResolution()
{
    AssertThrows("missing", () => ApiKeyResolver.Resolve(_ => null));
    AssertThrows("generic only", () => ApiKeyResolver.Resolve(name => name == "OPENAI_API_KEY" ? "generic" : null));
    AssertEqual("dedicated", ApiKeyResolver.Resolve(name => name == ApiKeyResolver.VariableName ? " dedicated " : "generic"));
    return Task.CompletedTask;
}

static Task BackendAndFacadePathParsing()
{
    var openAi = CliParser.Parse(["codex://threads/abc"]);
    AssertEqual(HandoffBackend.OpenAi, openAi.Backend);
    AssertEqual(false, openAi.BackendSpecified);

    var facade = CliParser.Parse(
        ["codex://threads/abc", "--backend", "facade", "--model", "copilot-model", "--facade-cli", "override"]);
    AssertEqual(HandoffBackend.Facade, facade.Backend);
    AssertEqual("copilot-model", facade.Model!);
    AssertEqual("override", facade.FacadeCliPath!);

    AssertThrows("facade model", () => CliParser.Parse(["codex://threads/abc", "--backend", "facade"]));
    AssertThrows("backend", () => CliParser.Parse(["codex://threads/abc", "--backend", "other"]));
    AssertThrows("unused facade path", () => CliParser.Parse(["codex://threads/abc", "--facade-cli", "ignored"]));
    AssertThrows("dump backend", () => CliParser.Parse(["dump", "codex://threads/abc", "--backend", "facade"]));

    var directory = Path.Combine(Path.GetTempPath(), $"facade-path-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var configured = Path.Combine(directory, "configured.exe");
        var overridePath = Path.Combine(directory, "override.exe");
        File.WriteAllText(configured, "");
        File.WriteAllText(overridePath, "");
        AssertEqual(overridePath, FacadeCliPathResolver.Resolve(overridePath, _ => configured));
        AssertEqual(configured, FacadeCliPathResolver.Resolve(null, name =>
            name == FacadeCliPathResolver.VariableName ? configured : null));
        AssertThrows("missing", () => FacadeCliPathResolver.Resolve(null, _ => null));
        AssertThrows("relative", () => FacadeCliPathResolver.Resolve("relative.exe", _ => null));
        AssertThrows(
            "not found",
            () => FacadeCliPathResolver.Resolve(Path.Combine(directory, "missing.exe"), _ => null));
    }
    finally
    {
        DeleteDirectory(directory);
    }

    return Task.CompletedTask;
}

static async Task FacadeRequestAndResponse()
{
    string? capturedPath = null;
    string? capturedRequest = null;
    var bodyJson = """
        {"purpose":"目的","decisions":[],"currentState":["完了"],"remainingWork":[],"rejectedApproaches":[],"openQuestions":[],"importantFiles":["src/HandoffTool.cs"]}
        """;
    var response = new JsonObject
    {
        ["status"] = "completed",
        ["result"] = new JsonObject
        {
            ["agent"] = "github-copilot",
            ["outputKind"] = "final_response",
            ["outputText"] = bodyJson,
        },
    }.ToJsonString();
    var model = new FacadeHandoffModel("C:\\Tools\\CodexAgentFacadeCli.exe", (path, request, cancellationToken) =>
    {
        capturedPath = path;
        capturedRequest = request;
        return Task.FromResult(new FacadeProcessResult(0, response, ""));
    });
    var directory = Path.GetFullPath(Directory.GetCurrentDirectory());

    var body = await model.CreateAsync("copilot-model", "FILTERED_INPUT", directory, CancellationToken.None);

    AssertEqual("目的", body.Purpose);
    AssertEqual("src/HandoffTool.cs", body.ImportantFiles[0]);
    AssertEqual("C:\\Tools\\CodexAgentFacadeCli.exe", capturedPath!);
    using var document = JsonDocument.Parse(capturedRequest!);
    var root = document.RootElement;
    AssertEqual("github-copilot", root.GetProperty("agent").GetString()!);
    AssertEqual("copilot-model", root.GetProperty("model").GetString()!);
    AssertEqual(directory, root.GetProperty("working_directory").GetString()!);
    AssertEqual(false, root.GetProperty("auto_approve").GetBoolean());
    AssertEqual(FacadeHandoffModel.DefaultTimeoutSeconds, root.GetProperty("timeout_seconds").GetInt32());
    AssertContains(root.GetProperty("prompt").GetString()!, HandoffBodyParser.SchemaJson);
    AssertContains(root.GetProperty("prompt").GetString()!, "FILTERED_INPUT");
    AssertContains(root.GetProperty("prompt").GetString()!, "Do not call tools or edit files.");
    AssertEqual(false, root.TryGetProperty("skills", out _));
    AssertEqual(false, root.TryGetProperty("run_mode", out _));
    AssertEqual(false, root.TryGetProperty("session_id", out _));
}

static async Task FacadeFailures()
{
    var directory = Path.GetFullPath(Directory.GetCurrentDirectory());
    var validBody = """
        {"purpose":"目的","decisions":[],"currentState":[],"remainingWork":[],"rejectedApproaches":[],"openQuestions":[],"importantFiles":[]}
        """;
    var invalidBodies = new[]
    {
        new FacadeProcessResult(1, """{"status":"failed","failure":{"summary":"agent failed"}}""", "failed stderr"),
        new FacadeProcessResult(4, """{"status":"timed_out","error":"timeout"}""", ""),
        new FacadeProcessResult(3, """{"status":"cancelled"}""", ""),
        new FacadeProcessResult(0, "not-json", "bad stdout"),
        FacadeResult("assistant_transcript", validBody),
        FacadeResult("final_response", ""),
        FacadeResult("final_response", """{"purpose":"missing"}"""),
    };

    foreach (var processResult in invalidBodies)
    {
        var model = new FacadeHandoffModel(
            "unused",
            (_, _, _) => Task.FromResult(processResult));
        await AssertThrowsAsync("facade failure", () =>
            model.CreateAsync("copilot-model", "prompt", directory, CancellationToken.None));
    }
}

static async Task FacadeCancellationAndTimeout()
{
    var directory = Path.GetFullPath(Directory.GetCurrentDirectory());
    static async Task<FacadeProcessResult> WaitForever(
        string path,
        string request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        var model = new FacadeHandoffModel("unused", WaitForever);
        var message = await CaptureHandoffExceptionAsync(() =>
            model.CreateAsync("copilot-model", "prompt", directory, cancellation.Token));
        AssertContains(message, "cancelled");
    }

    var timeoutModel = new FacadeHandoffModel("unused", WaitForever, timeoutSeconds: 1);
    var timeoutMessage = await CaptureHandoffExceptionAsync(() =>
        timeoutModel.CreateAsync("copilot-model", "prompt", directory, CancellationToken.None));
    AssertContains(timeoutMessage, "timed out after 1 seconds");
}

static FacadeProcessResult FacadeResult(string outputKind, string outputText) =>
    new(
        0,
        new JsonObject
        {
            ["status"] = "completed",
            ["result"] = new JsonObject
            {
                ["agent"] = "github-copilot",
                ["outputKind"] = outputKind,
                ["outputText"] = outputText,
            },
        }.ToJsonString(),
        "");

static async Task FacadeCliDoesNotRequireApiKey()
{
    var directory = Path.Combine(Path.GetTempPath(), $"facade-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var cliPath = Path.Combine(directory, "CodexAgentFacadeCli.exe");
    var output = Path.Combine(directory, "handoff.json");
    File.WriteAllText(cliPath, "");
    var bodyJson = """
        {"purpose":"目的","decisions":[],"currentState":[],"remainingWork":[],"rejectedApproaches":[],"openQuestions":[],"importantFiles":[]}
        """;
    var threads = new FakeThreads(new ThreadSnapshot(
        "abc",
        null,
        directory,
        [new StoredItem("t1", User("continue", null))]));
    try
    {
        var apiKeyReads = 0;
        ResolvedBackend? selectedBackend = null;
        string? capturedPath = null;
        string? capturedRequest = null;
        var exit = await Cli.RunAsync(
            [
                "codex://threads/abc",
                "--backend", "facade",
                "--model", "copilot-model",
                "--facade-cli", cliPath,
                "-o", output,
            ],
            name =>
            {
                if (name == ApiKeyResolver.VariableName)
                {
                    apiKeyReads++;
                    return null;
                }

                return null;
            },
            threads,
            backend =>
            {
                selectedBackend = backend;
                return Cli.CreateHandoffModel(
                    backend,
                    (path, request, cancellationToken) =>
                    {
                        capturedPath = path;
                        capturedRequest = request;
                        return Task.FromResult(FacadeResult("final_response", bodyJson));
                    });
            },
            new StringWriter(),
            new StringWriter(),
            CancellationToken.None);
        AssertEqual(0, exit);
        AssertEqual(0, apiKeyReads);
        AssertEqual(HandoffBackend.Facade, selectedBackend!.Backend);
        AssertEqual(null, selectedBackend.ApiKey);
        AssertEqual(Path.GetFullPath(cliPath), selectedBackend.FacadeCliPath);
        AssertEqual(Path.GetFullPath(cliPath), capturedPath);
        AssertEqual(true, File.Exists(output));
        using var requestDocument = JsonDocument.Parse(capturedRequest!);
        AssertEqual("github-copilot", requestDocument.RootElement.GetProperty("agent").GetString()!);
        AssertEqual("copilot-model", requestDocument.RootElement.GetProperty("model").GetString()!);
        AssertEqual(Path.GetFullPath(directory), requestDocument.RootElement.GetProperty("working_directory").GetString());

        var failingOutput = Path.Combine(directory, "failed-handoff.json");
        var failureStderr = new StringWriter();
        var failureExit = await Cli.RunAsync(
            [
                "codex://threads/abc",
                "--backend", "facade",
                "--model", "copilot-model",
                "--facade-cli", cliPath,
                "-o", failingOutput,
            ],
            _ => null,
            threads,
            new FailingModel(),
            new StringWriter(),
            failureStderr,
            CancellationToken.None);
        AssertEqual(1, failureExit);
        AssertEqual(false, File.Exists(failingOutput));
        AssertContains(failureStderr.ToString(), "stub model failure");
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static Task FilterMessages()
{
    var items = new List<StoredItem>
    {
        new("t1", User("see this", "SECRET_IMAGE")),
        new("t1", """{"type":"commandExecution","command":"echo","aggregatedOutput":"SECRET_TOOL_OUTPUT"}"""),
        new("t1", Agent("COMMENTARY_SECRET", "commentary")),
        new("t1", Agent("PARTIAL_SECRET", "partial_answer")),
        new("t1", """{"type":"fileChange","changes":[{"path":"a.cs","diff":"SECRET_DIFF_BODY"}]}"""),
        new("t1", Agent("FINAL_VISIBLE", "final_answer")),
        new("t2", User("only user", null)),
        new("t2", Agent("FIRST_COMMENT", "commentary")),
        new("t2", Agent("LAST_COMMENT", "commentary")),
    };
    var filtered = TranscriptFilter.Filter(items);
    var rendered = string.Join("\n", filtered.Messages.Select(message => message.Text));
    AssertContains(rendered, "see this");
    AssertContains(rendered, "[images omitted: 1]");
    AssertContains(rendered, "FINAL_VISIBLE");
    AssertContains(rendered, "LAST_COMMENT");
    AssertDoesNotContain(rendered, "SECRET_IMAGE");
    AssertDoesNotContain(rendered, "SECRET_TOOL_OUTPUT");
    AssertDoesNotContain(rendered, "SECRET_DIFF_BODY");
    AssertDoesNotContain(rendered, "COMMENTARY_SECRET");
    AssertDoesNotContain(rendered, "PARTIAL_SECRET");
    AssertDoesNotContain(rendered, "FIRST_COMMENT");
    AssertEqual("commandExecution,fileChange", string.Join(",", filtered.Coverage.ExcludedItemTypes));
    AssertEqual(true, filtered.Coverage.IncludedItemTypes.Contains("userMessage"));
    AssertEqual(true, filtered.Coverage.IncludedItemTypes.Contains("agentMessage"));
    return Task.CompletedTask;
}

static Task LongTranscript()
{
    var items = LongItems();
    var filtered = TranscriptFilter.Filter(items);
    var rendered = string.Join("\n", filtered.Messages.Select(message => message.Text));
    AssertDoesNotContain(rendered, "TURN-00-");
    AssertContains(rendered, "TURN-29-");
    AssertDoesNotContain(rendered, "END_SECRET");
    AssertContains(rendered, "[truncated]");
    AssertDoesNotContain(rendered, "SECRET_TOOL_OUTPUT");
    AssertEqual(true, filtered.Coverage.Truncated);
    AssertEqual(31, filtered.Coverage.TurnCount);
    AssertEqual(true, filtered.Coverage.IncludedTurnCount < filtered.Coverage.TurnCount);
    var total = filtered.Messages.Sum(message => message.Text.Length);
    AssertEqual(true, total <= TranscriptFilter.MaxTranscriptChars);

    var recovered = TranscriptFilter.Filter(items, boundForSummary: false);
    var recoveredText = string.Join("\n", recovered.Messages.Select(message => message.Text));
    AssertContains(recoveredText, "TURN-00-");
    AssertContains(recoveredText, "END_SECRET");
    AssertDoesNotContain(recoveredText, "SECRET_TOOL_OUTPUT");
    AssertDoesNotContain(recoveredText, "[truncated]");
    AssertEqual(false, recovered.Coverage.Truncated);
    AssertEqual(31, recovered.Coverage.IncludedTurnCount);
    return Task.CompletedTask;
}

static List<StoredItem> LongItems()
{
    var items = new List<StoredItem>();
    for (var i = 0; i < 30; i++)
    {
        var marker = $"TURN-{i:D2}-";
        var text = marker + new string('x', 5_000 - marker.Length);
        items.Add(new StoredItem($"t{i}", User(text, null)));
    }

    items.Add(new StoredItem("t0", """{"type":"commandExecution","aggregatedOutput":"SECRET_TOOL_OUTPUT"}"""));
    var huge = "START-" + new string('y', 9_000) + "-END_SECRET";
    items.Add(new StoredItem("t30", User(huge, null)));
    return items;
}

static async Task DumpRecoversSummaryOmissions()
{
    var threads = new FakeThreads(new ThreadSnapshot("threadA", null, null, LongItems()));
    var stdout = new StringWriter();
    var exit = await Cli.RunAsync(
        ["dump", "codex://threads/threadA"],
        _ => null,
        threads,
        new FakeModel(),
        stdout,
        new StringWriter(),
        CancellationToken.None);
    AssertEqual(0, exit);
    using var document = JsonDocument.Parse(stdout.ToString());
    var texts = string.Join("\n", document.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("text").GetString()));
    AssertContains(texts, "TURN-00-");
    AssertContains(texts, "END_SECRET");
    AssertDoesNotContain(texts, "SECRET_TOOL_OUTPUT");
    AssertDoesNotContain(texts, "[truncated]");
    AssertEqual(false, document.RootElement.GetProperty("coverage").GetProperty("truncated").GetBoolean());
    AssertEqual("commandExecution", string.Join(",", document.RootElement.GetProperty("coverage").GetProperty("excludedItemTypes").EnumerateArray().Select(item => item.GetString())));
}

static async Task RereadNamesToolAndCodexEnvironment()
{
    var model = new FakeModel();
    var threads = new FakeThreads(new ThreadSnapshot("abc", null, null, [new StoredItem("t1", User("continue", null))]));
    var output = Path.Combine(Path.GetTempPath(), $"handoff test {Guid.NewGuid():N}.json");
    var entry = Path.Combine(Path.GetTempPath(), "tool dir", "codex-thread-handoff.cs");
    var fullEntry = Path.GetFullPath(entry);
    var fullOutput = Path.GetFullPath(output);
    try
    {
        var exit = await Cli.RunAsync(
            ["codex://threads/abc", "-o", output],
            _ => "dedicated",
            threads,
            model,
            new StringWriter(),
            new StringWriter(),
            CancellationToken.None,
            entry);
        AssertEqual(0, exit);
        using var known = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var reread = known.RootElement.GetProperty("source").GetProperty("reread");
        AssertContains(reread.GetProperty("codexEnvironment").GetString()!, "CODEX_HOME");
        AssertContains(reread.GetProperty("codexEnvironment").GetString()!, "別のマシン");
        AssertContains(reread.GetProperty("toolLocation").GetString()!, fullEntry);
        AssertContains(reread.GetProperty("toolLocation").GetString()!, ArtifactBuilder.RepositoryUrl);
        var dumpDeeplink = reread.GetProperty("dumpFromDeeplink").GetString()!;
        var dumpFile = reread.GetProperty("dumpFromHandoffJson").GetString()!;
        AssertContains(dumpDeeplink, ArtifactBuilder.QuotePowerShellLiteral(fullEntry));
        AssertContains(dumpDeeplink, ArtifactBuilder.QuotePowerShellLiteral("codex://threads/abc"));
        AssertContains(dumpFile, ArtifactBuilder.QuotePowerShellLiteral(fullEntry));
        AssertContains(dumpFile, ArtifactBuilder.QuotePowerShellLiteral(fullOutput));
        AssertContains(reread.GetProperty("toolLocation").GetString()!, "PowerShell");
        File.Delete(output);

        var fallbackOutput = Path.Combine(Path.GetTempPath(), $"handoff-fallback-{Guid.NewGuid():N}.json");
        var fallbackExit = await Cli.RunAsync(
            ["codex://threads/abc", "-o", fallbackOutput],
            _ => "dedicated",
            threads,
            model,
            new StringWriter(),
            new StringWriter(),
            CancellationToken.None);
        AssertEqual(0, fallbackExit);
        using var unknown = JsonDocument.Parse(await File.ReadAllTextAsync(fallbackOutput));
        var fallback = unknown.RootElement.GetProperty("source").GetProperty("reread");
        AssertContains(fallback.GetProperty("toolLocation").GetString()!, "記録されていません");
        AssertContains(fallback.GetProperty("toolLocation").GetString()!, ArtifactBuilder.RepositoryUrl);
        AssertContains(fallback.GetProperty("dumpFromDeeplink").GetString()!, "--file 'codex-thread-handoff.cs' --");
        File.Delete(fallbackOutput);
    }
    finally
    {
        if (File.Exists(output))
        {
            File.Delete(output);
        }
    }
}

static async Task PowerShellDumpArgumentsStayLiteral()
{
    var samples = new[]
    {
        @"C:\tool dir\codex-thread-handoff.cs",
        "C:\\tool$'dir $(whoami)\\it's\\codex-thread-handoff.cs",
    };
    foreach (var sample in samples)
    {
        var script = Path.Combine(Path.GetTempPath(), $"quote-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script, "Write-Output " + ArtifactBuilder.QuotePowerShellLiteral(sample) + Environment.NewLine);
        try
        {
            var actual = (await RunPwshAsync(script)).TrimEnd('\r', '\n');
            AssertEqual(sample, actual);
        }
        finally
        {
            File.Delete(script);
        }
    }
}

static async Task<string> RunPwshAsync(string script)
{
    var start = new ProcessStartInfo
    {
        FileName = "pwsh",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("-NoProfile");
    start.ArgumentList.Add("-File");
    start.ArgumentList.Add(script);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stderr = await stderrTask;
    var stdout = await stdoutTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(stderr);
    }

    return stdout;
}

static Task CodexLaunchResolution()
{
    var nativeName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
    var js = Path.Combine("early", "node_modules", "@openai", "codex", "bin", "codex.js");
    var native = Path.Combine("late", nativeName);
    var jsFirst = CodexLaunchResolver.Resolve(
        $"early{Path.PathSeparator}late",
        path => path == native,
        path => path == js);
    AssertEqual("node", jsFirst.FileName);
    AssertEqual(js, jsFirst.Arguments[0]);
    AssertEqual("app-server", jsFirst.Arguments[1]);

    var nativeFirst = CodexLaunchResolver.Resolve(
        $"late{Path.PathSeparator}early",
        path => path == native,
        path => path == js);
    AssertEqual(native, nativeFirst.FileName);
    AssertEqual("app-server", nativeFirst.Arguments[0]);

    var sameDirectoryNative = Path.Combine("both", nativeName);
    var sameDirectoryJs = Path.Combine("both", "node_modules", "@openai", "codex", "bin", "codex.js");
    var sameDirectory = CodexLaunchResolver.Resolve(
        "both",
        path => path == sameDirectoryNative,
        path => path == sameDirectoryJs);
    AssertEqual(sameDirectoryNative, sameDirectory.FileName);

    var nonExecutableThenJs = CodexLaunchResolver.Resolve(
        "both",
        _ => false,
        path => path == sameDirectoryJs);
    AssertEqual("node", nonExecutableThenJs.FileName);
    AssertEqual(sameDirectoryJs, nonExecutableThenJs.Arguments[0]);
    AssertEqual(false, CodexLaunchResolver.HasAnyExecuteBit(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    AssertEqual(true, CodexLaunchResolver.HasAnyExecuteBit(UnixFileMode.UserExecute));
    AssertEqual(true, CodexLaunchResolver.HasAnyExecuteBit(UnixFileMode.OtherExecute));

    AssertThrows("missing", () => CodexLaunchResolver.Resolve("shim-only", _ => false, _ => false));

    var directory = Path.Combine(Path.GetTempPath(), $"codex-launch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var script = Path.Combine(directory, "codex");
        File.WriteAllText(script, "#!/usr/bin/env node\n");
        var command = Path.Combine(directory, "codex.cmd");
        File.WriteAllText(command, "@echo off\r\n");
        AssertEqual(false, CodexLaunchResolver.IsDirectlyExecutable(command));
        if (OperatingSystem.IsWindows())
        {
            AssertEqual(false, CodexLaunchResolver.IsDirectlyExecutable(script));
            File.WriteAllBytes(script, [(byte)'M', (byte)'Z', 0, 0]);
            AssertEqual(true, CodexLaunchResolver.IsDirectlyExecutable(script));
        }
        else
        {
            AssertEqual(false, CodexLaunchResolver.IsDirectlyExecutable(script));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            AssertEqual(true, CodexLaunchResolver.IsDirectlyExecutable(script));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            AssertEqual(false, CodexLaunchResolver.IsDirectlyExecutable(script));
        }

        var exe = Path.Combine(directory, "codex.exe");
        File.WriteAllBytes(exe, [(byte)'M', (byte)'Z']);
        AssertEqual(OperatingSystem.IsWindows(), CodexLaunchResolver.IsDirectlyExecutable(exe));
    }
    finally
    {
        DeleteDirectory(directory);
    }

    return Task.CompletedTask;
}

static Task Schema()
{
    var valid = """
        {"purpose":"目的","decisions":[],"currentState":["実装済み"],"remainingWork":["残件"],"rejectedApproaches":[],"openQuestions":[],"importantFiles":["src/A.cs"]}
        """;
    var body = HandoffBodyParser.Parse(valid);
    AssertEqual("目的", body.Purpose);
    AssertEqual("src/A.cs", body.ImportantFiles[0]);
    AssertThrows("extra", () => HandoffBodyParser.Parse(valid.Replace("\"purpose\"", "\"extra\":1,\"purpose\"", StringComparison.Ordinal)));
    AssertThrows("missing", () => HandoffBodyParser.Parse(valid.Replace("\"openQuestions\":[],", "", StringComparison.Ordinal)));
    AssertThrows("type", () => HandoffBodyParser.Parse(valid.Replace("\"decisions\":[]", "\"decisions\":\"no\"", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static async Task GitSnapshot()
{
    var repo = await CreateRepoAsync();
    try
    {
        var capture = await GitSnapshotCollector.CaptureAsync(repo, CancellationToken.None);
        AssertEqual(true, capture.Snapshot is not null);
        AssertContains(capture.Snapshot!.StatusShort, "tracked.txt");
        AssertContains(capture.Snapshot.StatusShort, "new.txt");
        AssertContains(capture.Snapshot.DiffStat, "tracked.txt");
        AssertDoesNotContain(capture.Snapshot.DiffStat, "UNIQUE_DIFF_BODY_LINE");
        AssertEqual(true, capture.Snapshot.Dirty);
        AssertEqual(true, capture.Snapshot.Head.Length > 0);

        var fileUri = new Uri(repo).AbsoluteUri;
        var fromUri = await GitSnapshotCollector.CaptureAsync(fileUri, CancellationToken.None);
        AssertEqual(true, fromUri.Snapshot is not null);

        var model = new FakeModel();
        var threads = new FakeThreads(new ThreadSnapshot(
            "abc",
            "title",
            repo,
            [new StoredItem("t1", User("実装した", null)), new StoredItem("t1", """{"type":"commandExecution","aggregatedOutput":"SECRET_TOOL_OUTPUT"}""")]));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var output = Path.Combine(Path.GetTempPath(), $"handoff-test-{Guid.NewGuid():N}.json");
        var exit = await Cli.RunAsync(
            ["codex://threads/abc", "-o", output, "--model", "gpt-test"],
            _ => "test-key",
            threads,
            model,
            stdout,
            stderr,
            CancellationToken.None);
        AssertEqual(0, exit);
        AssertEqual("gpt-test", model.Model);
        AssertEqual(Path.GetFullPath(repo), model.WorkingDirectory);
        AssertContains(model.Prompt!, "may already be committed");
        AssertDoesNotContain(model.Prompt!, "no such change");
        AssertContains(model.Prompt!, "tracked.txt");
        AssertContains(model.Prompt!, "git diff --stat:");
        AssertDoesNotContain(model.Prompt!, "UNIQUE_DIFF_BODY_LINE");
        AssertDoesNotContain(model.Prompt!, "SECRET_TOOL_OUTPUT");
        var artifact = await File.ReadAllTextAsync(output);
        AssertContains(artifact, "tracked.txt");
        AssertContains(artifact, "\"dirty\": true");
        AssertDoesNotContain(artifact, "gpt-test");
        AssertDoesNotContain(artifact, "test-key");
        AssertDoesNotContain(artifact, "SECRET_TOOL_OUTPUT");
        File.Delete(output);
    }
    finally
    {
        DeleteDirectory(repo);
    }
}

static async Task MissingRepository()
{
    var model = new FakeModel();
    var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
    var nonGit = Path.Combine(Path.GetTempPath(), $"nongit-{Guid.NewGuid():N}");
    Directory.CreateDirectory(nonGit);
    try
    {
        foreach (var cwd in new string?[] { null, missingPath, nonGit })
        {
            model.Called = false;
            var threads = new FakeThreads(new ThreadSnapshot("abc", null, cwd, [new StoredItem("t1", User("continue", null))]));
            var output = Path.Combine(Path.GetTempPath(), $"handoff-test-{Guid.NewGuid():N}.json");
            var exit = await Cli.RunAsync(
                ["[title](codex://threads/abc)", "-o", output],
                name => name == ApiKeyResolver.VariableName ? "dedicated" : "generic",
                threads,
                model,
                new StringWriter(),
                new StringWriter(),
                CancellationToken.None);
            AssertEqual(0, exit);
            AssertEqual(true, model.Called);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            AssertEqual(JsonValueKind.Null, document.RootElement.GetProperty("repository").ValueKind);
            AssertEqual(true, document.RootElement.GetProperty("warnings").GetArrayLength() > 0);
            File.Delete(output);
        }

        var denied = new StringWriter();
        var threadsForKey = new FakeThreads(new ThreadSnapshot("abc", null, null, []));
        var keyExit = await Cli.RunAsync(
            ["codex://threads/abc"],
            name => name == "OPENAI_API_KEY" ? "generic" : null,
            threadsForKey,
            model,
            new StringWriter(),
            denied,
            CancellationToken.None);
        AssertEqual(1, keyExit);
        AssertContains(denied.ToString(), ApiKeyResolver.VariableName);
        AssertEqual(0, threadsForKey.Calls);
    }
    finally
    {
        DeleteDirectory(nonGit);
    }
}

static async Task DumpBothInputs()
{
    var threads = new FakeThreads(new ThreadSnapshot(
        "threadA",
        "title",
        null,
        [
            new StoredItem("t1", User("hello", null)),
            new StoredItem("t1", """{"type":"commandExecution","aggregatedOutput":"SECRET_TOOL_OUTPUT"}"""),
            new StoredItem("t1", Agent("done", "final_answer")),
        ]));
    var handoffPath = Path.Combine(Path.GetTempPath(), $"handoff-test-{Guid.NewGuid():N}.json");
    var artifact = new HandoffArtifact(
        "1",
        new SourceInfo(
            "codex://threads/other-deeplink-ignored",
            "threadA",
            "title",
            new RereadInfo("human", "same Codex home", "tool location", "dump-deeplink", "dump-file")),
        null,
        SampleBody(),
        new CoverageInfo([], [], 0, 0, false, ""),
        []);
    await File.WriteAllTextAsync(handoffPath, JsonText.Serialize(artifact));
    try
    {
        var fromDeeplink = new StringWriter();
        var fromFile = new StringWriter();
        AssertEqual(0, await Cli.RunAsync(["dump", "codex://threads/threadA"], _ => null, threads, new FakeModel(), fromDeeplink, new StringWriter(), CancellationToken.None));
        AssertEqual(0, await Cli.RunAsync(["dump", handoffPath], _ => null, threads, new FakeModel(), fromFile, new StringWriter(), CancellationToken.None));
        AssertEqual(fromDeeplink.ToString(), fromFile.ToString());
        AssertDoesNotContain(fromDeeplink.ToString(), "SECRET_TOOL_OUTPUT");
        AssertContains(fromDeeplink.ToString(), "hello");
        AssertContains(fromDeeplink.ToString(), "done");
        AssertEqual(2, threads.Requested.Count);
        AssertEqual("threadA", threads.Requested[0]);
        AssertEqual("threadA", threads.Requested[1]);
    }
    finally
    {
        File.Delete(handoffPath);
    }
}

static Task AppServerJsonLinesHaveNoBom()
{
    AssertEqual(0, CodexAppServerThreadSource.JsonLineEncoding.GetPreamble().Length);
    using var stream = new MemoryStream();
    using (var writer = new StreamWriter(stream, CodexAppServerThreadSource.JsonLineEncoding, leaveOpen: true))
    {
        writer.WriteLine("{\"method\":\"initialize\"}");
    }

    var bytes = stream.ToArray();
    AssertEqual((byte)'{', bytes[0]);
    return Task.CompletedTask;
}

static async Task AppServerProtocol()
{
    var missing = await RunServerAsync("missing", static (method, root) =>
    {
        var id = root["id"]!.GetValue<int>();
        if (method == "initialize")
        {
            return Result(id, new JsonObject { ["userAgent"] = "test" });
        }

        if (method == "thread/read")
        {
            return Error(id, "thread not found");
        }

        return Error(id, "unexpected method");
    });
    AssertEqual("initialize,initialized,thread/read", string.Join(",", missing.Methods));
    AssertContains(missing.Error!, "thread not found");
    AssertDoesNotContain(string.Join("\n", missing.Requests), "includeTurns");
    AssertDoesNotContain(string.Join("\n", missing.Requests), "thread/list");

    var pages = await RunServerAsync("abc", static (method, root) =>
    {
        var id = root["id"]!.GetValue<int>();
        if (method == "initialize")
        {
            return Result(id, new JsonObject { ["userAgent"] = "test" });
        }

        if (method == "thread/read")
        {
            return Result(id, new JsonObject
            {
                ["thread"] = new JsonObject
                {
                    ["id"] = "abc",
                    ["name"] = "Bug bash",
                    ["cwd"] = "C:\\repo",
                },
            });
        }

        if (method == "thread/items/list")
        {
            var cursor = root["params"]?["cursor"]?.GetValue<string>();
            if (cursor is null)
            {
                return Result(id, Page("turn-1", "first", "page-2"));
            }

            return Result(id, Page("turn-2", "second", null));
        }

        return Error(id, "unexpected method");
    });
    AssertEqual(null, pages.Error);
    AssertEqual("Bug bash", pages.Snapshot!.Title);
    AssertEqual("C:\\repo", pages.Snapshot.Cwd);
    AssertEqual(2, pages.Snapshot.Items.Count);
    AssertContains(pages.Snapshot.Items[0].ItemJson, "first");
    AssertContains(pages.Snapshot.Items[1].ItemJson, "second");
    AssertDoesNotContain(string.Join("\n", pages.Requests), "includeTurns");

    var mismatch = await RunServerAsync("abc", static (method, root) =>
    {
        var id = root["id"]!.GetValue<int>();
        if (method == "initialize")
        {
            return Result(id, new JsonObject());
        }

        if (method == "thread/read")
        {
            return Result(id, new JsonObject { ["thread"] = new JsonObject { ["id"] = "other" } });
        }

        return Error(id, "unexpected method");
    });
    AssertContains(mismatch.Error!, "other");
    AssertEqual(false, mismatch.Methods.Contains("thread/items/list"));
}

static JsonObject Result(int id, JsonObject result) => new()
{
    ["id"] = id,
    ["result"] = result,
};

static JsonObject Error(int id, string message) => new()
{
    ["id"] = id,
    ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
};

static JsonObject Page(string turnId, string text, string? cursor) => new()
{
    ["data"] = new JsonArray(new JsonObject
    {
        ["turnId"] = turnId,
        ["item"] = new JsonObject
        {
            ["type"] = "userMessage",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    }),
    ["nextCursor"] = cursor is null ? null : JsonValue.Create(cursor),
};

static async Task<ServerRun> RunServerAsync(string threadId, Func<string, JsonObject, JsonObject?> respond)
{
    var requestsToServer = new LineBuffer();
    var responsesToClient = new LineBuffer();
    var methods = new List<string>();
    var requests = new List<string>();
    var server = Task.Run(async () =>
    {
        while (true)
        {
            var line = await requestsToServer.Reader.ReadLineAsync();
            if (line is null)
            {
                return;
            }

            requests.Add(line);
            var root = JsonNode.Parse(line)!.AsObject();
            var method = root["method"]!.GetValue<string>();
            lock (methods)
            {
                methods.Add(method);
            }

            if (method == "initialized")
            {
                continue;
            }

            var response = respond(method, root);
            if (response is not null)
            {
                await responsesToClient.Writer.WriteLineAsync(response.ToJsonString());
            }
        }
    });

    ThreadSnapshot? snapshot = null;
    string? error = null;
    await using var rpc = new LineJsonRpc(requestsToServer.Writer, responsesToClient.Reader);
    try
    {
        snapshot = await AppServerThreadReader.ReadAsync(rpc, threadId, CancellationToken.None);
    }
    catch (HandoffException ex)
    {
        error = ex.Message;
    }

    requestsToServer.Writer.Dispose();
    await server.WaitAsync(TimeSpan.FromSeconds(5));
    return new ServerRun(snapshot, error, methods.ToArray(), requests.ToArray());
}

static string User(string text, string? imageSecret)
{
    var content = new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = text });
    if (imageSecret is not null)
    {
        content.Add((JsonNode)new JsonObject
        {
            ["type"] = "image",
            ["url"] = $"data:image/png;base64,{imageSecret}",
        });
    }

    return new JsonObject { ["type"] = "userMessage", ["content"] = content }.ToJsonString();
}

static string Agent(string text, string phase) =>
    new JsonObject { ["type"] = "agentMessage", ["text"] = text, ["phase"] = phase }.ToJsonString();

static HandoffBody SampleBody() => new("purpose", [], ["state"], [], [], [], []);

static async Task<string> CreateRepoAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"handoff-repo-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    await Git(directory, "init");
    await Git(directory, "config", "user.email", "test@example.com");
    await Git(directory, "config", "user.name", "test");
    await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "base\n");
    await Git(directory, "add", "tracked.txt");
    await Git(directory, "-c", "commit.gpgsign=false", "commit", "-m", "init");
    await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "base\nUNIQUE_DIFF_BODY_LINE\n");
    await File.WriteAllTextAsync(Path.Combine(directory, "new.txt"), "new\n");
    return directory;
}

static async Task Git(string directory, params string[] arguments)
{
    var start = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = directory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stderr = await stderrTask;
    await stdoutTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(stderr);
    }
}

static void DeleteDirectory(string path)
{
    if (!Directory.Exists(path))
    {
        return;
    }

    foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(entry, FileAttributes.Normal);
    }

    Directory.Delete(path, recursive: true);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertContains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected to find '{expected}'.");
    }
}

static void AssertDoesNotContain(string text, string unexpected)
{
    if (text.Contains(unexpected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Did not expect to find '{unexpected}'.");
    }
}

static void AssertThrows(string name, Action action)
{
    try
    {
        action();
    }
    catch (HandoffException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected failure: {name}");
}

static async Task AssertThrowsAsync(string name, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (HandoffException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected failure: {name}");
}

static async Task<string> CaptureHandoffExceptionAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (HandoffException ex)
    {
        return ex.Message;
    }

    throw new InvalidOperationException("Expected HandoffException.");
}

internal sealed class FakeThreads(ThreadSnapshot snapshot) : IThreadSource
{
    public List<string> Requested { get; } = [];
    public int Calls { get; private set; }

    public Task<ThreadSnapshot> ReadAsync(string threadId, CancellationToken cancellationToken)
    {
        Calls++;
        Requested.Add(threadId);
        if (!string.Equals(snapshot.ThreadId, threadId, StringComparison.Ordinal))
        {
            throw new HandoffException($"App server thread/read failed: thread not found");
        }

        return Task.FromResult(snapshot);
    }
}

internal sealed class FakeModel : IHandoffModel
{
    public bool Called { get; set; }
    public string? Model { get; private set; }
    public string? Prompt { get; private set; }
    public string? WorkingDirectory { get; private set; }

    public Task<HandoffBody> CreateAsync(
        string model,
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Called = true;
        Model = model;
        Prompt = prompt;
        WorkingDirectory = workingDirectory;
        return Task.FromResult(new HandoffBody("purpose", [], ["state"], ["rest"], [], [], ["tracked.txt"]));
    }
}

internal sealed class FailingModel : IHandoffModel
{
    public Task<HandoffBody> CreateAsync(
        string model,
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        throw new HandoffException("stub model failure");
}

internal sealed record ServerRun(ThreadSnapshot? Snapshot, string? Error, string[] Methods, string[] Requests);

sealed class LineBuffer
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

    public LineBuffer()
    {
        Writer = new LineBufferWriter(_lines.Writer);
        Reader = new LineBufferReader(_lines.Reader);
    }

    public TextWriter Writer { get; }

    public TextReader Reader { get; }
}

sealed class LineBufferWriter(ChannelWriter<string?> writer) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value) => writer.TryWrite(value);

    public override Task WriteLineAsync(string? value)
    {
        writer.TryWrite(value);
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        writer.TryComplete();
        base.Dispose(disposing);
    }
}

sealed class LineBufferReader(ChannelReader<string?> reader) : TextReader
{
    public override string? ReadLine()
    {
        return ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.TryRead(out var line))
            {
                return line;
            }
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        return null;
    }
}
