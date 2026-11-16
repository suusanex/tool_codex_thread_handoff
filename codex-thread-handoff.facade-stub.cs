#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable

using System.Text.Json;
using System.Text.Json.Nodes;

var requestJson = await Console.In.ReadToEndAsync();
if (Environment.GetEnvironmentVariable("CODEX_THREAD_HANDOFF_TEST_STUB_MODE") == "wait")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 1;
}

try
{
    using var request = JsonDocument.Parse(requestJson);
    var root = request.RootElement;
    var agent = root.GetProperty("agent").GetString();
    var model = root.GetProperty("model").GetString();
    var workingDirectory = root.GetProperty("working_directory").GetString();
    var timeoutSeconds = root.GetProperty("timeout_seconds").GetInt32();
    var autoApprove = root.GetProperty("auto_approve").GetBoolean();
    if (agent != "github-copilot" ||
        string.IsNullOrWhiteSpace(model) ||
        string.IsNullOrWhiteSpace(workingDirectory) ||
        !Directory.Exists(workingDirectory) ||
        autoApprove ||
        timeoutSeconds < 1)
    {
        return 2;
    }

    var body = new JsonObject
    {
        ["purpose"] = $"model={model};workingDirectory={workingDirectory}",
        ["decisions"] = new JsonArray(),
        ["currentState"] = new JsonArray(),
        ["remainingWork"] = new JsonArray(),
        ["rejectedApproaches"] = new JsonArray(),
        ["openQuestions"] = new JsonArray(),
        ["importantFiles"] = new JsonArray(),
    };
    var response = new JsonObject
    {
        ["status"] = "completed",
        ["result"] = new JsonObject
        {
            ["agent"] = "github-copilot",
            ["exitCode"] = 0,
            ["outputKind"] = "final_response",
            ["outputText"] = body.ToJsonString(),
        },
    };
    Console.Out.Write(response.ToJsonString());
    return 0;
}
catch (JsonException)
{
    return 2;
}
