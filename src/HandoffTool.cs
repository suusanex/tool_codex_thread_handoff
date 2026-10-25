using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenAI.Responses;

namespace CodexThreadHandoff;

/// <summary>
/// 利用者向けに失敗理由を返して停止するための例外。自動復旧はしない。
/// </summary>
internal sealed class HandoffException : Exception
{
    public HandoffException(string message) : base(message)
    {
    }
}

internal static class DeeplinkParser
{
    private static readonly Regex ValidDeeplink = new(
        @"codex://threads/(?<id>[A-Za-z0-9_-]+)(?=$|[\s\)\]>,])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CodexScheme = new(
        @"codex://",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string ParseThreadId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new HandoffException("Deeplink is empty.");
        }

        var matches = ValidDeeplink.Matches(input);
        if (matches.Count == 0)
        {
            throw new HandoffException("Input does not contain a codex://threads/<id> deeplink.");
        }

        foreach (Match scheme in CodexScheme.Matches(input))
        {
            var covered = false;
            foreach (Match valid in matches)
            {
                if (scheme.Index >= valid.Index && scheme.Index < valid.Index + valid.Length)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                throw new HandoffException("Input contains a malformed Codex deeplink.");
            }
        }

        var threadId = matches[0].Groups["id"].Value;
        for (var i = 1; i < matches.Count; i++)
        {
            if (!string.Equals(matches[i].Groups["id"].Value, threadId, StringComparison.Ordinal))
            {
                throw new HandoffException("Input contains multiple Codex thread ids.");
            }
        }

        return threadId;
    }

    public static string ToDeeplink(string threadId) => $"codex://threads/{threadId}";
}

/// <summary>
/// 専用環境変数だけを読む。OPENAI_API_KEY へフォールバックすると、別用途の key で課金や Data Sharing が起きる。
/// </summary>
internal static class ApiKeyResolver
{
    public const string VariableName = "CODEX_THREAD_HANDOFF_OPENAI_API_KEY";

    public static string Resolve(Func<string, string?> readVariable)
    {
        var value = readVariable(VariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new HandoffException($"{VariableName} is not set.");
        }

        return value.Trim();
    }
}

internal sealed record StoredItem(string TurnId, string ItemJson);

internal sealed record ThreadSnapshot(string ThreadId, string? Title, string? Cwd, IReadOnlyList<StoredItem> Items);

internal sealed record TranscriptMessage(string TurnId, string Role, string Text);

internal sealed record CoverageInfo(
    [property: JsonPropertyName("includedItemTypes")] IReadOnlyList<string> IncludedItemTypes,
    [property: JsonPropertyName("excludedItemTypes")] IReadOnlyList<string> ExcludedItemTypes,
    [property: JsonPropertyName("turnCount")] int TurnCount,
    [property: JsonPropertyName("includedTurnCount")] int IncludedTurnCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("notes")] string Notes);

internal sealed record FilteredTranscript(
    IReadOnlyList<TranscriptMessage> Messages,
    CoverageInfo Coverage);

internal static class TranscriptFilter
{
    public const int MaxMessageChars = 8_000;
    public const int MaxTranscriptChars = 120_000;
    private const string TruncationSuffix = "\n[truncated]";

    public static FilteredTranscript Filter(IReadOnlyList<StoredItem> items)
    {
        var turns = GroupTurns(items);
        var renderedTurns = new List<RenderedTurn>(turns.Count);
        var includedTypes = new SortedSet<string>(StringComparer.Ordinal);
        var excludedTypes = new SortedSet<string>(StringComparer.Ordinal);
        var messageTruncated = false;
        var skippedAgentMessages = 0;

        foreach (var turn in turns)
        {
            var chosenAgent = ChooseAgentMessage(turn.Items);
            var messages = new List<TranscriptMessage>();
            foreach (var item in turn.Items)
            {
                if (!item.Payload.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    excludedTypes.Add("unknown");
                    continue;
                }

                var type = typeElement.GetString() ?? "unknown";
                if (type == "userMessage")
                {
                    var user = ReadUserMessage(item.Payload);
                    if (user.Text.Length == 0 && user.ImageCount == 0)
                    {
                        continue;
                    }

                    var text = user.Text;
                    if (user.ImageCount > 0)
                    {
                        if (text.Length > 0)
                        {
                            text += "\n";
                        }

                        text += $"[images omitted: {user.ImageCount}]";
                    }

                    messages.Add(new TranscriptMessage(turn.TurnId, "user", Clip(text, ref messageTruncated)));
                    includedTypes.Add("userMessage");
                    continue;
                }

                if (type == "agentMessage")
                {
                    if (!ReferenceEquals(item, chosenAgent))
                    {
                        skippedAgentMessages++;
                        continue;
                    }

                    var text = ReadAgentText(item.Payload);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    messages.Add(new TranscriptMessage(turn.TurnId, "assistant", Clip(text, ref messageTruncated)));
                    includedTypes.Add("agentMessage");
                    continue;
                }

                excludedTypes.Add(type);
            }

            if (messages.Count > 0)
            {
                renderedTurns.Add(new RenderedTurn(turn.TurnId, messages));
            }
        }

        var kept = SelectNewest(renderedTurns, out var droppedTurns, out var clippedNewestTurn);
        var notes = new StringBuilder(
            "Default model input keeps user text and the final agent message in each turn. Tool output, command output, and diff bodies are excluded.");
        if (skippedAgentMessages > 0)
        {
            notes.Append($" Skipped {skippedAgentMessages} non-final agent messages.");
        }

        if (droppedTurns > 0)
        {
            notes.Append($" Omitted {droppedTurns} older turns to stay within {MaxTranscriptChars} characters.");
        }

        if (clippedNewestTurn)
        {
            notes.Append(" The newest turn was shortened to stay within the transcript character limit.");
        }

        var includedTurnCount = kept.Select(message => message.TurnId).Distinct(StringComparer.Ordinal).Count();
        return new FilteredTranscript(
            kept,
            new CoverageInfo(
                includedTypes.ToArray(),
                excludedTypes.ToArray(),
                turns.Count,
                includedTurnCount,
                messageTruncated || droppedTurns > 0 || clippedNewestTurn,
                notes.ToString()));
    }

    private static List<TurnGroup> GroupTurns(IReadOnlyList<StoredItem> items)
    {
        var turns = new List<TurnGroup>();
        var index = new Dictionary<string, TurnGroup>(StringComparer.Ordinal);
        foreach (var stored in items)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(stored.ItemJson);
            }
            catch (JsonException ex)
            {
                throw new HandoffException($"Stored thread item is not valid JSON: {ex.Message}");
            }

            using (document)
            {
                if (!index.TryGetValue(stored.TurnId, out var turn))
                {
                    turn = new TurnGroup(stored.TurnId);
                    index.Add(stored.TurnId, turn);
                    turns.Add(turn);
                }

                turn.Items.Add(new ParsedItem(document.RootElement.Clone()));
            }
        }

        return turns;
    }

    private static ParsedItem? ChooseAgentMessage(List<ParsedItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (IsAgent(items[i].Payload) && Phase(items[i].Payload) == "final_answer")
            {
                return items[i];
            }
        }

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (IsAgent(items[i].Payload))
            {
                return items[i];
            }
        }

        return null;
    }

    private static bool IsAgent(JsonElement item) =>
        item.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() == "agentMessage";

    private static string? Phase(JsonElement item)
    {
        if (item.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String)
        {
            return phase.GetString();
        }

        return null;
    }

    private static string ReadAgentText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? "";
        }

        return "";
    }

    private static (string Text, int ImageCount) ReadUserMessage(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return ("", 0);
        }

        var builder = new StringBuilder();
        var images = 0;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeElement.GetString();
            if (type == "text" &&
                part.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(textElement.GetString());
                continue;
            }

            if (type is "image" or "localImage")
            {
                images++;
            }
        }

        return (builder.ToString(), images);
    }

    private static string Clip(string text, ref bool truncated)
    {
        if (text.Length <= MaxMessageChars)
        {
            return text;
        }

        truncated = true;
        var keep = Math.Max(0, MaxMessageChars - TruncationSuffix.Length);
        return text[..keep] + TruncationSuffix;
    }

    private static List<TranscriptMessage> SelectNewest(
        List<RenderedTurn> turns,
        out int droppedTurns,
        out bool clippedNewestTurn)
    {
        droppedTurns = 0;
        clippedNewestTurn = false;
        if (turns.Count == 0)
        {
            return [];
        }

        var selected = new List<RenderedTurn>();
        var used = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var length = turns[i].Length;
            if (selected.Count > 0 && used + length > MaxTranscriptChars)
            {
                droppedTurns = i + 1;
                break;
            }

            selected.Add(turns[i]);
            used += length;
        }

        selected.Reverse();
        if (selected.Count == 1 && selected[0].Length > MaxTranscriptChars)
        {
            clippedNewestTurn = true;
            return ClipTurn(selected[0]);
        }

        return selected.SelectMany(turn => turn.Messages).ToList();
    }

    private static List<TranscriptMessage> ClipTurn(RenderedTurn turn)
    {
        var kept = new List<TranscriptMessage>();
        var used = 0;
        for (var i = turn.Messages.Count - 1; i >= 0; i--)
        {
            var length = turn.Messages[i].Text.Length;
            if (kept.Count > 0 && used + length > MaxTranscriptChars)
            {
                break;
            }

            kept.Add(turn.Messages[i]);
            used += length;
            if (used > MaxTranscriptChars)
            {
                break;
            }
        }

        kept.Reverse();
        return kept;
    }

    private sealed class TurnGroup(string turnId)
    {
        public string TurnId { get; } = turnId;
        public List<ParsedItem> Items { get; } = [];
    }

    private sealed class ParsedItem(JsonElement payload)
    {
        public JsonElement Payload { get; } = payload;
    }

    private sealed class RenderedTurn(string turnId, List<TranscriptMessage> messages)
    {
        public string TurnId { get; } = turnId;
        public List<TranscriptMessage> Messages { get; } = messages;
        public int Length { get; } = messages.Sum(message => message.Text.Length);
    }
}

internal sealed record RepositorySnapshot(
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("dirty")] bool Dirty,
    [property: JsonPropertyName("statusShort")] string StatusShort,
    [property: JsonPropertyName("diffStat")] string DiffStat);

internal sealed record RepositoryCapture(RepositorySnapshot? Snapshot, IReadOnlyList<string> Warnings);

internal static class GitSnapshotCollector
{
    public const int MaxCommandChars = 16_000;

    public static Task<RepositoryCapture> CaptureAsync(string? cwd, CancellationToken cancellationToken) =>
        CaptureAsync(cwd, RunGitAsync, cancellationToken);

    internal static async Task<RepositoryCapture> CaptureAsync(
        string? cwd,
        Func<string, string[], CancellationToken, Task<GitCommandResult>> runGit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return Missing("Thread metadata has no cwd.");
        }

        cwd = NormalizeCwd(cwd);
        if (!Directory.Exists(cwd))
        {
            return Missing($"Thread cwd does not exist: {cwd}");
        }

        var inside = await runGit(cwd, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!inside.Succeeded || !string.Equals(inside.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Missing($"Thread cwd is not a Git work tree: {cwd}");
        }

        var branch = await runGit(cwd, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var head = await runGit(cwd, ["rev-parse", "HEAD"], cancellationToken);
        var status = await runGit(cwd, ["status", "--short"], cancellationToken);
        var diff = await runGit(cwd, ["diff", "--stat"], cancellationToken);
        if (!branch.Succeeded || !head.Succeeded || !status.Succeeded || !diff.Succeeded)
        {
            var failed = FirstFailure(branch, head, status, diff);
            return Missing($"git command failed: {TrimForWarning(failed)}");
        }

        var warnings = new List<string>();
        var statusText = Bound(status.Stdout, "git status --short", warnings);
        var diffText = Bound(diff.Stdout, "git diff --stat", warnings);
        var snapshot = new RepositorySnapshot(
            cwd,
            branch.Stdout.Trim(),
            head.Stdout.Trim(),
            status.Stdout.Any(character => !char.IsWhiteSpace(character)),
            statusText.TrimEnd(),
            diffText.TrimEnd());
        return new RepositoryCapture(snapshot, warnings);
    }

    private static RepositoryCapture Missing(string warning) => new(null, [warning]);

    private static string NormalizeCwd(string cwd)
    {
        if (cwd.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(cwd, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            return uri.LocalPath;
        }

        return cwd;
    }

    private static string Bound(string text, string label, List<string> warnings)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length <= MaxCommandChars)
        {
            return normalized;
        }

        warnings.Add($"{label} output was truncated.");
        var keep = Math.Max(0, MaxCommandChars - "\n[truncated]".Length);
        return normalized[..keep] + "\n[truncated]";
    }

    private static string FirstFailure(params GitCommandResult[] results)
    {
        foreach (var result in results)
        {
            if (!result.Succeeded)
            {
                var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
                return string.IsNullOrWhiteSpace(detail) ? $"exit {result.ExitCode}" : detail.Trim();
            }
        }

        return "unknown git failure";
    }

    private static string TrimForWarning(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string cwd,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new GitCommandResult(false, "", "Failed to start git.", -1);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(false, "", ex.Message, -1);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new GitCommandResult(false, "", "git command timed out.", -1);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitCommandResult(process.ExitCode == 0, stdout, stderr, process.ExitCode);
    }
}

internal sealed record GitCommandResult(bool Succeeded, string Stdout, string Stderr, int ExitCode);

internal static class PromptBuilder
{
    public static string Build(
        FilteredTranscript transcript,
        string? title,
        RepositoryCapture repository)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Create a lossy handoff so another coding agent can continue the work without the original thread history.");
        builder.AppendLine("Use only the filtered transcript and repository status below.");
        builder.AppendLine("Write every JSON string in the dominant language of the user messages.");
        builder.AppendLine("purpose is the current goal.");
        builder.AppendLine("decisions are important decisions that are already made.");
        builder.AppendLine("currentState describes completed work and the actual state. When the transcript says work is implemented but git status shows uncommitted changes or no such change, say that in currentState.");
        builder.AppendLine("remainingWork is work that is still open.");
        builder.AppendLine("rejectedApproaches are failed or rejected approaches that should not be retried. Use an empty array when none are important.");
        builder.AppendLine("openQuestions are important uncertainties and risks. Use an empty array when none are important.");
        builder.AppendLine("importantFiles are paths the next agent should open. Use an empty array when no file is important.");
        builder.AppendLine("Do not treat this summary as a complete copy of the source thread.");
        builder.AppendLine();
        builder.AppendLine($"Thread title: {title ?? "(none)"}");
        builder.AppendLine();
        builder.AppendLine("Filtered transcript:");
        if (transcript.Messages.Count == 0)
        {
            builder.AppendLine("(no user or final agent messages)");
        }
        else
        {
            foreach (var message in transcript.Messages)
            {
                builder.AppendLine($"[{message.Role}]");
                builder.AppendLine(message.Text);
                builder.AppendLine();
            }
        }

        builder.AppendLine("Repository status:");
        if (repository.Snapshot is null)
        {
            builder.AppendLine("unavailable");
            foreach (var warning in repository.Warnings)
            {
                builder.AppendLine(warning);
            }
        }
        else
        {
            var snapshot = repository.Snapshot;
            builder.AppendLine($"cwd: {snapshot.Cwd}");
            builder.AppendLine($"branch: {snapshot.Branch}");
            builder.AppendLine($"HEAD: {snapshot.Head}");
            builder.AppendLine($"dirty: {snapshot.Dirty}");
            builder.AppendLine("git status --short:");
            builder.AppendLine(snapshot.StatusShort);
            builder.AppendLine("git diff --stat:");
            builder.AppendLine(snapshot.DiffStat);
            foreach (var warning in repository.Warnings)
            {
                builder.AppendLine(warning);
            }
        }

        return builder.ToString();
    }
}

internal sealed record HandoffBody(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("decisions")] IReadOnlyList<string> Decisions,
    [property: JsonPropertyName("currentState")] IReadOnlyList<string> CurrentState,
    [property: JsonPropertyName("remainingWork")] IReadOnlyList<string> RemainingWork,
    [property: JsonPropertyName("rejectedApproaches")] IReadOnlyList<string> RejectedApproaches,
    [property: JsonPropertyName("openQuestions")] IReadOnlyList<string> OpenQuestions,
    [property: JsonPropertyName("importantFiles")] IReadOnlyList<string> ImportantFiles);

internal static class HandoffBodyParser
{
    public const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "purpose": { "type": "string" },
            "decisions": { "type": "array", "items": { "type": "string" } },
            "currentState": { "type": "array", "items": { "type": "string" } },
            "remainingWork": { "type": "array", "items": { "type": "string" } },
            "rejectedApproaches": { "type": "array", "items": { "type": "string" } },
            "openQuestions": { "type": "array", "items": { "type": "string" } },
            "importantFiles": { "type": "array", "items": { "type": "string" } }
          },
          "required": [
            "purpose",
            "decisions",
            "currentState",
            "remainingWork",
            "rejectedApproaches",
            "openQuestions",
            "importantFiles"
          ]
        }
        """;

    private static readonly string[] StringArrayProperties =
    [
        "decisions",
        "currentState",
        "remainingWork",
        "rejectedApproaches",
        "openQuestions",
        "importantFiles",
    ];

    public static HandoffBody Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HandoffException($"OpenAI handoff body was not JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new HandoffException("OpenAI handoff body was not a JSON object.");
            }

            var allowed = new HashSet<string>(StringArrayProperties, StringComparer.Ordinal) { "purpose" };
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new HandoffException($"OpenAI handoff body contains unsupported property '{property.Name}'.");
                }
            }

            if (!document.RootElement.TryGetProperty("purpose", out var purpose) || purpose.ValueKind != JsonValueKind.String)
            {
                throw new HandoffException("OpenAI handoff body is missing string property 'purpose'.");
            }

            var arrays = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var name in StringArrayProperties)
            {
                if (!document.RootElement.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    throw new HandoffException($"OpenAI handoff body is missing string array '{name}'.");
                }

                var values = new List<string>();
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        throw new HandoffException($"OpenAI handoff body property '{name}' contains a non-string item.");
                    }

                    values.Add(entry.GetString() ?? "");
                }

                arrays.Add(name, values);
            }

            return new HandoffBody(
                purpose.GetString() ?? "",
                arrays["decisions"],
                arrays["currentState"],
                arrays["remainingWork"],
                arrays["rejectedApproaches"],
                arrays["openQuestions"],
                arrays["importantFiles"]);
        }
    }
}

internal sealed record RereadInfo(
    [property: JsonPropertyName("human")] string Human,
    [property: JsonPropertyName("dumpFromDeeplink")] string DumpFromDeeplink,
    [property: JsonPropertyName("dumpFromHandoffJson")] string DumpFromHandoffJson);

internal sealed record SourceInfo(
    [property: JsonPropertyName("deeplink")] string Deeplink,
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("reread")] RereadInfo Reread);

internal sealed record HandoffArtifact(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("source")] SourceInfo Source,
    [property: JsonPropertyName("repository")] RepositorySnapshot? Repository,
    [property: JsonPropertyName("handoff")] HandoffBody Handoff,
    [property: JsonPropertyName("coverage")] CoverageInfo Coverage,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

internal static class ArtifactBuilder
{
    public const string HumanReread =
        "Open the deeplink in the Codex GUI. Codex GUI で deeplink を開くと元 thread を確認できます。";

    public static HandoffArtifact Build(
        ThreadSnapshot thread,
        HandoffBody body,
        RepositoryCapture repository,
        FilteredTranscript transcript,
        string outputPath)
    {
        var deeplink = DeeplinkParser.ToDeeplink(thread.ThreadId);
        var quotedPath = outputPath.Contains(' ', StringComparison.Ordinal) ? $"\"{outputPath}\"" : outputPath;
        return new HandoffArtifact(
            "1",
            new SourceInfo(
                deeplink,
                thread.ThreadId,
                thread.Title,
                new RereadInfo(
                    HumanReread,
                    $"dotnet run --file codex-thread-handoff.cs -- dump {deeplink}",
                    $"dotnet run --file codex-thread-handoff.cs -- dump {quotedPath}")),
            repository.Snapshot,
            body,
            transcript.Coverage,
            repository.Warnings);
    }
}

internal sealed record DumpMessage(
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text);

internal sealed record DumpDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("deeplink")] string Deeplink,
    [property: JsonPropertyName("messages")] IReadOnlyList<DumpMessage> Messages,
    [property: JsonPropertyName("coverage")] CoverageInfo Coverage);

internal static class DumpBuilder
{
    public static DumpDocument Build(string threadId, FilteredTranscript transcript)
    {
        return new DumpDocument(
            "1",
            threadId,
            DeeplinkParser.ToDeeplink(threadId),
            transcript.Messages.Select(message => new DumpMessage(message.TurnId, message.Role, message.Text)).ToArray(),
            transcript.Coverage);
    }
}

internal static class JsonText
{
    // File-based app は JsonSerializer のリフレクションを既定で無効にする。
    // 成果物の形は決まっているので、リフレクションや source generator を使わずに書き出す。
    public static string Serialize(HandoffArtifact artifact) => Write(writer => WriteArtifact(writer, artifact));

    public static string Serialize(DumpDocument document) => Write(writer => WriteDump(writer, document));

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteArtifact(Utf8JsonWriter writer, HandoffArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", artifact.SchemaVersion);
        writer.WritePropertyName("source");
        WriteSource(writer, artifact.Source);
        writer.WritePropertyName("repository");
        if (artifact.Repository is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteRepository(writer, artifact.Repository);
        }

        writer.WritePropertyName("handoff");
        WriteBody(writer, artifact.Handoff);
        writer.WritePropertyName("coverage");
        WriteCoverage(writer, artifact.Coverage);
        writer.WritePropertyName("warnings");
        WriteStrings(writer, artifact.Warnings);
        writer.WriteEndObject();
    }

    private static void WriteDump(Utf8JsonWriter writer, DumpDocument document)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", document.SchemaVersion);
        writer.WriteString("threadId", document.ThreadId);
        writer.WriteString("deeplink", document.Deeplink);
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in document.Messages)
        {
            writer.WriteStartObject();
            writer.WriteString("turnId", message.TurnId);
            writer.WriteString("role", message.Role);
            writer.WriteString("text", message.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("coverage");
        WriteCoverage(writer, document.Coverage);
        writer.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter writer, SourceInfo source)
    {
        writer.WriteStartObject();
        writer.WriteString("deeplink", source.Deeplink);
        writer.WriteString("threadId", source.ThreadId);
        if (source.Title is null)
        {
            writer.WriteNull("title");
        }
        else
        {
            writer.WriteString("title", source.Title);
        }

        writer.WritePropertyName("reread");
        writer.WriteStartObject();
        writer.WriteString("human", source.Reread.Human);
        writer.WriteString("dumpFromDeeplink", source.Reread.DumpFromDeeplink);
        writer.WriteString("dumpFromHandoffJson", source.Reread.DumpFromHandoffJson);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRepository(Utf8JsonWriter writer, RepositorySnapshot repository)
    {
        writer.WriteStartObject();
        writer.WriteString("cwd", repository.Cwd);
        writer.WriteString("branch", repository.Branch);
        writer.WriteString("head", repository.Head);
        writer.WriteBoolean("dirty", repository.Dirty);
        writer.WriteString("statusShort", repository.StatusShort);
        writer.WriteString("diffStat", repository.DiffStat);
        writer.WriteEndObject();
    }

    private static void WriteBody(Utf8JsonWriter writer, HandoffBody body)
    {
        writer.WriteStartObject();
        writer.WriteString("purpose", body.Purpose);
        WriteNamedStrings(writer, "decisions", body.Decisions);
        WriteNamedStrings(writer, "currentState", body.CurrentState);
        WriteNamedStrings(writer, "remainingWork", body.RemainingWork);
        WriteNamedStrings(writer, "rejectedApproaches", body.RejectedApproaches);
        WriteNamedStrings(writer, "openQuestions", body.OpenQuestions);
        WriteNamedStrings(writer, "importantFiles", body.ImportantFiles);
        writer.WriteEndObject();
    }

    private static void WriteCoverage(Utf8JsonWriter writer, CoverageInfo coverage)
    {
        writer.WriteStartObject();
        WriteNamedStrings(writer, "includedItemTypes", coverage.IncludedItemTypes);
        WriteNamedStrings(writer, "excludedItemTypes", coverage.ExcludedItemTypes);
        writer.WriteNumber("turnCount", coverage.TurnCount);
        writer.WriteNumber("includedTurnCount", coverage.IncludedTurnCount);
        writer.WriteBoolean("truncated", coverage.Truncated);
        writer.WriteString("notes", coverage.Notes);
        writer.WriteEndObject();
    }

    private static void WriteNamedStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        WriteStrings(writer, values);
    }

    private static void WriteStrings(Utf8JsonWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}

internal interface IThreadSource
{
    Task<ThreadSnapshot> ReadAsync(string threadId, CancellationToken cancellationToken);
}

internal interface IHandoffModel
{
    Task<HandoffBody> CreateAsync(string model, string apiKey, string prompt, CancellationToken cancellationToken);
}

internal static class HandoffWorkflow
{
    public static async Task<HandoffArtifact> CreateAsync(
        string threadId,
        string outputPath,
        string model,
        string apiKey,
        IThreadSource threads,
        IHandoffModel handoffModel,
        CancellationToken cancellationToken)
    {
        var snapshot = await threads.ReadAsync(threadId, cancellationToken);
        if (!string.Equals(snapshot.ThreadId, threadId, StringComparison.Ordinal))
        {
            throw new HandoffException($"Thread source returned '{snapshot.ThreadId}' for requested thread '{threadId}'.");
        }

        var transcript = TranscriptFilter.Filter(snapshot.Items);
        var repository = await GitSnapshotCollector.CaptureAsync(snapshot.Cwd, cancellationToken);
        var prompt = PromptBuilder.Build(transcript, snapshot.Title, repository);
        var body = await handoffModel.CreateAsync(model, apiKey, prompt, cancellationToken);
        return ArtifactBuilder.Build(snapshot, body, repository, transcript, outputPath);
    }

    public static async Task<DumpDocument> DumpAsync(
        string threadId,
        IThreadSource threads,
        CancellationToken cancellationToken)
    {
        var snapshot = await threads.ReadAsync(threadId, cancellationToken);
        if (!string.Equals(snapshot.ThreadId, threadId, StringComparison.Ordinal))
        {
            throw new HandoffException($"Thread source returned '{snapshot.ThreadId}' for requested thread '{threadId}'.");
        }

        return DumpBuilder.Build(threadId, TranscriptFilter.Filter(snapshot.Items));
    }
}

internal static class HandoffInput
{
    public static string ResolveThreadId(string input)
    {
        if (File.Exists(input))
        {
            return ReadThreadIdFromHandoff(input);
        }

        return DeeplinkParser.ParseThreadId(input);
    }

    public static string ReadThreadIdFromHandoff(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HandoffException($"Failed to read handoff JSON: {ex.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HandoffException($"Handoff JSON is invalid: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("source", out var source) ||
                source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("threadId", out var threadIdElement) ||
                threadIdElement.ValueKind != JsonValueKind.String)
            {
                throw new HandoffException("Handoff JSON does not contain source.threadId.");
            }

            var threadId = threadIdElement.GetString() ?? "";
            if (!Regex.IsMatch(threadId, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
            {
                throw new HandoffException("Handoff JSON source.threadId is invalid.");
            }

            return threadId;
        }
    }
}

/// <summary>
/// Codex App Server の stdio JSON-RPC。履歴は thread/items/list だけで読み、includeTurns にはフォールバックしない。
/// </summary>
internal sealed class LineJsonRpc : IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly TextReader _reader;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Task _readTask;
    private int _nextId = 1;
    private Exception? _loopError;

    public LineJsonRpc(TextWriter writer, TextReader reader)
    {
        _writer = writer;
        _reader = reader;
        _readTask = ReadLoop();
    }

    public async Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var payload = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters,
        };
        await WriteLineAsync(payload.ToJsonString(), cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.Add(id, pending);
        }

        var payload = new JsonObject
        {
            ["method"] = method,
            ["id"] = id,
            ["params"] = parameters,
        };

        try
        {
            await WriteLineAsync(payload.ToJsonString(), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            return await pending.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HandoffException($"App server request timed out: {method}");
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine(json);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new HandoffException($"Failed to write to the app server: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_shutdown.Token);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw new HandoffException($"App server returned a non-JSON line: {ex.Message}");
                }

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var id = idElement.GetInt32();
                    TaskCompletionSource<JsonElement>? pending = null;
                    lock (_gate)
                    {
                        if (_pending.TryGetValue(id, out var found))
                        {
                            pending = found;
                        }
                    }

                    pending?.TrySetResult(document.RootElement.Clone());
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _loopError = ex;
        }
        finally
        {
            FailPending(_loopError ?? new HandoffException("App server closed the connection."));
        }
    }

    private void FailPending(Exception exception)
    {
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_gate)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var source in pending)
        {
            source.TrySetException(exception);
        }
    }

    private void ThrowIfClosed()
    {
        if (_loopError is not null)
        {
            throw new HandoffException(_loopError.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        _shutdown.Dispose();
    }
}

internal static class AppServerThreadReader
{
    public static async Task<ThreadSnapshot> ReadAsync(LineJsonRpc rpc, string threadId, CancellationToken cancellationToken)
    {
        var initialize = await rpc.RequestAsync(
            "initialize",
            new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "codex_thread_handoff",
                    ["title"] = "Codex Thread Handoff",
                    ["version"] = "1.0.0",
                },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = true,
                },
            },
            cancellationToken);
        RequireResult(initialize, "initialize");
        await rpc.NotifyAsync("initialized", new JsonObject(), cancellationToken);

        var read = await rpc.RequestAsync(
            "thread/read",
            new JsonObject { ["threadId"] = threadId },
            cancellationToken);
        var thread = RequireResult(read, "thread/read");
        if (!thread.TryGetProperty("thread", out var threadElement) || threadElement.ValueKind != JsonValueKind.Object)
        {
            throw new HandoffException("App server thread/read result did not contain a thread object.");
        }

        if (!threadElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            throw new HandoffException("App server thread/read result did not contain a thread id.");
        }

        var returnedId = idElement.GetString() ?? "";
        if (!string.Equals(returnedId, threadId, StringComparison.Ordinal))
        {
            throw new HandoffException($"App server returned thread '{returnedId}' for requested thread '{threadId}'.");
        }

        string? title = null;
        if (threadElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            title = nameElement.GetString();
        }

        string? cwd = null;
        if (threadElement.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String)
        {
            cwd = cwdElement.GetString();
        }

        var items = new List<StoredItem>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 2000; page++)
        {
            var parameters = new JsonObject
            {
                ["threadId"] = threadId,
                ["limit"] = 100,
                ["sortDirection"] = "asc",
            };
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var listed = await rpc.RequestAsync("thread/items/list", parameters, cancellationToken);
            var result = RequireResult(listed, "thread/items/list");
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new HandoffException("App server thread/items/list result did not contain data.");
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("turnId", out var turnIdElement) ||
                    turnIdElement.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("item", out var itemElement) ||
                    itemElement.ValueKind != JsonValueKind.Object)
                {
                    throw new HandoffException("App server thread/items/list returned an invalid item.");
                }

                items.Add(new StoredItem(turnIdElement.GetString() ?? "", itemElement.GetRawText()));
            }

            if (!result.TryGetProperty("nextCursor", out var nextCursor) || nextCursor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                cursor = null;
            }
            else if (nextCursor.ValueKind == JsonValueKind.String)
            {
                cursor = nextCursor.GetString();
            }
            else
            {
                throw new HandoffException("App server thread/items/list returned an invalid cursor.");
            }

            if (string.IsNullOrEmpty(cursor))
            {
                return new ThreadSnapshot(threadId, title, cwd, items);
            }

            if (!seenCursors.Add(cursor))
            {
                throw new HandoffException("App server thread/items/list repeated a cursor.");
            }
        }

        throw new HandoffException("App server thread/items/list did not finish within the page limit.");
    }

    private static JsonElement RequireResult(JsonElement response, string method)
    {
        if (response.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : "Unknown app server error.";
            throw new HandoffException($"App server {method} failed: {message}");
        }

        if (!response.TryGetProperty("result", out var result))
        {
            throw new HandoffException($"App server {method} returned neither result nor error.");
        }

        return result;
    }
}

internal sealed class CodexAppServerThreadSource : IThreadSource
{
    // Encoding.UTF8 は BOM を出す。App Server の先頭バイトが JSON でなくなるため、BOM なしを使う。
    internal static readonly Encoding JsonLineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ThreadSnapshot> ReadAsync(string threadId, CancellationToken cancellationToken)
    {
        var jsPath = FindCodexJs();
        var start = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = JsonLineEncoding,
            StandardOutputEncoding = JsonLineEncoding,
            StandardErrorEncoding = JsonLineEncoding,
        };
        start.ArgumentList.Add(jsPath);
        start.ArgumentList.Add("app-server");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new HandoffException("Failed to start codex app-server.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new HandoffException($"Failed to start codex app-server: {ex.Message}");
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (stderr)
                {
                    if (stderr.Length < 4_000)
                    {
                        stderr.AppendLine(args.Data);
                    }
                }
            }
        };
        process.BeginErrorReadLine();
        process.StandardInput.AutoFlush = true;
        await using var rpc = new LineJsonRpc(process.StandardInput, process.StandardOutput);
        try
        {
            return await AppServerThreadReader.ReadAsync(rpc, threadId, cancellationToken);
        }
        catch (HandoffException ex)
        {
            string stderrText;
            lock (stderr)
            {
                stderrText = stderr.ToString().Trim();
            }

            if (stderrText.Length > 0)
            {
                throw new HandoffException($"{ex.Message}{Environment.NewLine}{stderrText}");
            }

            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    private static string FindCodexJs()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var js = Path.Combine(directory.Trim(), "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(js))
            {
                return js;
            }
        }

        throw new HandoffException("Codex CLI was not found on PATH.");
    }
}

internal sealed class OpenAiHandoffModel : IHandoffModel
{
    public async Task<HandoffBody> CreateAsync(
        string model,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
#pragma warning disable OPENAI001
        var client = new ResponsesClient(new ApiKeyCredential(apiKey));
        var options = new CreateResponseOptions
        {
            Model = model,
            Instructions = "Return only the handoff JSON object described by the schema.",
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "handoff_body",
                    jsonSchema: BinaryData.FromString(HandoffBodyParser.SchemaJson),
                    jsonSchemaFormatDescription: "Lossy coding-agent handoff body.",
                    jsonSchemaIsStrict: true),
            },
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
        ClientResult<ResponseResult> result = await client.CreateResponseAsync(options, cancellationToken);
        var text = result.Value.GetOutputText();
#pragma warning restore OPENAI001
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new HandoffException("OpenAI response did not contain output text.");
        }

        return HandoffBodyParser.Parse(text);
    }
}

internal sealed record CliRequest(bool Dump, string Input, string? OutputPath, string? Model);

internal static class CliParser
{
    public static CliRequest Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        string? model = null;
        var dump = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-o")
            {
                output = ReadValue(args, ref i, "-o");
                continue;
            }

            if (arg == "--model")
            {
                model = ReadValue(args, ref i, "--model");
                continue;
            }

            if (arg.StartsWith('-'))
            {
                throw new HandoffException($"Unknown argument '{arg}'.");
            }

            if (input is null && arg == "dump")
            {
                dump = true;
                continue;
            }

            if (input is not null)
            {
                throw new HandoffException("Too many arguments.");
            }

            input = arg;
        }

        if (input is null)
        {
            throw new HandoffException("Usage: codex-thread-handoff <deeplink> [-o path] [--model id] | codex-thread-handoff dump <deeplink|handoff.json>");
        }

        if (dump && (output is not null || model is not null))
        {
            throw new HandoffException("dump does not accept -o or --model.");
        }

        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new HandoffException("--model requires a model id.");
        }

        return new CliRequest(dump, input, output, model);
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            throw new HandoffException($"{name} requires a value.");
        }

        index++;
        return args[index];
    }
}

internal static class Cli
{
    public const string DefaultModel = "gpt-6-luna";

    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, Environment.GetEnvironmentVariable, new CodexAppServerThreadSource(), new OpenAiHandoffModel(), Console.Out, Console.Error, CancellationToken.None);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> readVariable,
        IThreadSource threads,
        IHandoffModel model,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = CliParser.Parse(args);
            if (request.Dump)
            {
                var threadId = HandoffInput.ResolveThreadId(request.Input);
                var dump = await HandoffWorkflow.DumpAsync(threadId, threads, cancellationToken);
                await stdout.WriteLineAsync(JsonText.Serialize(dump));
                return 0;
            }

            var apiKey = ApiKeyResolver.Resolve(readVariable);
            var generateThreadId = DeeplinkParser.ParseThreadId(request.Input);
            var outputPath = request.OutputPath ?? Path.Combine(Directory.GetCurrentDirectory(), $"handoff-{generateThreadId}.json");
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                throw new HandoffException($"Output directory does not exist: {outputDirectory}");
            }

            var artifact = await HandoffWorkflow.CreateAsync(
                generateThreadId,
                outputPath,
                request.Model ?? DefaultModel,
                apiKey,
                threads,
                model,
                cancellationToken);
            await File.WriteAllTextAsync(outputPath, JsonText.Serialize(artifact), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            foreach (var warning in artifact.Warnings)
            {
                await stderr.WriteLineAsync($"warning: {warning}");
            }

            await stdout.WriteLineAsync($"Wrote {outputPath}");
            return 0;
        }
        catch (HandoffException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync(ex.ToString());
            return 1;
        }
    }
}
