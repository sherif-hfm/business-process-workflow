using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

public sealed class WorkflowDefinitionService(IWorkflowDefinitionRepository definitions)
    : IWorkflowDefinitionService
{
    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var records = await definitions.ListLatestAsync(cancellationToken);
        return records.Select(ToSummary).ToList();
    }

    public async Task<WorkflowDetailDto?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var record = await definitions.GetAsync(id, cancellationToken);
        return record is null ? null : ToDetail(record);
    }

    public async Task<WorkflowDetailDto> CreateAsync(
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        WorkflowModelMigrator.Normalize(definition);
        ValidateDefinition(definition);
        var name = definition.Name.Trim();
        var version = await definitions.GetLatestVersionAsync(name, cancellationToken) + 1;
        var created = await definitions.AddAsync(name, version, definition, publish, cancellationToken);
        return ToDetail(created);
    }

    public async Task<WorkflowDetailDto?> CreateNewVersionAsync(
        long sourceWorkflowId,
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        var source = await definitions.GetAsync(sourceWorkflowId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        WorkflowModelMigrator.Normalize(definition);
        ValidateDefinition(definition);
        var name = string.IsNullOrWhiteSpace(definition.Name) ? source.Name : definition.Name.Trim();
        var version = await definitions.GetLatestVersionAsync(name, cancellationToken) + 1;
        var created = await definitions.AddAsync(name, version, definition, publish, cancellationToken);
        return ToDetail(created);
    }

    public Task<bool> PublishAsync(long id, CancellationToken cancellationToken) =>
        definitions.SetPublishedAsync(id, true, cancellationToken);

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        definitions.DeleteAsync(id, cancellationToken);

    internal static void ValidateDefinition(WorkflowModel definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new WorkflowDomainException("Workflow name is required.");
        }

        if (definition.Steps.Count == 0)
        {
            throw new WorkflowDomainException("Workflow must contain at least one step.");
        }

        if (definition.InitialStepId is null || definition.Steps.All(s => s.Id != definition.InitialStepId))
        {
            throw new WorkflowDomainException("Workflow initialStepId must reference an existing step.");
        }

        var initialStep = definition.Steps.Single(s => s.Id == definition.InitialStepId);
        if (!WorkflowStepTypes.IsStart(initialStep.Type))
        {
            throw new WorkflowDomainException("Workflow initialStepId must reference a start event.");
        }

        var stepIds = definition.Steps.Select(s => s.Id).ToHashSet();
        foreach (var step in definition.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                throw new WorkflowDomainException($"Step #{step.Id} name is required.");
            }

            if (!WorkflowStepTypes.IsSupported(step.Type))
            {
                throw new WorkflowDomainException($"Step #{step.Id} has an unsupported type '{step.Type}'.");
            }

            if (WorkflowStepTypes.IsEnd(step.Type) && step.Actions.Count > 0)
            {
                throw new WorkflowDomainException($"End event #{step.Id} cannot have outgoing actions.");
            }

            if (WorkflowStepTypes.IsAutomatic(step.Type))
            {
                if (step.NextStepId is null || !stepIds.Contains(step.NextStepId.Value))
                {
                    throw new WorkflowDomainException($"Automatic task #{step.Id} must reference an existing nextStepId.");
                }
            }

            if (WorkflowStepTypes.IsStart(step.Type))
            {
                if (step.Actions.Count > 0)
                {
                    throw new WorkflowDomainException($"Start event #{step.Id} cannot have outgoing actions.");
                }

                if (step.NextStepId is null || !stepIds.Contains(step.NextStepId.Value))
                {
                    throw new WorkflowDomainException($"Start event #{step.Id} must reference an existing nextStepId.");
                }
            }

            if (WorkflowStepTypes.IsUserTask(step.Type) && step.NextStepId is not null)
            {
                throw new WorkflowDomainException($"User task #{step.Id} cannot use nextStepId.");
            }

            foreach (var action in step.Actions)
            {
                if (!stepIds.Contains(action.ToStepId))
                {
                    throw new WorkflowDomainException($"Action #{action.Id} points to missing step #{action.ToStepId}.");
                }

                ValidateVariables(action.Variables, $"action #{action.Id}");
            }

            ValidateVariables(step.Variables, $"step #{step.Id}");
        }
    }

    private static void ValidateVariables(IEnumerable<VariableModel> variables, string owner)
    {
        var allowedTypes = new HashSet<string>
        {
            WorkflowVariableTypes.String,
            WorkflowVariableTypes.Number,
            WorkflowVariableTypes.Boolean,
            WorkflowVariableTypes.Date,
            WorkflowVariableTypes.DateTime
        };

        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                throw new WorkflowDomainException($"Variable name is required on {owner}.");
            }

            if (!allowedTypes.Contains(variable.DataType))
            {
                throw new WorkflowDomainException($"Variable '{variable.Name}' on {owner} has unsupported type '{variable.DataType}'.");
            }
        }
    }

    internal static WorkflowSummaryDto ToSummary(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.Version, record.IsPublished, record.CreatedAt);

    internal static WorkflowDetailDto ToDetail(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.Version, record.IsPublished, record.CreatedAt, record.Definition);
}
