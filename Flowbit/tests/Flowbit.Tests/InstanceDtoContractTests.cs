using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceDtoContractTests
{
    [Fact]
    public void ClaimOwnershipIsNotExposedAsATopLevelInstanceProperty()
    {
        Assert.DoesNotContain(
            typeof(StartInstanceResultDto).GetProperties(),
            property => property.Name == "ClaimedBy");
        Assert.DoesNotContain(
            typeof(InstanceSummaryDto).GetProperties(),
            property => property.Name == "ClaimedBy");
        Assert.DoesNotContain(
            typeof(InstanceDetailDto).GetProperties(),
            property => property.Name == "ClaimedBy");

        Assert.Contains(
            typeof(UserTaskDto).GetProperties(),
            property => property.Name == "ClaimedBy");
        Assert.Contains(typeof(UserTaskDto).GetProperties(), property => property.Name == "CompletedBy");
        Assert.Contains(typeof(UserTaskDto).GetProperties(), property => property.Name == "Result");
        Assert.Contains(typeof(UserTaskDto).GetProperties(), property => property.Name == "Capabilities");
        Assert.Equal(
            new[] { "ClaimedByMe", "CanClaim", "CanUnclaim", "CanAct" },
            typeof(UserTaskCapabilitiesDto).GetProperties().Select(property => property.Name));

        Assert.Contains(typeof(StartInstanceResultDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InstanceSummaryDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InstanceDetailDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InboxItemDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InboxItemDto).GetProperties(), property => property.Name == "Variables");
        Assert.Contains(typeof(StartInstanceResultDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(InstanceSummaryDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(InstanceDetailDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(UserTaskActionAckDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(MessageDeliveryAckDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(MessageStartAckDto).GetProperties(), property => property.Name == "Fault");
    }

    [Fact]
    public void ParallelExecutionAndCompletionProjectionsAreExposedConsistently()
    {
        var responseTypes = new[]
        {
            typeof(StartInstanceResultDto),
            typeof(InstanceSummaryDto),
            typeof(InstanceDetailDto),
            typeof(UserTaskActionAckDto),
            typeof(MessageDeliveryAckDto),
            typeof(MessageStartAckDto)
        };
        foreach (var responseType in responseTypes)
        {
            Assert.Contains(
                responseType.GetProperties(),
                property => property.Name == "ExecutionPositions"
                            && property.PropertyType == typeof(IReadOnlyList<ExecutionPositionDto>));
            Assert.Contains(
                responseType.GetProperties(),
                property => property.Name == "Completion"
                            && property.PropertyType == typeof(CompletionInfoDto));
        }

        Assert.Equal(
            new[]
            {
                "TokenId",
                "NodeId",
                "NodeName",
                "NodeExternalId",
                "NodeType",
                "TokenStatus",
                "ArrivedViaFlowId",
                "TerminationReason",
                "UserTaskId",
                "MultiInstanceExecutionId"
            },
            typeof(ExecutionPositionDto).GetProperties().Select(property => property.Name));
        Assert.Equal(
            new[] { "Kind", "TokenId", "NodeId", "NodeName", "NodeExternalId", "CompletedAt" },
            typeof(CompletionInfoDto).GetProperties().Select(property => property.Name));
        Assert.Equal(
            new[]
            {
                "Id",
                "ForkNodeId",
                "ParentExecutionId",
                "Status",
                "CompletionReason",
                "InterruptingNodeId",
                "InterruptingTokenId",
                "TotalBranchCount",
                "ActiveBranchCount",
                "CompletedBranchCount",
                "MergedBranchCount",
                "InterruptedBranchCount",
                "CancelledBranchCount",
                "CreatedAt",
                "UpdatedAt",
                "CompletedAt"
            },
            typeof(ParallelGatewayExecutionDto).GetProperties().Select(property => property.Name));

        Assert.Contains(
            typeof(InstanceDetailDto).GetProperties(),
            property => property.Name == "ParallelGatewayExecutions"
                        && property.PropertyType == typeof(IReadOnlyList<ParallelGatewayExecutionDto>));
        Assert.Contains(
            typeof(InstanceDetailDto).GetProperties(),
            property => property.Name == "MultiInstances"
                        && property.PropertyType == typeof(IReadOnlyList<MultiInstanceProgressDto>));
        Assert.Contains(
            typeof(UserTaskWorkSummaryDto).GetProperties(),
            property => property.Name == "NormalTaskCount");
        Assert.Contains(
            typeof(UserTaskWorkSummaryDto).GetProperties(),
            property => property.Name == "MultiInstanceTaskCount");
        Assert.Equal("normal", WorkflowCompletionKinds.Normal);
        Assert.Equal("terminate", WorkflowCompletionKinds.Terminate);
    }
}
