using System.Threading.Channels;
using Npgsql;

namespace Flowbit.Worker;

public interface IJobWakeupSignal
{
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class PostgresJobWakeupSignal(
    NpgsqlDataSource dataSource,
    ILogger<PostgresJobWakeupSignal> logger) : BackgroundService, IJobWakeupSignal
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = true
        });

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            _ = await _signals.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Normal polling timeout.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, _) => _signals.Writer.TryWrite(true);
                await using (var listen = new NpgsqlCommand("LISTEN flowbit_jobs", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PostgreSQL job notification listener disconnected; polling remains authoritative.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
