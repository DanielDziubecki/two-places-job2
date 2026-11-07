using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Job 2 worker for the MEGA.dev "two places" challenge.
// It connects to the job's MCP link, follows the voice-to-memo skill with a
// Claude tool-use loop, and keeps the result (notes + receipt) for the main
// agent to fetch from GET /result.

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton<Job2Runner>();
var app = builder.Build();

string Secret() => Environment.GetEnvironmentVariable("RESULT_SECRET") ?? "";

bool Authorized(HttpRequest req)
{
    var s = Secret();
    if (s.Length < 16) return false; // refuse to serve anything without a real secret
    var given = req.Headers["X-Result-Secret"].ToString();
    return CryptographicEquals(given, s);
}

static bool CryptographicEquals(string a, string b) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapPost("/run", (HttpRequest req, Job2Runner runner) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    var force = req.Query["force"] == "1";
    var started = runner.TryStart(force);
    return Results.Json(new { started, state = runner.State.Status }, statusCode: started ? 202 : 200);
});

app.MapGet("/result", (HttpRequest req, Job2Runner runner) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    return Results.Json(runner.State);
});

if (Environment.GetEnvironmentVariable("RUN_ON_START") == "1")
    app.Services.GetRequiredService<Job2Runner>().TryStart(false);

app.Run();

public sealed class RunState
{
    public string Status { get; set; } = "idle"; // idle | running | done | failed
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Model { get; set; }
    public string? SkillSource { get; set; }
    public string? Error { get; set; }
    public string? SubmitResult { get; set; }   // raw text the job's submit returned (holds the receipt)
    public JsonNode? SubmitStructured { get; set; }
    public string? SubmittedNotes { get; set; }  // the notes text handed to submit
    public string? FinalMessage { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public List<TraceEntry> Trace { get; set; } = new();
}

public sealed record TraceEntry(int Turn, string Kind, string Name, string Input, string Output, long Ms);

public sealed class Job2Runner
{
    private readonly object _gate = new();
    private readonly ILogger<Job2Runner> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public RunState State { get; private set; } = new();

    public Job2Runner(ILogger<Job2Runner> log) => _log = log;

    public bool TryStart(bool force)
    {
        lock (_gate)
        {
            if (State.Status == "running") return false;
            if (State.Status == "done" && !force) return false;
            State = new RunState { Status = "running", StartedAt = DateTimeOffset.UtcNow };
        }
        _ = Task.Run(RunAsync);
        return true;
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing environment variable {name}");

    private static string Clip(string s, int max = 4000) => s.Length <= max ? s : s[..max] + "…[truncated]";

    private async Task RunAsync()
    {
        var st = State;
        try
        {
            var jobUrl = Env("JOB_MCP_URL");
            var clipUrl = Env("CLIP_URL");
            var apiKey = Env("ANTHROPIC_API_KEY");
            var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-5-5";
            st.Model = model;

            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(jobUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "job2",
            }));

            // Skill: prefer the copy served on the job's own link, fall back to the bundled folder.
            string skill;
            try
            {
                var res = await mcp.ReadResourceAsync("skill://voice-to-memo/SKILL.md");
                skill = string.Join("\n", res.Contents.OfType<TextResourceContents>().Select(c => c.Text));
                st.SkillSource = "mcp resource skill://voice-to-memo/SKILL.md";
                if (string.IsNullOrWhiteSpace(skill)) throw new InvalidOperationException("empty skill resource");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skill resource not readable, using bundled copy");
                skill = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "skills/voice-to-memo/SKILL.md"));
                st.SkillSource = "bundled skills/voice-to-memo/SKILL.md";
            }

            // Guardrail: the model sees only this job's tools.
            var mcpTools = await mcp.ListToolsAsync();
            var tools = new JsonArray();
            foreach (var t in mcpTools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description ?? "",
                    ["input_schema"] = JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText()),
                });
            }

            var system =
                "You are a worker agent. Do exactly one job by following the skill below, using only the tools given. " +
                "Pass the step token each tool returns into the next call as the tool's schema asks. " +
                "Do not invent findings or offsets: base every note on the transcript. " +
                "When submit has returned, reply with a short final message that repeats submit's reply verbatim.\n\n" +
                $"Clip URL for this job: {clipUrl}\n\n--- SKILL ---\n{skill}";

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Run job 2 now, following the skill from step 1 to submit." },
            };

            const int maxTurns = 40;
            for (var turn = 1; turn <= maxTurns; turn++)
            {
                var sw = Stopwatch.StartNew();
                var resp = await CallClaudeAsync(apiKey, model, system, tools, messages);
                sw.Stop();
                st.InputTokens += resp["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
                st.OutputTokens += resp["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;

                var content = resp["content"]!.AsArray();
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                var text = string.Join("\n", content.Where(c => c?["type"]?.GetValue<string>() == "text")
                                                    .Select(c => c!["text"]!.GetValue<string>()));
                st.Trace.Add(new TraceEntry(turn, "generation", model, "", Clip(text, 1500), sw.ElapsedMilliseconds));

                var toolUses = content.Where(c => c?["type"]?.GetValue<string>() == "tool_use").ToList();
                if (toolUses.Count == 0)
                {
                    st.FinalMessage = text;
                    break;
                }

                var results = new JsonArray();
                foreach (var tu in toolUses)
                {
                    var name = tu!["name"]!.GetValue<string>();
                    var id = tu["id"]!.GetValue<string>();
                    var inputJson = tu["input"]?.ToJsonString() ?? "{}";
                    var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputJson) ?? new();

                    var tsw = Stopwatch.StartNew();
                    string output;
                    bool isError;
                    try
                    {
                        var r = await mcp.CallToolAsync(name, args);
                        var parts = r.Content.OfType<TextContentBlock>().Select(b => b.Text).ToList();
                        if (r.StructuredContent is { } sc) parts.Add(sc.ToString());
                        output = string.Join("\n", parts);
                        isError = r.IsError == true;
                        if (name == "submit" && !isError)
                        {
                            st.SubmitResult = output;
                            st.SubmitStructured = r.StructuredContent is { } s2 ? JsonNode.Parse(s2.ToString()) : null;
                            st.SubmittedNotes = tu["input"]?["notes"]?.ToString() ?? inputJson;
                        }
                    }
                    catch (Exception ex)
                    {
                        output = $"Tool call failed: {ex.Message}";
                        isError = true;
                    }
                    tsw.Stop();
                    st.Trace.Add(new TraceEntry(turn, "tool", name, Clip(inputJson), Clip(output), tsw.ElapsedMilliseconds));
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"] = output,
                        ["is_error"] = isError,
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            }

            st.Status = st.SubmitResult is null ? "failed" : "done";
            if (st.SubmitResult is null) st.Error ??= "Agent stopped without a successful submit.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job 2 run failed");
            st.Status = "failed";
            st.Error = ex.Message;
        }
        finally
        {
            st.FinishedAt = DateTimeOffset.UtcNow;
            _log.LogInformation("Job 2 finished: {Status}, tokens in/out {In}/{Out}", st.Status, st.InputTokens, st.OutputTokens);
        }
    }

    private static async Task<JsonNode> CallClaudeAsync(string apiKey, string model, string system, JsonArray tools, JsonArray messages)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8000,
            ["system"] = system,
            ["tools"] = tools.DeepClone(),
            ["messages"] = messages.DeepClone(),
        };
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JsonNode.Parse(raw)!;
            var retryable = (int)resp.StatusCode is 429 or 500 or 502 or 503 or 529;
            if (!retryable || attempt >= 4)
                throw new InvalidOperationException($"Claude API {(int)resp.StatusCode}: {Clip(raw, 500)}");
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }
    }
}
