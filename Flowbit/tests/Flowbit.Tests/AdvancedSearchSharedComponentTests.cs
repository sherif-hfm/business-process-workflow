extern alias FlowbitUi;

#pragma warning disable BL0005 // Tests exercise component parameters without a renderer.

using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using AdvancedVariableFilterEditor = FlowbitUi::Flowbit.Ui.Components.Shared.AdvancedVariableFilterEditor;
using SortFieldOption = FlowbitUi::Flowbit.Ui.Components.Shared.SortFieldOption;
using SortToolbar = FlowbitUi::Flowbit.Ui.Components.Shared.SortToolbar;
using VariableValues = FlowbitUi::Flowbit.Ui.Components.Shared.VariableValues;
using Xunit;

namespace Flowbit.Tests;

public sealed class AdvancedSearchSharedComponentTests
{
    [Fact]
    public async Task Editor_FormatAndClear_UpdateParentState()
    {
        string? changed = null;
        var editor = new AdvancedVariableFilterEditor
        {
            Value = "{\"center\":{\"$eq\":\"MC-1042\"}}",
            ValueChanged = EventCallback.Factory.Create<string?>(
                this,
                value => changed = value)
        };

        await InvokeAsync(editor, "FormatAsync");

        Assert.NotNull(changed);
        Assert.Contains(Environment.NewLine, changed, StringComparison.Ordinal);
        using (var formatted = JsonDocument.Parse(changed))
        {
            Assert.Equal(
                "MC-1042",
                formatted.RootElement.GetProperty("center").GetProperty("$eq").GetString());
        }

        await InvokeAsync(editor, "ClearAsync");
        Assert.Null(changed);
    }

    [Fact]
    public async Task Editor_MalformedJsonDoesNotUpdateParentAndStoresInlineError()
    {
        var callbackCount = 0;
        var editor = new AdvancedVariableFilterEditor
        {
            Value = "{invalid}",
            ValueChanged = EventCallback.Factory.Create<string?>(
                this,
                _ => callbackCount++)
        };

        await InvokeAsync(editor, "FormatAsync");

        Assert.Equal(0, callbackCount);
        Assert.Contains(
            "not valid JSON",
            GetField<string?>(editor, "validationError"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SortToolbar_EmitsStructuredClausesInPriorityOrder()
    {
        IReadOnlyList<SearchSortDto>? applied = null;
        var toolbar = new SortToolbar
        {
            Id = "test-sort",
            Options =
            [
                new SortFieldOption("updatedAt", "Updated"),
                new SortFieldOption("id", "ID")
            ],
            DefaultField = "updatedAt",
            DefaultDirection = "desc",
            Applied = EventCallback.Factory.Create<IReadOnlyList<SearchSortDto>>(
                this,
                value => applied = value)
        };
        Invoke(toolbar, "OnParametersSet");
        Invoke(toolbar, "Add");
        Invoke(toolbar, "ChangeDirection", 1, "asc");

        await InvokeAsync(toolbar, "ApplyAsync");

        Assert.Equal(
            [new SearchSortDto("updatedAt", "desc"), new SearchSortDto("id", "asc")],
            applied);
    }

    [Fact]
    public void VariableValues_FormatsNestedJsonForTheCollapsibleView()
    {
        using var document = JsonDocument.Parse("{\"id\":\"MC-1042\",\"services\":[\"health-certificate\"]}");
        var component = new VariableValues
        {
            Values = new Dictionary<string, JsonElement>
            {
                ["request"] = document.RootElement.Clone()
            }
        };

        var formatted = GetProperty<string>(component, "FormattedValues");

        Assert.Contains(Environment.NewLine, formatted, StringComparison.Ordinal);
        Assert.Contains("health-certificate", formatted, StringComparison.Ordinal);
    }

    private static async Task InvokeAsync(object target, string name)
    {
        var task = Assert.IsAssignableFrom<Task>(Invoke(target, name));
        await task;
    }

    private static object? Invoke(object target, string name, params object?[]? arguments) =>
        Method(target, name).Invoke(target, arguments);

    private static MethodInfo Method(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Method '{name}' was not found on {target.GetType().Name}.");

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? (default(T) is null
                ? default!
                : throw new InvalidOperationException($"Field '{name}' is null or missing.")));

    private static T GetProperty<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Property '{name}' is null or missing."));
}

#pragma warning restore BL0005
