using System.Text;
using System.Text.Json;

namespace ObsidianX.Core.Services;

/// <summary>
/// Orchestrates the Co-Pilot Arena flow:
///
///     user spec
///        │
///        ▼
///     Intern (local LLM, e.g. Ollama qwen2.5:7b)
///        │  refines into structured plan: spec / files / acceptance
///        ▼
///     Worker (CluadeX via named pipe)
///        │  runs the 48-tool agent loop, returns final reply
///        ▼
///     TaskRunResult
///
/// Why a delegate planner (instead of a typed AiHubService dependency):
/// the WPF client lives in a separate process from the AiHubService
/// (which runs inside ObsidianX.Server). The client reaches the hub via
/// HTTP at <c>/api/ai/chat</c>. By accepting the planner as a
/// <c>Func&lt;string, CancellationToken, Task&lt;InternPlan&gt;&gt;</c>
/// we keep this orchestrator transport-agnostic — it can be wired to
/// HTTP, in-proc, gRPC, or a stub for tests, without recompiling Core.
///
/// Why <see cref="IProgress{T}"/>: the WPF UI subscribes once and
/// receives bubble-shaped events on the dispatcher thread. Decouples
/// the orchestrator's threading from how the UI renders updates, and
/// gives a clean seam for headless / CLI consumers.
/// </summary>
public sealed class CoPilotOrchestrator
{
    /// <summary>Local-model planner. Takes the user's raw spec, returns
    /// a parsed <see cref="InternPlan"/>. Implementations should call
    /// <see cref="InternPlan.ParseFromLlm"/> to handle imperfect JSON
    /// gracefully — small models can't always close their braces.</summary>
    public Func<string, CancellationToken, Task<InternPlan>>? Planner { get; init; }

    /// <summary>CluadeX worker client. Required.</summary>
    public CluadeXClient? Worker { get; init; }

    public OrchestrationOptions Options { get; init; } = new();

    /// <summary>Run one orchestrated task end-to-end. Always returns a
    /// <see cref="TaskRunResult"/> — failures are reported via
    /// <see cref="TaskRunStatus.Failed"/> / <see cref="TaskRunStatus.TimedOut"/>
    /// rather than thrown, so the UI can render them in the same bubble
    /// flow as success.</summary>
    public async Task<TaskRunResult> RunAsync(
        string userSpec,
        string? workingDirectory,
        IProgress<OrchestrationEvent>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSpec))
            throw new ArgumentException("userSpec cannot be empty", nameof(userSpec));
        if (Planner is null) throw new InvalidOperationException("Planner not configured");
        if (Worker is null) throw new InvalidOperationException("Worker not configured");

        // InvariantCulture so the Thai calendar (พ.ศ.) doesn't turn 2026
        // into "69". Also use UtcNow so two machines collaborating on the
        // same orchestration won't end up with conflicting ids in mixed
        // timezones — the id is opaque to the user, only timestamps in
        // the bubble headers are localised for display.
        string taskId = "task-" + DateTime.UtcNow.ToString(
            "yyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var started = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(new TaskCreated(DateTime.UtcNow, taskId, userSpec));

        // Hard wall-clock budget. Linked CTS so callers can still cancel
        // manually; we layer the budget on top.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(Options.MaxWallClock);
        var token = budgetCts.Token;

        // ─── Phase 1: intern plans ────────────────────────────────────
        InternPlan plan;
        try
        {
            progress?.Report(new InternStarted(DateTime.UtcNow));
            plan = await Planner(userSpec, token);
            progress?.Report(new InternFinished(DateTime.UtcNow, plan));
        }
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "intern", "wall-clock budget exhausted during planning"));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.TimedOut));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.TimedOut, Started = started, Ended = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "intern", ex.Message));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Error = ex.Message };
        }

        // ─── Phase 2: worker codes ────────────────────────────────────
        // Compose the worker spec: intern's refined spec + acceptance
        // bullets. Lessons stays null until Phase 1C wires brain-derived
        // self-improvement principles into the call.
        string workerSpec = BuildWorkerSpec(plan);
        string workerOutput;
        try
        {
            progress?.Report(new WorkerStarted(DateTime.UtcNow, taskId));
            int remainingMs = Math.Max(5_000, (int)Math.Min(int.MaxValue, (Options.MaxWallClock - sw.Elapsed).TotalMilliseconds));
            workerOutput = await Worker.WriteCodeAsync(
                taskId,
                workerSpec,
                lessons: null,
                contextFiles: plan.Files,
                workingDirectory: workingDirectory,
                timeoutMs: remainingMs,
                ct: token);
            progress?.Report(new WorkerFinished(DateTime.UtcNow, workerOutput));
        }
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "worker", "wall-clock budget exhausted during coding"));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.TimedOut));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.TimedOut, Started = started, Ended = DateTime.UtcNow, Plan = plan };
        }
        catch (Exception ex)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "worker", ex.Message));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, Error = ex.Message };
        }

        sw.Stop();
        progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Done));
        return new TaskRunResult
        {
            TaskId = taskId,
            Status = TaskRunStatus.Done,
            Started = started,
            Ended = DateTime.UtcNow,
            Elapsed = sw.Elapsed,
            Plan = plan,
            WorkerOutput = workerOutput,
        };
    }

    /// <summary>
    /// Standard prompt the planner delegate should send to the local
    /// model. Exposed so callers can adapt the wrapping (system vs user
    /// role, history shape) for the transport they're using.
    /// </summary>
    public static string BuildPlannerPrompt(string userSpec) =>
        $$"""
        You are an intern engineer who breaks a coding request down for a senior coder. Output ONLY a JSON object — no prose, no markdown fences — with these fields:

          {
            "spec": "<one to three sentences refining the request for the senior>",
            "files": ["<repo-relative paths or globs the senior should likely touch>"],
            "acceptance": ["<bulleted criteria for done — 1 to 4 items>"],
            "rationale": "<why this approach, one sentence>"
          }

        If the request is too vague to plan, set "spec" to a clarifying question and leave the arrays empty.

        REQUEST:
        {{userSpec}}
        """;

    private static string BuildWorkerSpec(InternPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(plan.Spec);
        if (plan.Acceptance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Acceptance criteria:");
            foreach (var a in plan.Acceptance) sb.AppendLine("- " + a);
        }
        if (!string.IsNullOrWhiteSpace(plan.Rationale))
        {
            sb.AppendLine();
            sb.Append("Rationale from intern: ").Append(plan.Rationale);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Structured plan produced by the local-LLM intern. <see cref="ParseFromLlm"/>
/// is intentionally permissive — small models often emit broken JSON or
/// wrap output in code fences. The fallback path treats free-form text
/// as a single-spec plan so the worker still receives a sensible
/// delegation rather than failing the whole run.
/// </summary>
public sealed record InternPlan(
    string Spec,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Acceptance,
    string Rationale)
{
    /// <summary>Rendered for a human-facing UI bubble. Keep markdown-ish
    /// so a Markdown viewer can format it; consumers that show plain
    /// text will still get a readable layout because of the bullet
    /// glyphs.</summary>
    public string ToDisplay()
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Spec:** " + Spec);
        if (Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Files:** " + string.Join(", ", Files));
        }
        if (Acceptance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Acceptance:**");
            foreach (var a in Acceptance) sb.AppendLine("• " + a);
        }
        if (!string.IsNullOrWhiteSpace(Rationale))
        {
            sb.AppendLine();
            sb.AppendLine("**Why:** " + Rationale);
        }
        return sb.ToString().TrimEnd();
    }

    public static InternPlan ParseFromLlm(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new InternPlan("(empty plan from intern)", Array.Empty<string>(), Array.Empty<string>(), "");

        // Strip code fences like ```json … ``` if present.
        var trimmed = raw.Trim();
        var firstFence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (firstFence >= 0)
        {
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > firstFence)
            {
                var inside = trimmed.Substring(firstFence + 3, lastFence - firstFence - 3);
                // Drop a single language tag on the first line, e.g. "json\n".
                var nl = inside.IndexOf('\n');
                if (nl >= 0 && nl < 14 && inside[..nl].Trim().All(char.IsLetter))
                    inside = inside[(nl + 1)..];
                trimmed = inside.Trim();
            }
        }

        // Find the first JSON object. Models sometimes prepend "Here's the
        // plan:" before the {.
        int openBrace = trimmed.IndexOf('{');
        int closeBrace = trimmed.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return new InternPlan(
                Spec: raw.Trim(),
                Files: Array.Empty<string>(),
                Acceptance: Array.Empty<string>(),
                Rationale: "(intern emitted free-form text, not JSON — passing as-is)");
        }
        var json = trimmed.Substring(openBrace, closeBrace - openBrace + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string spec = root.TryGetProperty("spec", out var s) ? (s.GetString() ?? "") : "";
            string why = root.TryGetProperty("rationale", out var r) ? (r.GetString() ?? "") : "";

            var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : "")
                    .Where(x => x.Length > 0)
                    .ToArray()
                : Array.Empty<string>();
            var acc = root.TryGetProperty("acceptance", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : "")
                    .Where(x => x.Length > 0)
                    .ToArray()
                : Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(spec)) spec = raw.Trim();
            return new InternPlan(spec, files, acc, why);
        }
        catch
        {
            return new InternPlan(
                Spec: raw.Trim(),
                Files: Array.Empty<string>(),
                Acceptance: Array.Empty<string>(),
                Rationale: "(intern emitted invalid JSON — passing raw as spec)");
        }
    }
}

public sealed class OrchestrationOptions
{
    /// <summary>Per-task ceiling on the worker's iteration count.
    /// Forwarded to CluadeX (which has its own 15-iter cap; this is a
    /// belt-and-braces upper bound).</summary>
    public int MaxTurnsPerTask { get; set; } = 15;

    /// <summary>Hard end-to-end deadline (intern + worker combined).</summary>
    public TimeSpan MaxWallClock { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Informational only until Anthropic billing is wired in;
    /// surfaces in the UI gauge so the user has an at-a-glance budget
    /// awareness for cloud-provider runs.</summary>
    public decimal MaxUsdPerTask { get; set; } = 0.20m;

    /// <summary>How many times the reviewer (Phase 1C) may send the
    /// worker back for revisions before escalating to the user.</summary>
    public int MaxReviseRounds { get; set; } = 3;
}

public enum TaskRunStatus
{
    Planning,
    Working,
    Done,
    Failed,
    TimedOut,
}

public sealed class TaskRunResult
{
    public string TaskId { get; set; } = "";
    public TaskRunStatus Status { get; set; }
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public TimeSpan Elapsed { get; set; }
    public InternPlan? Plan { get; set; }
    public string? WorkerOutput { get; set; }
    public string? Error { get; set; }
}

// ─── Events ──────────────────────────────────────────────────────────
// Discriminated by record type so consumers can pattern-match. Each
// carries the timestamp it was emitted at — the UI uses this to render
// the bubble's footer ("0:23 ago") without needing a clock of its own.

public abstract record OrchestrationEvent(DateTime Ts);
public sealed record TaskCreated(DateTime Ts, string TaskId, string UserSpec) : OrchestrationEvent(Ts);
public sealed record InternStarted(DateTime Ts) : OrchestrationEvent(Ts);
public sealed record InternFinished(DateTime Ts, InternPlan Plan) : OrchestrationEvent(Ts);
public sealed record WorkerStarted(DateTime Ts, string TaskId) : OrchestrationEvent(Ts);
public sealed record WorkerFinished(DateTime Ts, string Output) : OrchestrationEvent(Ts);
public sealed record OrchestratorError(DateTime Ts, string Phase, string Message) : OrchestrationEvent(Ts);
public sealed record OrchestratorFinished(DateTime Ts, TaskRunStatus Status) : OrchestrationEvent(Ts);
