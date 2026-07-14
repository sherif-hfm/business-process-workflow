using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Service.Services;
using WorkflowEngine.Shared.Models;
using Xunit;

namespace WorkflowEngine.Tests;

public sealed class DefinitionValidationTests
{
    [Fact]
    public async Task CreateAsync_CanonicalizesKnownMultiInstanceValuesCaseInsensitively()
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        multi.Mode = "SeQuEnTiAl";
        multi.Source = "CoLlEcTiOn";
        multi.CompletionEvaluation = "AfTeRaLl";
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        Assert.Equal(MultiInstanceModes.Sequential, saved.Mode);
        Assert.Equal(MultiInstanceSources.Collection, saved.Source);
        Assert.Equal(MultiInstanceCompletionEvaluations.AfterAll, saved.CompletionEvaluation);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("completion")]
    public async Task CreateAsync_RejectsUnknownMultiInstanceEnums(string target)
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        if (target == "mode") multi.Mode = "sequentual";
        if (target == "source") multi.Source = "users";
        if (target == "completion") multi.CompletionEvaluation = "sometimes";
        var service = CreateService(out _);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("completion")]
    public async Task CreateAsync_RejectsExplicitNullMultiInstanceEnums(string target)
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        if (target == "mode") multi.Mode = null!;
        if (target == "source") multi.Source = null!;
        if (target == "completion") multi.CompletionEvaluation = null!;
        var service = CreateService(out _);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateNodeIdsBeforeSingleLookups()
    {
        var model = LoadModel("votes-users-list.json");
        model.FlowNodes.Add(Clone(model.FlowNodes[0]));
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("duplicated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateFlowIdsBeforeSingleLookups()
    {
        var model = LoadModel("votes-users-list.json");
        model.SequenceFlows.Add(Clone(model.SequenceFlows[0]));
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("duplicated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsCaseInsensitiveVariableNameDuplicates()
    {
        var model = LoadModel("votes-users-list.json");
        model.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "VOTERS",
            DataType = WorkflowVariableTypes.String,
            IsArray = true,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<string>())
        });
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("case-insensitive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowDefinitionService CreateService(out CapturingDefinitionRepository repository)
    {
        repository = new CapturingDefinitionRepository();
        return new WorkflowDefinitionService(
            repository,
            new ParseOnlyScriptEvaluator(),
            NullLogger<WorkflowDefinitionService>.Instance);
    }

    internal static WorkflowModel LoadModel(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return JsonSerializer.Deserialize<WorkflowModel>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Workflow fixture did not deserialize.");
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Fixture clone failed.");

    private sealed class ParseOnlyScriptEvaluator : IScriptEvaluator
    {
        public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken) =>
            new(true, null);

        public bool IsValid(string script, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class CapturingDefinitionRepository : IWorkflowDefinitionRepository
    {
        public WorkflowDefinitionRecord? Added { get; private set; }

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListVersionsByKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task<int> GetLatestVersionAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<WorkflowDefinitionRecord> AddAsync(
            string name,
            int version,
            WorkflowModel definition,
            bool isPublished,
            CancellationToken cancellationToken)
        {
            Added = new WorkflowDefinitionRecord(
                1,
                name,
                definition.Id,
                version,
                definition,
                isPublished,
                false,
                DateTimeOffset.UtcNow);
            return Task.FromResult(Added);
        }

        public Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> SetDefaultAsync(long id, bool isDefault, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
