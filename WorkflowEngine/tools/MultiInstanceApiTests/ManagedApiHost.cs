using System.Diagnostics;

sealed class ManagedApiHost : IAsyncDisposable
{
    private readonly Options _options;
    private readonly string _projectPath;
    private readonly string _workingDirectory;
    private readonly string _logPath;
    private readonly object _logLock = new();
    private Process? _process;

    public ManagedApiHost(Options options)
    {
        _options = options;
        _projectPath = Path.GetFullPath(options.ApiProject
            ?? Path.Combine(options.FixtureRoot!, "WorkflowEngine", "src", "WorkflowEngine.Api", "WorkflowEngine.Api.csproj"));
        if (!File.Exists(_projectPath))
            throw new FileNotFoundException("Managed API project was not found.", _projectPath);
        _workingDirectory = Directory.GetParent(Directory.GetParent(Directory.GetParent(_projectPath)!.FullName)!.FullName)!.FullName;
        _logPath = Path.Combine(options.ReportDirectory!, $"multi-instance-api-{options.RunId}-server.log");
    }

    public async Task StartAsync()
    {
        if (_process is { HasExited: false }) return;
        AppendLog($"{Environment.NewLine}=== API start {DateTimeOffset.UtcNow:O} ===");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-launch-profile");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(_projectPath);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--environment");
        start.ArgumentList.Add("Development");
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(_options.ApiBase);

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) AppendLog(eventArgs.Data);
        };
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) AppendLog(eventArgs.Data);
        };
        if (!_process.Start()) throw new InvalidOperationException("Failed to start WorkflowEngine.Api.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitUntilReadyAsync();
    }

    public async Task RestartAsync()
    {
        AppendLog($"=== API restart requested {DateTimeOffset.UtcNow:O} ===");
        await StopAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            AppendLog($"=== API stopped {DateTimeOffset.UtcNow:O} (exit {process.ExitCode}) ===");
            process.Dispose();
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        using var readiness = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"WorkflowEngine.Api exited during startup with code {_process.ExitCode}. See {_logPath}.");
            try
            {
                using var response = await readiness.GetAsync($"{_options.ApiBase}/openapi/v1.json");
                if (response.IsSuccessStatusCode) return;
                lastError = new InvalidOperationException($"Readiness returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"WorkflowEngine.Api did not become ready. Last error: {lastError?.Message}. See {_logPath}.");
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
            File.AppendAllText(_logPath, line + Environment.NewLine);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
