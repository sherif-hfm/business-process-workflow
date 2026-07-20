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
        Assert.Contains(typeof(StartInstanceResultDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(InstanceSummaryDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(InstanceDetailDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(UserTaskActionAckDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(MessageDeliveryAckDto).GetProperties(), property => property.Name == "Fault");
        Assert.Contains(typeof(MessageStartAckDto).GetProperties(), property => property.Name == "Fault");
    }
}
