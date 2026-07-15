using WorkflowEngine.Shared.Dtos;
using Xunit;

namespace WorkflowEngine.Tests;

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

        Assert.Contains(typeof(StartInstanceResultDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InstanceSummaryDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InstanceDetailDto).GetProperties(), property => property.Name == "BusinessKey");
        Assert.Contains(typeof(InboxItemDto).GetProperties(), property => property.Name == "BusinessKey");
    }
}
