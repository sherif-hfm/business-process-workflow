using Flowbit.Shared.Dtos;
using Flowbit.Ui.Auth;
using Flowbit.Ui.Clients;

namespace Flowbit.Ui.Dashboard;

public sealed class DashboardService(WorkflowApiClient api, TokenState token)
{
    public async Task<DashboardSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!token.HasToken)
        {
            return DashboardSnapshot.SignedOut;
        }

        var recentTask = CaptureAsync(() => api.GetInstancesAsync(
            page: 1,
            pageSize: 6,
            cancellationToken: cancellationToken));
        var runningTask = LoadCountAsync("running", cancellationToken);
        var completedTask = LoadCountAsync("completed", cancellationToken);
        var faultedTask = LoadCountAsync("faulted", cancellationToken);
        var cancelledTask = LoadCountAsync("cancelled", cancellationToken);
        var inboxTask = CaptureAsync(() => api.GetInboxAsync(
            page: 1,
            pageSize: 5,
            cancellationToken: cancellationToken));
        var workflowsTask = CaptureAsync(() => api.GetWorkflowsAsync(cancellationToken));

        await Task.WhenAll(
            recentTask,
            runningTask,
            completedTask,
            faultedTask,
            cancelledTask,
            inboxTask,
            workflowsTask);

        var recent = await recentTask;
        var running = await runningTask;
        var completed = await completedTask;
        var faulted = await faultedTask;
        var cancelled = await cancelledTask;
        var inbox = await inboxTask;
        var workflows = await workflowsTask;

        return new DashboardSnapshot(
            HasIdentity: true,
            TotalInstances: recent.Value?.TotalCount ?? 0,
            RunningInstances: running.Value,
            CompletedInstances: completed.Value,
            FaultedInstances: faulted.Value,
            CancelledInstances: cancelled.Value,
            RecentInstances: recent.Value?.Items ?? [],
            InboxCount: inbox.Value?.TotalCount ?? 0,
            RecentInbox: inbox.Value?.Items ?? [],
            Workflows: workflows.Value ?? [],
            InstancesError: recent.Error ?? running.Error ?? completed.Error ?? faulted.Error ?? cancelled.Error,
            InboxError: inbox.Error,
            WorkflowsError: workflows.Error);
    }

    private Task<CaptureResult<long>> LoadCountAsync(string status, CancellationToken cancellationToken) =>
        CaptureAsync(async () =>
        {
            var result = await api.GetInstancesAsync(
                status: status,
                page: 1,
                pageSize: 1,
                cancellationToken: cancellationToken);
            return result.TotalCount;
        });

    private static async Task<CaptureResult<T>> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new CaptureResult<T>(await action(), null);
        }
        catch (Exception ex)
        {
            return new CaptureResult<T>(default, ex.Message);
        }
    }

    private sealed record CaptureResult<T>(T? Value, string? Error);
}

public sealed record DashboardSnapshot(
    bool HasIdentity,
    long TotalInstances,
    long RunningInstances,
    long CompletedInstances,
    long FaultedInstances,
    long CancelledInstances,
    IReadOnlyList<InstanceSummaryDto> RecentInstances,
    long InboxCount,
    IReadOnlyList<InboxItemDto> RecentInbox,
    IReadOnlyList<WorkflowSummaryDto> Workflows,
    string? InstancesError,
    string? InboxError,
    string? WorkflowsError)
{
    public static DashboardSnapshot SignedOut { get; } = new(
        false, 0, 0, 0, 0, 0, [], 0, [], [], null, null, null);
}
