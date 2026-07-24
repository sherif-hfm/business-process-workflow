using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorNavigationTests
{
    private const string DiagramCandidateJson = """
        {
          "lanes": [
            { "id": 1, "name": "Intake", "externalId": "LANE_INTAKE" },
            { "id": 2, "name": "Decision", "externalId": "LANE_DECISION" }
          ],
          "flowNodes": [
            { "id": 1, "name": "Start", "externalId": "START", "type": "startEvent", "laneId": 1 },
            { "id": 2, "name": "Review", "externalId": "REVIEW", "type": "userTask", "laneId": 1 },
            { "id": 3, "name": "Decide", "externalId": "DECIDE", "type": "exclusiveGateway", "laneId": 2 },
            { "id": 4, "name": "Approve", "externalId": "APPROVE", "type": "userTask", "laneId": 2 },
            { "id": 5, "name": "Finish", "externalId": "FINISH", "type": "endEvent", "laneId": 2 },
            { "id": 6, "name": "Rework", "externalId": "REWORK", "type": "task", "laneId": 2 },
            {
              "id": 7,
              "name": "Review failed",
              "externalId": "REVIEW_FAILED",
              "type": "errorBoundaryEvent",
              "laneId": 1,
              "attachedToRef": 2
            },
            { "id": 8, "name": "Parallel fork", "externalId": "FORK", "type": "parallelGateway", "laneId": 2 },
            {
              "id": 9,
              "name": "Cancel fork",
              "externalId": "CANCEL_FORK",
              "type": "parallelInterruptEvent",
              "laneId": 2,
              "parallelGatewayRef": 8
            }
          ],
          "sequenceFlows": [
            { "id": 101, "name": "Begin review", "sourceRef": 1, "targetRef": 2 },
            { "id": 102, "name": "Review complete", "sourceRef": 2, "targetRef": 3 },
            { "id": 103, "name": "Approve", "sourceRef": 3, "targetRef": 4 },
            { "id": 104, "name": "Reject", "sourceRef": 3, "targetRef": 5 },
            { "id": 105, "name": "Needs rework", "sourceRef": 4, "targetRef": 6 },
            { "id": 106, "name": "Retry", "sourceRef": 6, "targetRef": 3 },
            { "id": 107, "name": "Review again", "sourceRef": 2, "targetRef": 2 },
            { "id": 108, "name": "Handle failure", "sourceRef": 7, "targetRef": 5 },
            { "id": 109, "name": "Fork branch", "sourceRef": 8, "targetRef": 4 },
            { "id": 110, "name": "Cancel branch", "sourceRef": 9, "targetRef": 5 }
          ]
        }
        """;

    private const string SearchCandidateJson = """
        {
          "lanes": [
            { "id": 1, "name": "Review Operations", "externalId": "LANE_REVIEW" },
            { "id": 2, "name": "Archive", "externalId": "LANE_ARCHIVE" }
          ],
          "flowNodes": [
            {
              "id": 10,
              "name": "Review",
              "externalId": "REVIEW_TASK",
              "type": "userTask",
              "laneId": 1
            },
            {
              "id": 11,
              "name": "Review Request",
              "externalId": "TASK-11",
              "type": "userTask",
              "laneId": 1
            },
            {
              "id": 12,
              "name": "Archive Case",
              "externalId": "ARCHIVE_TASK",
              "type": "task",
              "laneId": 2
            },
            {
              "id": 13,
              "name": "Finish",
              "externalId": "END",
              "type": "endEvent",
              "laneId": 2
            }
          ],
          "sequenceFlows": [
            { "id": 201, "name": "Review complete", "sourceRef": 10, "targetRef": 11 },
            { "id": 202, "name": "Continue", "sourceRef": 11, "targetRef": 12 },
            { "id": 203, "name": "", "sourceRef": 12, "targetRef": 13 }
          ]
        }
        """;

    [Fact]
    public void Trace_LocalSelectionIncludesAdjacentFlowsAndDeduplicatesSelfLoop()
    {
        using var result = Trace(
            DiagramCandidateJson,
            """{ "kind": "node", "nodeId": 2 }""",
            "local");

        AssertIds(result.RootElement, "nodeIds", 1, 2, 3);
        AssertIds(result.RootElement, "flowIds", 101, 102, 107);
        AssertIds(result.RootElement, "contextNodeIds", 7);
    }

    [Theory]
    [InlineData("upstream", new[] { 1, 2 }, new[] { 101, 107 })]
    [InlineData("downstream", new[] { 2, 3, 4, 5, 6 }, new[] { 102, 103, 104, 105, 106, 107 })]
    [InlineData("route", new[] { 1, 2, 3, 4, 5, 6 }, new[] { 101, 102, 103, 104, 105, 106, 107 })]
    public void Trace_DirectionalModesTerminateAcrossCycles(
        string mode,
        int[] expectedNodeIds,
        int[] expectedFlowIds)
    {
        using var result = Trace(
            DiagramCandidateJson,
            """{ "kind": "node", "nodeId": 2 }""",
            mode);

        AssertIds(result.RootElement, "nodeIds", expectedNodeIds);
        AssertIds(result.RootElement, "flowIds", expectedFlowIds);
        AssertIds(result.RootElement, "contextNodeIds", 7);
    }

    [Fact]
    public void Trace_FlowSelectionIncludesItsEndpoints()
    {
        using var result = Trace(
            DiagramCandidateJson,
            """{ "kind": "flow", "flowId": 103 }""",
            "local");

        AssertIds(result.RootElement, "nodeIds", 3, 4);
        AssertIds(result.RootElement, "flowIds", 103);
        AssertIds(result.RootElement, "contextNodeIds");
    }

    [Fact]
    public void Trace_LaneSelectionIncludesLaneContentsAndEveryIncidentFlow()
    {
        using var result = Trace(
            DiagramCandidateJson,
            """{ "kind": "lane", "laneId": 2 }""",
            "local");

        AssertContainsIds(result.RootElement, "nodeIds", 3, 4, 5, 6, 8, 9);
        AssertContainsIds(result.RootElement, "flowIds", 102, 103, 104, 105, 106, 108, 109, 110);
    }

    [Theory]
    [InlineData("""{ "kind": "node", "nodeId": 2 }""", 7)]
    [InlineData("""{ "kind": "node", "nodeId": 7 }""", 2)]
    [InlineData("""{ "kind": "node", "nodeId": 8 }""", 9)]
    [InlineData("""{ "kind": "node", "nodeId": 9 }""", 8)]
    public void Trace_ReportsBoundaryAndParallelInterruptReferencesAsContext(
        string selectionJson,
        int expectedContextNodeId)
    {
        using var result = Trace(DiagramCandidateJson, selectionJson, "local");

        AssertContainsIds(result.RootElement, "contextNodeIds", expectedContextNodeId);
    }

    [Theory]
    [InlineData(-1, "overview")]
    [InlineData(0.5999, "overview")]
    [InlineData(0.6, "medium")]
    [InlineData(0.8999, "medium")]
    [InlineData(0.9, "detail")]
    [InlineData(2, "detail")]
    public void SemanticZoom_UsesStableBoundaryClassifications(double zoom, string expected)
    {
        var engine = CreateEngine();

        Assert.Equal(expected, engine.Evaluate($"classifySemanticZoom({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)})").AsString());
    }

    [Fact]
    public void Search_RanksExactMatchFirstAndHonorsLimit()
    {
        using var results = Search("rEvIeW");
        var identities = ReadSearchIdentities(results.RootElement);

        Assert.Equal(("node", 10), identities[0]);
        Assert.Contains(("node", 11), identities);
        Assert.Contains(("flow", 201), identities);
        Assert.Contains(("lane", 1), identities);

        using var limitedResults = Search("review", 2);
        Assert.Equal(2, limitedResults.RootElement.GetArrayLength());
    }

    [Fact]
    public void Search_CoversIdsExternalIdsTypesLanesAndFlowEndpoints()
    {
        Assert.Equal(("flow", 201), FirstSearchIdentity("201"));
        Assert.Equal(("node", 11), FirstSearchIdentity("task-11"));
        Assert.Equal(("node", 10), FirstSearchIdentity("USERTASK"));
        Assert.Equal(("lane", 1), FirstSearchIdentity("review operations"));

        using var endpointResults = Search("archive case");
        Assert.Contains(("flow", 202), ReadSearchIdentities(endpointResults.RootElement));
    }

    [Fact]
    public void Search_BlankQueryReturnsNoResults()
    {
        using var results = Search("   ");

        Assert.Empty(results.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData(0, 0, 100, 0, false)]
    [InlineData(100, 0, 0, 0, true)]
    [InlineData(0, 0, 0, 100, false)]
    [InlineData(0, 100, 0, 0, true)]
    [InlineData(0, 0, -20, 100, false)]
    [InlineData(0, 100, 20, 0, true)]
    public void LabelDirection_IsLeftToRightOrTopToBottom(
        double beforeX,
        double beforeY,
        double afterX,
        double afterY,
        bool expected)
    {
        var engine = CreateEngine();
        var expression =
            $"shouldReverseLabelDirection({{x:{beforeX},y:{beforeY}}},{{x:{afterX},y:{afterY}}})";

        Assert.Equal(expected, engine.Evaluate(expression).AsBoolean());
    }

    [Fact]
    public void LabelGuide_ReversesEverySupportedGeometryWithoutChangingTheVisibleRoute()
    {
        var engine = CreateEngine();

        Assert.Equal(
            "M 10 0 L 0 0",
            engine.Evaluate(
                "edgeGeometryPathD({kind:'line',start:{x:0,y:0},end:{x:10,y:0}}, true)")
                .AsString());
        Assert.Equal(
            "M 10 0 Q 5 8 0 0",
            engine.Evaluate(
                "edgeGeometryPathD({kind:'quadratic',start:{x:0,y:0},control:{x:5,y:8},end:{x:10,y:0}}, true)")
                .AsString());
        Assert.Equal(
            "M 10 0 C 8 9, 2 7, 0 0",
            engine.Evaluate(
                "edgeGeometryPathD({kind:'cubic',start:{x:0,y:0},control1:{x:2,y:7},control2:{x:8,y:9},end:{x:10,y:0}}, true)")
                .AsString());
    }

    [Fact]
    public void LabelGuide_MapsBezierParameterToArcLengthFraction()
    {
        var engine = CreateEngine();
        var fraction = engine.Evaluate(
            """
            edgeArcFractionAtT(
              {kind:'quadratic',start:{x:0,y:0},control:{x:0,y:180},end:{x:320,y:180}},
              .5)
            """).AsNumber();

        Assert.InRange(fraction, 0.25, 0.45);
    }

    [Fact]
    public void LabelTruncation_PreservesFittingTextAndEllipsizesLongText()
    {
        var engine = CreateEngine();

        Assert.Equal(
            "Approval",
            engine.Evaluate("truncateFlowLabel('Approval', 100, 7)").AsString());

        var truncated = engine.Evaluate(
            "truncateFlowLabel('Continue after technical cancellation', 70, 7)").AsString();
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
        Assert.True(truncated.Length < "Continue after technical cancellation".Length);
        Assert.True(truncated.Length * 7 <= 70);
        Assert.Equal(
            "😀…",
            engine.Evaluate("truncateFlowLabel('😀😀😀', 14, 7)").AsString());
    }

    [Theory]
    [InlineData(400, 10, 80, "path")]
    [InlineData(90, 10, 100, "tangent")]
    [InlineData(180, 140, 70, "tangent")]
    [InlineData(10, 0, 80, "tangent")]
    [InlineData(0, 0, 80, "hidden")]
    public void LabelPresentation_ChoosesPathTangentOrHiddenFallback(
        double pathLength,
        double curvatureDegrees,
        double renderedTextLength,
        string expected)
    {
        var engine = CreateEngine();
        var expression =
            $"flowLabelPresentation({pathLength}, {curvatureDegrees}, {renderedTextLength})";

        Assert.Equal(expected, engine.Evaluate(expression).AsString());
    }

    [Fact]
    public void FitTransform_AccountsForPaddingCentersBoundsAndCapsZoom()
    {
        var engine = CreateEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            JSON.stringify({
              padded: calculateFitTransform(
                { left: 100, right: 300, top: 200, bottom: 300 },
                800, 600, 50, .25, 10),
              capped: calculateFitTransform(
                { left: 100, right: 300, top: 200, bottom: 300 },
                800, 600, 50, .25, 2)
            })
            """).AsString());

        var padded = result.RootElement.GetProperty("padded");
        AssertClose(8d / 3d, padded.GetProperty("zoom").GetDouble());
        AssertClose(50, padded.GetProperty("x").GetDouble());
        AssertClose(137.5, padded.GetProperty("y").GetDouble());
        AssertViewportCenter(padded, 800, 600, expectedCenterX: 200, expectedCenterY: 250);

        var capped = result.RootElement.GetProperty("capped");
        AssertClose(2, capped.GetProperty("zoom").GetDouble());
        AssertClose(0, capped.GetProperty("x").GetDouble());
        AssertClose(100, capped.GetProperty("y").GetDouble());
        AssertViewportCenter(capped, 800, 600, expectedCenterX: 200, expectedCenterY: 250);
    }

    [Fact]
    public void ViewOnlyNavigationState_RemainsOutsideTheSavedWorkflowModel()
    {
        var html = ReadEditorSource();

        Assert.Contains(
            "const canonical = JSON.parse(JSON.stringify(model));",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("model.traceMode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.labelMode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.viewState", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.searchQuery", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.labelPlacements", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.untracedOpacity", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationPanels_AutoHideAndPersistIndependentPinStates()
    {
        var html = ReadEditorSource();

        Assert.Contains("id=\"diagramToolsHandle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"diagramToolsPin\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"inspectorPin\"", html, StringComparison.Ordinal);
        Assert.Contains(".diagram-tools:hover .diagram-tools-content", html, StringComparison.Ordinal);
        Assert.Contains(".inspector-dock:not(.is-pinned):hover", html, StringComparison.Ordinal);
        Assert.Contains("main.inspector-revealed .diagram-tools", html, StringComparison.Ordinal);
        Assert.Contains("transform: translateX(calc(100% - 38px));", html, StringComparison.Ordinal);
        Assert.Contains("class=\"inspector-peek-label\"", html, StringComparison.Ordinal);
        Assert.Contains("transform: translate(-50%, -50%) rotate(90deg);", html, StringComparison.Ordinal);
        Assert.Contains(
            "main:not(.inspector-revealed) .inspector-dock:not(.is-pinned) #inspectorPin",
            html,
            StringComparison.Ordinal);
        Assert.Contains("\"flowbit.diagramToolsPinned\"", html, StringComparison.Ordinal);
        Assert.Contains("\"flowbit.inspectorPinned\"", html, StringComparison.Ordinal);
        Assert.Contains("releaseUnpinnedButtonFocus(inspectorPin, pinned);", html, StringComparison.Ordinal);
        Assert.Contains("releaseUnpinnedButtonFocus(diagramToolsPin, pinned);", html, StringComparison.Ordinal);
        Assert.Contains("mainEl.classList.toggle(\"inspector-revealed\", revealed);", html, StringComparison.Ordinal);
        Assert.Contains("mainEl.style.setProperty(\"--sidebar-width\"", html, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame(applyViewBox);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenLabelMode_HidesExternalShapeCaptionsButKeepsTaskCardTitles()
    {
        var html = ReadEditorSource();

        Assert.Matches(
            @"#svg\[data-label-mode=""off""\]\s*" +
            @"\.node:not\(\.selected\):not\(:hover\) \.shape-caption\s*" +
            @"\{\s*display:\s*none;",
            html);
        Assert.Contains(
            "#svg[data-label-mode=\"smart\"][data-zoom-level=\"medium\"]",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            ".node:not(.selected):not(:hover) .shape-caption",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".node:not(.trace-active):not(.trace-context):not(.selected):not(:hover) .shape-caption",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "#svg[data-label-mode=\"smart\"][data-zoom-level=\"overview\"]",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            ".node:not(.selected):not(:hover) .shape-caption",
            html,
            StringComparison.Ordinal);
        Assert.Contains("class: \"nid shape-caption\"", html, StringComparison.Ordinal);
        Assert.Contains("class: \"event-name shape-caption\"", html, StringComparison.Ordinal);
        Assert.Contains(
            ".node .nid:not(.shape-caption)",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[data-zoom-level=\"medium\"] .node .nid,",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[data-zoom-level=\"overview\"] .node .nid,",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("class: \"node-title shape-caption\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void InactivePathOpacity_IsConfigurableAndDefaultsToFullVisibility()
    {
        var html = ReadEditorSource();

        Assert.Contains(
            "id=\"traceOpacityInput\" type=\"range\" min=\"10\" max=\"100\" step=\"5\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("let untracedOpacity = 1;", html, StringComparison.Ordinal);
        Assert.Contains(
            "opacity: var(--trace-muted-opacity, 1);",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "svg.style.setProperty(\"--trace-muted-opacity\", String(untracedOpacity));",
            html,
            StringComparison.Ordinal);
        Assert.Contains("return \"trace-muted\";", html, StringComparison.Ordinal);
    }

    private static JsonDocument Trace(string candidateJson, string selectionJson, string mode)
    {
        var engine = CreateEngine();
        engine.SetValue("candidateJson", candidateJson);
        engine.SetValue("selectionJson", selectionJson);
        engine.SetValue("traceMode", mode);
        var resultJson = engine.Evaluate(
            "JSON.stringify(buildDiagramTraceContext(" +
            "JSON.parse(candidateJson), JSON.parse(selectionJson), traceMode))").AsString();
        return JsonDocument.Parse(resultJson);
    }

    private static JsonDocument Search(string query, int limit = 20)
    {
        var engine = CreateEngine();
        engine.SetValue("candidateJson", SearchCandidateJson);
        engine.SetValue("searchQuery", query);
        engine.SetValue("searchLimit", limit);
        var resultJson = engine.Evaluate(
            """
            JSON.stringify(searchDiagram(JSON.parse(candidateJson), searchQuery, searchLimit).map(result => ({
              kind: result.kind,
              id: result.id ?? result.nodeId ?? result.flowId ?? result.laneId
            })))
            """).AsString();
        return JsonDocument.Parse(resultJson);
    }

    private static IReadOnlyList<(string Kind, int Id)> ReadSearchIdentities(JsonElement results) =>
        results.EnumerateArray()
            .Select(result => (
                result.GetProperty("kind").GetString()!,
                result.GetProperty("id").GetInt32()))
            .ToArray();

    private static (string Kind, int Id) FirstSearchIdentity(string query)
    {
        using var results = Search(query);
        return ReadSearchIdentities(results.RootElement)[0];
    }

    private static void AssertIds(JsonElement root, string propertyName, params int[] expected)
    {
        var actual = ReadIds(root, propertyName);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id).ToArray(), actual.OrderBy(id => id).ToArray());
    }

    private static void AssertContainsIds(JsonElement root, string propertyName, params int[] expected)
    {
        var actual = ReadIds(root, propertyName);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        foreach (var id in expected)
        {
            Assert.Contains(id, actual);
        }
    }

    private static int[] ReadIds(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).EnumerateArray().Select(value => value.GetInt32()).ToArray();

    private static void AssertViewportCenter(
        JsonElement transform,
        double canvasWidth,
        double canvasHeight,
        double expectedCenterX,
        double expectedCenterY)
    {
        var zoom = transform.GetProperty("zoom").GetDouble();
        var viewportCenterX = transform.GetProperty("x").GetDouble() + canvasWidth / zoom / 2;
        var viewportCenterY = transform.GetProperty("y").GetDouble() + canvasHeight / zoom / 2;
        AssertClose(expectedCenterX, viewportCenterX);
        AssertClose(expectedCenterY, viewportCenterY);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 1e-9, expected + 1e-9);

    private static Engine CreateEngine()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN DIAGRAM NAVIGATION HELPERS(?<code>[\s\S]*?)// END DIAGRAM NAVIGATION HELPERS");
        Assert.True(match.Success, "The marked diagram navigation helper block was not found.");

        var engine = new Engine();
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }
}
