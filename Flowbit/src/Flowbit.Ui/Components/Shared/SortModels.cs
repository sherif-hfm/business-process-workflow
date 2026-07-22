namespace Flowbit.Ui.Components.Shared;

public sealed record SortFieldOption(string Value, string Label);

internal sealed class SortDraftClause(string field, string direction)
{
    public string Field { get; set; } = field;
    public string Direction { get; set; } = direction;
}
