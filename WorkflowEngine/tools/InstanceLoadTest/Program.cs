using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

var options = ParseArgs(args);
Console.WriteLine("Workflow Engine instance load test");
Console.WriteLine($"  API:         {options.ApiBase}");
Console.WriteLine($"  Count:       {options.Count:N0}");
Console.WriteLine($"  Concurrency: {options.Concurrency}");
Console.WriteLine($"  WorkflowId:  {(options.WorkflowId?.ToString() ?? "(auto)")}");
Console.WriteLine();

var token = CreateToken(options);
using var http = new HttpClient
{
    BaseAddress = new Uri(options.ApiBase.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromMinutes(5)
};
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

var workflowId = options.WorkflowId ?? await ResolveWorkflowIdAsync(http, cancellationToken: default);
Console.WriteLine($"Using workflow id {workflowId}");

var progress = new ProgressTracker(options.Count);
var gate = new SemaphoreSlim(options.Concurrency);
var errors = new List<string>();
var errorLock = new object();
var stopwatch = Stopwatch.StartNew();

var tasks = Enumerable.Range(0, options.Count)
    .Select(async _ =>
    {
        await gate.WaitAsync();
        try
        {
            var amount = Random.Shared.Next(100, 1001);
            var bodyJson = JsonSerializer.Serialize(new StartInstancePayload(
                workflowId,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["amount"] = JsonSerializer.SerializeToElement(amount)
                }));
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("api/instances", content);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                RecordError($"HTTP {(int)response.StatusCode}: {Truncate(detail, 200)}");
            }
            else
            {
                progress.IncrementSuccess();
            }
        }
        catch (Exception ex)
        {
            RecordError(ex.Message);
        }
        finally
        {
            gate.Release();
        }
    })
    .ToArray();

await Task.WhenAll(tasks);
stopwatch.Stop();

progress.Finish();
Console.WriteLine();
Console.WriteLine($"Done in {stopwatch.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine($"  Succeeded: {progress.Succeeded:N0}");
Console.WriteLine($"  Failed:    {progress.Failed:N0}");
if (progress.Succeeded > 0)
{
    var rate = progress.Succeeded / stopwatch.Elapsed.TotalSeconds;
    Console.WriteLine($"  Throughput: {rate:N1} instances/sec");
}

if (errors.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Sample errors:");
    foreach (var error in errors.Take(10))
    {
        Console.WriteLine($"  - {error}");
    }

    if (errors.Count > 10)
    {
        Console.WriteLine($"  ... and {errors.Count - 10} more");
    }

    Environment.ExitCode = 1;
}

void RecordError(string message)
{
    progress.IncrementFailure();
    lock (errorLock)
    {
        if (errors.Count < 100)
        {
            errors.Add(message);
        }
    }
}

static Options ParseArgs(string[] args)
{
    var result = new Options();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--api" when i + 1 < args.Length:
                result = result with { ApiBase = args[++i] };
                break;
            case "--count" when i + 1 < args.Length && int.TryParse(args[++i], out var count):
                result = result with { Count = Math.Max(1, count) };
                break;
            case "--concurrency" when i + 1 < args.Length && int.TryParse(args[++i], out var concurrency):
                result = result with { Concurrency = Math.Clamp(concurrency, 1, 512) };
                break;
            case "--workflow-id" when i + 1 < args.Length && long.TryParse(args[++i], out var workflowId):
                result = result with { WorkflowId = workflowId };
                break;
            case "--jwt-key" when i + 1 < args.Length:
                result = result with { JwtKey = args[++i] };
                break;
            case "--help":
            case "-h":
                PrintHelp();
                Environment.Exit(0);
                break;
        }
    }

    return result;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Usage: dotnet run --project WorkflowEngine/tools/InstanceLoadTest -- [options]

        Options:
          --api <url>            API base URL (default: http://localhost:5017)
          --count <n>            Number of instances to create (default: 200000)
          --concurrency <n>      Parallel HTTP requests (default: 32)
          --workflow-id <id>     Published workflow id (default: first published)
          --jwt-key <key>        JWT signing key (default: dev appsettings key)
          -h, --help             Show this help

        Each start uses a random 'amount' from 100 to 1000.

        Example:
          # Start the API without per-instance Information logging:
          dotnet run --no-launch-profile --project WorkflowEngine/src/WorkflowEngine.Api -- --environment LoadTest --urls http://localhost:5017

          # Warm up, then run the measured workload:
          dotnet run --project WorkflowEngine/tools/InstanceLoadTest -- --count 1000 --concurrency 48
          dotnet run --project WorkflowEngine/tools/InstanceLoadTest -- --count 200000 --concurrency 48
        """);
}

static string CreateToken(Options options)
{
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtKey)),
        SecurityAlgorithms.HmacSha256);

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, "load-test"),
        new Claim(JwtRegisteredClaimNames.Sub, "load-test"),
        new Claim(ClaimTypes.Role, "Requester"),
        new Claim(ClaimTypes.Role, "Manager")
    };

    var token = new JwtSecurityToken(
        issuer: options.Issuer,
        audience: options.Audience,
        claims: claims,
        notBefore: DateTime.UtcNow,
        expires: DateTime.UtcNow.AddHours(24),
        signingCredentials: credentials);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

static async Task<long> ResolveWorkflowIdAsync(HttpClient http, CancellationToken cancellationToken)
{
    var workflows = await http.GetFromJsonAsync<List<WorkflowSummary>>("api/workflows", cancellationToken)
        ?? throw new InvalidOperationException("GET /api/workflows returned no data.");

    var published = workflows.FirstOrDefault(w => w.IsPublished)
        ?? throw new InvalidOperationException("No published workflow found. Seed or publish one first.");

    return published.Id;
}

static string Truncate(string value, int max) =>
    value.Length <= max ? value : value[..max] + "...";

sealed record Options(
    string ApiBase = Defaults.ApiBase,
    int Count = 200_000,
    int Concurrency = 32,
    long? WorkflowId = null,
    string JwtKey = Defaults.JwtKey,
    string Issuer = Defaults.Issuer,
    string Audience = Defaults.Audience);

sealed record StartInstancePayload(
    long WorkflowId,
    int? StartEventId,
    Dictionary<string, JsonElement>? Variables);

sealed record WorkflowSummary(
    long Id,
    string Name,
    int Version,
    bool IsPublished,
    DateTimeOffset CreatedAt);

sealed class ProgressTracker(int total)
{
    private int _succeeded;
    private int _failed;
    private int _lastReportedDone;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Stopwatch _reportTimer = Stopwatch.StartNew();

    public int Succeeded => Volatile.Read(ref _succeeded);
    public int Failed => Volatile.Read(ref _failed);

    public void IncrementSuccess()
    {
        var done = Interlocked.Increment(ref _succeeded);
        MaybeReport(done);
    }

    public void IncrementFailure()
    {
        Interlocked.Increment(ref _failed);
        var done = Succeeded + Failed;
        MaybeReport(done);
    }

    private void MaybeReport(int done)
    {
        if (_reportTimer.Elapsed < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lock (_reportTimer)
        {
            if (_reportTimer.Elapsed < TimeSpan.FromSeconds(2))
            {
                return;
            }

            var intervalSec = _reportTimer.Elapsed.TotalSeconds;
            var elapsed = _elapsed.Elapsed;
            var delta = done - _lastReportedDone;
            var instantRate = intervalSec > 0 ? delta / intervalSec : 0;
            var avgRate = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
            var remaining = Math.Max(0, total - done);
            var eta = avgRate > 0
                ? TimeSpan.FromSeconds(remaining / avgRate)
                : Timeout.InfiniteTimeSpan;

            var etaText = eta == Timeout.InfiniteTimeSpan
                ? "ETA --:--:--"
                : $"ETA {eta:hh\\:mm\\:ss}";

            Console.WriteLine(
                $"  {done:N0}/{total:N0} ({100.0 * done / total:F1}%) | " +
                $"{instantRate:N1}/sec now, {avgRate:N1}/sec avg | " +
                $"elapsed {elapsed:hh\\:mm\\:ss}, {etaText} | " +
                $"failed {Failed:N0}");

            _lastReportedDone = done;
            _reportTimer.Restart();
        }
    }

    public void Finish()
    {
        var done = Succeeded + Failed;
        var elapsed = _elapsed.Elapsed;
        var avgRate = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
        Console.WriteLine(
            $"  {done:N0}/{total:N0} (100%) | {avgRate:N1}/sec avg | " +
            $"elapsed {elapsed:hh\\:mm\\:ss} | failed {Failed:N0}");
    }
}

static class Defaults
{
    public const string ApiBase = "http://localhost:5017";
    public const string JwtKey = "dev-only-symmetric-signing-key-change-me-please-32+";
    public const string Issuer = "workflow-engine-dev";
    public const string Audience = "workflow-engine-api";
}
