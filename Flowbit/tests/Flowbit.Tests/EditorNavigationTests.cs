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
              "type": "scopedInterruptEvent",
              "laneId": 2,
              "gatewayRef": 8
            },
            {
              "id": 10,
              "name": "First reviewer wins",
              "externalId": "CANCELLING_JOIN",
              "type": "exclusiveGateway",
              "laneId": 2,
              "joinCancellation": { "gatewayRef": 8 }
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
    [InlineData("""{ "kind": "node", "nodeId": 8 }""", 10)]
    [InlineData("""{ "kind": "node", "nodeId": 10 }""", 8)]
    public void Trace_ReportsBoundaryInterruptAndCancellingJoinReferencesAsContext(
        string selectionJson,
        int expectedContextNodeId)
    {
        using var result = Trace(DiagramCandidateJson, selectionJson, "local");

        AssertContainsIds(result.RootElement, "contextNodeIds", expectedContextNodeId);
    }

    [Fact]
    public void ScopedInterruptSelectorReachabilityIncludesImplicitBoundaryAttachment()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"function buildGatewayAdjacencyIndex[\s\S]*?(?=function gatewayTopology)");
        Assert.True(match.Success, "The editor gateway adjacency helpers were not found.");

        var engine = new Engine();
        engine.Execute(
            """
            function isBoundaryEventType(type) { return type === 'errorBoundaryEvent'; }
            """);
        engine.Execute(match.Value);
        engine.SetValue(
            "candidateJson",
            """
            {
              "flowNodes": [
                { "id": 2, "type": "parallelGateway" },
                { "id": 3, "type": "serviceTask" },
                { "id": 4, "type": "errorBoundaryEvent", "attachedToRef": 3 },
                { "id": 5, "type": "scopedInterruptEvent" },
                { "id": 6, "type": "parallelGateway" }
              ],
              "sequenceFlows": [
                { "id": 201, "sourceRef": 2, "targetRef": 3 },
                { "id": 401, "sourceRef": 4, "targetRef": 5 },
                { "id": 501, "sourceRef": 5, "targetRef": 6 }
              ]
            }
            """);

        Assert.True(engine.Evaluate(
            """
            const adjacency = buildGatewayAdjacencyIndex(JSON.parse(candidateJson));
            canReachWithAdjacency(2, 5, adjacency) &&
              !canReachWithAdjacency(6, 5, adjacency) &&
              !canReachWithAdjacencyAvoiding(2, 6, 5, adjacency);
            """).AsBoolean());
        Assert.Contains(
            "canReachWithAdjacency(candidate.id, node.id, adjacency)",
            html,
            StringComparison.Ordinal);
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
        Assert.DoesNotContain("model.activeTool", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.snapToGrid", html, StringComparison.Ordinal);
        Assert.DoesNotContain("model.gridSize", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GridControls_AreAccessibleAndPersistAsEditorPreferences()
    {
        var html = ReadEditorSource();

        Assert.Matches(
            @"<input(?=[^>]*\bid=""snapToGridInput"")(?=[^>]*\btype=""checkbox"")[^>]*>",
            html);
        Assert.Matches(
            @"<input(?=[^>]*\bid=""gridSizeInput"")(?=[^>]*\btype=""number"")(?=[^>]*\bmin=""4"")(?=[^>]*\bmax=""200"")(?=[^>]*\bstep=""1"")(?=[^>]*\bvalue=""24"")(?=[^>]*\baria-label=""Grid size"")[^>]*>",
            html);
        Assert.Contains(
            "title=\"Align nodes to the nearest grid point while dragging\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "const SNAP_TO_GRID_STORAGE_KEY = \"flowbit.snapToGrid\";",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "const GRID_SIZE_STORAGE_KEY = \"flowbit.gridSize\";",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "gridPattern.setAttribute(\"width\", String(gridSize));",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "gridPattern.setAttribute(\"height\", String(gridSize));",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GridHelpers_ClampSizeAndSnapToNearestIntersection()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN GRID HELPERS(?<code>[\s\S]*?)// END GRID HELPERS");
        Assert.True(match.Success, "The marked grid helper block was not found.");

        var engine = new Engine();
        engine.Execute(match.Groups["code"].Value);

        Assert.Equal(24d, engine.Evaluate("normalizeGridSize(null)").AsNumber());
        Assert.Equal(24d, engine.Evaluate("normalizeGridSize('')").AsNumber());
        Assert.Equal(4d, engine.Evaluate("normalizeGridSize(1)").AsNumber());
        Assert.Equal(200d, engine.Evaluate("normalizeGridSize(500)").AsNumber());
        Assert.Equal(32d, engine.Evaluate("normalizeGridSize(31.6)").AsNumber());
        Assert.Equal(180d, engine.Evaluate("snapCoordinateToGrid(177, 20)").AsNumber());
        Assert.Equal(200d, engine.Evaluate("snapCoordinateToGrid(193, 20)").AsNumber());
        Assert.Equal(-20d, engine.Evaluate("snapCoordinateToGrid(-17, 20)").AsNumber());
    }

    [Fact]
    public void EditorMenus_KeepFileEditAndViewInHeaderWithVerticalAuthoringPalette()
    {
        var html = ReadEditorSource();
        var menuGroup = Regex.Match(
            html,
            @"<div(?=[^>]*\bclass=""[^""]*\btoolbar-menu-group\b[^""]*"")(?=[^>]*\baria-label=""Editor menus"")[^>]*>");
        Assert.True(menuGroup.Success, "The compact editor menu group was not found.");
        var headingIndex = html.IndexOf("<h1>", StringComparison.Ordinal);
        var workflowNameIndex = html.IndexOf("id=\"wfName\"", StringComparison.Ordinal);
        Assert.True(
            headingIndex >= 0 && headingIndex < menuGroup.Index && menuGroup.Index < workflowNameIndex,
            "The editor menus must appear between the brand heading and workflow name.");
        var headerMarkup = Regex.Match(
            html,
            @"<header\b[^>]*>(?<markup>[\s\S]*?)</header>").Groups["markup"].Value;
        Assert.Equal(3, Regex.Matches(headerMarkup, @"<details\b").Count);
        Assert.DoesNotContain("id=\"addMenu\"", headerMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"toolMenu\"", headerMarkup, StringComparison.Ordinal);

        var fileMenu = Regex.Match(
            html,
            @"<details(?=[^>]*\bid=""fileMenu"")(?=[^>]*\bclass=""toolbar-menu"")(?<attributes>[^>]*)>(?<markup>[\s\S]*?)</details>");
        Assert.True(fileMenu.Success, "The File toolbar menu was not found.");
        Assert.DoesNotMatch(@"\bopen(?:\s*=|\s|$)", fileMenu.Groups["attributes"].Value);

        var fileMarkup = fileMenu.Groups["markup"].Value;
        Assert.Matches(
            @"<summary(?=[^>]*\bid=""fileMenuSummary"")(?=[^>]*\baria-label=""File actions"")[^>]*>",
            fileMarkup);
        Assert.Equal(3, Regex.Matches(fileMarkup, @"<button\b").Count);
        Assert.Matches(@"<button(?=[^>]*\bid=""newBtn"")(?=[^>]*\btype=""button"")[^>]*>", fileMarkup);
        Assert.Matches(@"<button(?=[^>]*\bid=""loadBtn"")(?=[^>]*\btype=""button"")[^>]*>", fileMarkup);
        Assert.Matches(@"<button(?=[^>]*\bid=""saveBtn"")(?=[^>]*\btype=""button"")[^>]*>", fileMarkup);

        var editMenu = Regex.Match(
            html,
            @"<details(?=[^>]*\bid=""editMenu"")(?=[^>]*\bclass=""[^""]*\btoolbar-menu\b[^""]*"")(?<attributes>[^>]*)>(?<markup>[\s\S]*?)</details>");
        Assert.True(editMenu.Success, "The Edit toolbar menu was not found.");
        Assert.DoesNotMatch(@"\bopen(?:\s*=|\s|$)", editMenu.Groups["attributes"].Value);

        var editMarkup = editMenu.Groups["markup"].Value;
        Assert.Matches(
            @"<summary(?=[^>]*\bid=""editMenuSummary"")(?=[^>]*\baria-label=""Edit history"")[^>]*>",
            editMarkup);
        Assert.Equal(2, Regex.Matches(editMarkup, @"<button\b").Count);
        Assert.Matches(@"<button(?=[^>]*\bid=""undoBtn"")(?=[^>]*\btype=""button"")[^>]*>", editMarkup);
        Assert.Matches(@"<button(?=[^>]*\bid=""redoBtn"")(?=[^>]*\btype=""button"")[^>]*>", editMarkup);

        var palette = Regex.Match(
            html,
            @"<section(?=[^>]*\bid=""authoringPalette"")(?=[^>]*\bclass=""authoring-palette"")(?=[^>]*\baria-label=""Workflow authoring tools"")[^>]*>(?<markup>[\s\S]*?)</section>");
        Assert.True(palette.Success, "The vertical authoring palette was not found.");
        Assert.True(palette.Index > html.IndexOf("</header>", StringComparison.Ordinal));

        var paletteMarkup = palette.Groups["markup"].Value;
        Assert.Matches(
            @"<button(?=[^>]*\bid=""authoringPaletteHandle"")(?=[^>]*\bdata-active-tool=""pan"")(?=[^>]*\baria-controls=""authoringPaletteContent"")(?=[^>]*\baria-expanded=""false"")[^>]*>",
            paletteMarkup);
        Assert.Matches(
            @"<button(?=[^>]*\bid=""authoringPalettePin"")(?=[^>]*\baria-pressed=""false"")[^>]*>",
            paletteMarkup);
        Assert.Contains("<span class=\"authoring-palette-title\">Tools</span>", paletteMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool:", paletteMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("activeToolLabel", paletteMarkup, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(paletteMarkup, @"<button\b").Count);
        Assert.Matches(@"<button(?=[^>]*\bid=""addStepBtn"")(?=[^>]*\btype=""button"")[^>]*>", paletteMarkup);
        Assert.DoesNotMatch(@"<button(?=[^>]*\bid=""addStepBtn"")(?=[^>]*\bclass=""[^""]*\bprimary\b)[^>]*>", paletteMarkup);
        Assert.Matches(@"<button(?=[^>]*\bid=""addPhaseBtn"")(?=[^>]*\btype=""button"")[^>]*>", paletteMarkup);
        Assert.Matches(
            @"<button(?=[^>]*\bid=""connectBtn"")(?=[^>]*\btype=""button"")(?=[^>]*\baria-pressed=""false"")[^>]*>",
            paletteMarkup);
        Assert.Matches(
            @"<button(?=[^>]*\bid=""selectToolBtn"")(?=[^>]*\baria-pressed=""false"")[^>]*>",
            paletteMarkup);
        Assert.Matches(
            @"<button(?=[^>]*\bid=""panToolBtn"")(?=[^>]*\baria-pressed=""true"")[^>]*>",
            paletteMarkup);
        Assert.Matches(
            @"<svg(?=[^>]*\bid=""svg"")(?=[^>]*\brole=""application"")(?=[^>]*\baria-label=""Workflow diagram canvas"")(?=[^>]*\bdata-active-tool=""pan"")[^>]*>",
            html);

        var viewMenu = Regex.Match(
            html,
            @"<details(?=[^>]*\bid=""viewMenu"")(?=[^>]*\bclass=""[^""]*\btoolbar-menu\b[^""]*"")(?<attributes>[^>]*)>(?<markup>[\s\S]*?)</details>");
        Assert.True(viewMenu.Success, "The View toolbar menu was not found.");
        Assert.DoesNotMatch(@"\bopen(?:\s*=|\s|$)", viewMenu.Groups["attributes"].Value);

        var viewMarkup = viewMenu.Groups["markup"].Value;
        Assert.Matches(
            @"<summary(?=[^>]*\bid=""viewMenuSummary"")(?=[^>]*\baria-label=""View options"")[^>]*>",
            viewMarkup);
        Assert.Contains("id=\"labelModeSelect\"", viewMarkup, StringComparison.Ordinal);
        Assert.Contains("id=\"snapToGridInput\"", viewMarkup, StringComparison.Ordinal);
        Assert.Contains("id=\"gridSizeInput\"", viewMarkup, StringComparison.Ordinal);
        Assert.Contains("id=\"traceOpacityInput\"", viewMarkup, StringComparison.Ordinal);
        Assert.Contains("id=\"themeToggleBtn\"", viewMarkup, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(viewMarkup, @"<button\b").Cast<Match>());

        Assert.Contains("let activeTool = \"pan\";", html, StringComparison.Ordinal);
        Assert.Matches(@"setActiveTool\(""pan""\);\s*resetViewport\(\);", html);
        Assert.Contains("function setActiveTool(tool)", html, StringComparison.Ordinal);
        Assert.Contains(
            "function closeToolbarMenus(except = null, restoreFocus = false)",
            html,
            StringComparison.Ordinal);
        Assert.Matches(
            @"\.toolbar-menu-popover\s*\{[^}]*position:\s*absolute;",
            html);
        Assert.Contains(
            ".toolbar-menu:not([open]) > .toolbar-menu-popover { display: none; }",
            html,
            StringComparison.Ordinal);
        Assert.Matches(
            @"\.toolbar-menu-popover\s+\.button-label\s*\{[^}]*display:\s*inline;",
            html);
        Assert.Matches(
            @"\.toolbar-menu-end\s+\.toolbar-menu-popover\s*\{[^}]*right:\s*0;[^}]*left:\s*auto;",
            html);
        Assert.Matches(
            @"\.authoring-palette\s*\{[^}]*position:\s*absolute;[^}]*left:\s*12px;[^}]*display:\s*flex;",
            html);
        Assert.Matches(
            @"\.authoring-palette-section\s*\{[^}]*flex-direction:\s*column;",
            html);
        Assert.Contains(
            ".authoring-palette.is-pinned .authoring-palette-content",
            html,
            StringComparison.Ordinal);
        Assert.Matches(
            @"\.view-menu-popover\s*\{[^}]*width:\s*min\(270px,\s*calc\(100vw\s*-\s*32px\)\);[^}]*min-width:\s*0;",
            html);
        Assert.Matches(
            @"@media\s*\(max-width:\s*300px\)\s*\{[\s\S]*?\.toolbar-menu\s+\.toolbar-menu-popover\s*\{[^}]*position:\s*fixed;[^}]*right:\s*16px;[^}]*left:\s*16px;",
            html);
        Assert.Matches(
            @"<div(?=[^>]*\bid=""hint"")(?=[^>]*\brole=""status"")(?=[^>]*\baria-live=""polite"")[^>]*>",
            html);
        Assert.DoesNotMatch(
            @"<div(?=[^>]*\bclass=""toolbar-group"")(?=[^>]*\baria-label=""File actions"")[^>]*>",
            html);
        Assert.DoesNotMatch(
            @"<div(?=[^>]*\bclass=""toolbar-group"")(?=[^>]*\baria-label=""Editor tools"")[^>]*>",
            html);
        Assert.DoesNotMatch(
            @"<div(?=[^>]*\bclass=""toolbar-group"")(?=[^>]*\baria-label=""Authoring actions"")[^>]*>",
            html);
        Assert.DoesNotMatch(
            @"<div(?=[^>]*\bclass=""toolbar-group"")(?=[^>]*\baria-label=""Edit history"")[^>]*>",
            html);
        Assert.DoesNotMatch(
            @"<div(?=[^>]*\bclass=""toolbar-group"")(?=[^>]*\baria-label=""View options"")[^>]*>",
            html);
        Assert.Matches(
            @"\.toolbar-identity\s*\{[^}]*min-width:\s*min\(360px,\s*100%\);",
            html);
        Assert.Matches(
            @"\.wf-name\s*\{[^}]*min-width:\s*0;",
            html);
    }

    [Fact]
    public void ThemeToggle_FollowsSystemAndPersistsOutsideTheWorkflowModel()
    {
        var html = ReadEditorSource();

        Assert.Contains("id=\"themeToggleBtn\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "aria-label=\"Switch to light theme\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"light\"]", html, StringComparison.Ordinal);
        Assert.Contains("color-scheme: dark;", html, StringComparison.Ordinal);
        Assert.Contains("color-scheme: light;", html, StringComparison.Ordinal);
        Assert.Contains(
            "const THEME_STORAGE_KEY = \"flowbit.theme\";",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.matchMedia(\"(prefers-color-scheme: dark)\")",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "document.documentElement.dataset.theme = theme;",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "writeUiPreference(THEME_STORAGE_KEY, theme);",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "themeMediaQuery.addEventListener(\"change\", followSystemTheme);",
            html,
            StringComparison.Ordinal);
        Assert.Contains("fill=\"var(--grid-dot)\"", html, StringComparison.Ordinal);
        Assert.Contains("fill=\"var(--edge-stroke)\"", html, StringComparison.Ordinal);
        Assert.Contains("t.tagName === \"BUTTON\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "themeToggleBtn.setAttribute(\"aria-pressed\"",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("model.theme", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("light", false, true, true, "light", "#eef2f7")]
    [InlineData(null, false, true, true, "dark", "#09111f")]
    [InlineData("sepia", false, true, false, "light", "#eef2f7")]
    [InlineData(null, true, false, false, "dark", "#09111f")]
    public void ThemeBootstrap_AppliesStoredOrSystemPreferenceBeforePaint(
        string? storedTheme,
        bool storageThrows,
        bool hasMatchMedia,
        bool systemPrefersDark,
        string expectedTheme,
        string expectedThemeColor)
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"<script>(?<code>[\s\S]*?)</script>");
        Assert.True(match.Success, "The pre-paint theme bootstrap was not found.");

        var engine = new Engine();
        engine.SetValue("storedTheme", storedTheme);
        engine.SetValue("storageThrows", storageThrows);
        engine.SetValue("hasMatchMedia", hasMatchMedia);
        engine.SetValue("systemPrefersDark", systemPrefersDark);
        engine.Execute(
            """
            const localStorage = {
              getItem: function() {
                if (storageThrows) throw new Error("Storage blocked");
                return storedTheme;
              }
            };
            const window = hasMatchMedia
              ? {
                  matchMedia: function() {
                    return { matches: systemPrefersDark };
                  }
                }
              : {};
            let themeColor = null;
            const document = {
              documentElement: { dataset: {} },
              getElementById: function() {
                return {
                  setAttribute: function(name, value) {
                    if (name === "content") themeColor = value;
                  }
                };
              }
            };
            """);

        engine.Execute(match.Groups["code"].Value);

        Assert.Equal(
            expectedTheme,
            engine.Evaluate("document.documentElement.dataset.theme").AsString());
        Assert.Equal(expectedThemeColor, engine.Evaluate("themeColor").AsString());
    }

    [Fact]
    public void ThemeHelpers_ValidateAndAlternateTheSupportedThemes()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN THEME HELPERS(?<code>[\s\S]*?)// END THEME HELPERS");
        Assert.True(match.Success, "The theme helpers were not found.");

        var engine = new Engine();
        engine.Execute(
            """
            let themeMediaQuery = { matches: false };
            let themeWasExplicitlyChosen = false;
            const themeAttributes = {};
            const themeToggleBtn = {
              title: "",
              setAttribute: function(name, value) { themeAttributes[name] = value; }
            };
            const themeToggleLabel = { textContent: "" };
            const themeColorMeta = {
              setAttribute: function(name, value) {
                if (name === "content") this.content = value;
              }
            };
            const document = {
              documentElement: { dataset: {} },
              getElementById: function() { return themeColorMeta; }
            };
            let persistedKey = null;
            let persistedValue = null;
            function writeUiPreference(key, value) {
              persistedKey = key;
              persistedValue = value;
            }
            const THEME_STORAGE_KEY = "flowbit.theme";
            """);
        engine.Execute(match.Groups["code"].Value);

        Assert.Equal("light", engine.Evaluate("nextTheme('dark')").AsString());
        Assert.Equal("dark", engine.Evaluate("nextTheme('light')").AsString());
        Assert.Equal("dark", engine.Evaluate("nextTheme('sepia')").AsString());
        Assert.True(engine.Evaluate("normalizeTheme('sepia') === null").AsBoolean());

        Assert.Equal("dark", engine.Evaluate("setTheme('dark', true)").AsString());
        Assert.Equal(
            "dark",
            engine.Evaluate("document.documentElement.dataset.theme").AsString());
        Assert.Equal(
            "Switch to light theme",
            engine.Evaluate("themeAttributes['aria-label']").AsString());
        Assert.Equal("Light", engine.Evaluate("themeToggleLabel.textContent").AsString());
        Assert.Equal(
            "Switch to light theme",
            engine.Evaluate("themeToggleBtn.title").AsString());
        Assert.True(engine.Evaluate("themeWasExplicitlyChosen").AsBoolean());
        Assert.Equal("flowbit.theme", engine.Evaluate("persistedKey").AsString());
        Assert.Equal("dark", engine.Evaluate("persistedValue").AsString());
        Assert.Equal("#09111f", engine.Evaluate("themeColorMeta.content").AsString());

        Assert.Equal("light", engine.Evaluate("setTheme('light', false)").AsString());
        Assert.Equal(
            "Switch to dark theme",
            engine.Evaluate("themeAttributes['aria-label']").AsString());
        Assert.Equal("Dark", engine.Evaluate("themeToggleLabel.textContent").AsString());
        Assert.Equal(
            "Switch to dark theme",
            engine.Evaluate("themeToggleBtn.title").AsString());
        Assert.Equal("dark", engine.Evaluate("persistedValue").AsString());
        Assert.Equal("#eef2f7", engine.Evaluate("themeColorMeta.content").AsString());

        Assert.Equal(
            "dark",
            engine.Evaluate(
                "themeMediaQuery.matches = true; setTheme('sepia', false)")
                .AsString());

        engine.Execute(
            "themeWasExplicitlyChosen = false; followSystemTheme({ matches: false });");
        Assert.Equal(
            "light",
            engine.Evaluate("document.documentElement.dataset.theme").AsString());

        engine.Execute(
            "themeWasExplicitlyChosen = true; followSystemTheme({ matches: true });");
        Assert.Equal(
            "light",
            engine.Evaluate("document.documentElement.dataset.theme").AsString());

        Assert.Equal(
            "dark",
            engine.Evaluate("themeMediaQuery = null; setTheme('sepia', false)")
                .AsString());
    }

    [Fact]
    public void NavigationPanels_AutoHideAndPersistIndependentPinStates()
    {
        var html = ReadEditorSource();

        Assert.Contains("id=\"diagramToolsHandle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"diagramToolsPin\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"authoringPaletteHandle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"authoringPalettePin\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"inspectorPin\"", html, StringComparison.Ordinal);
        Assert.Contains(".diagram-tools:hover .diagram-tools-content", html, StringComparison.Ordinal);
        Assert.Contains(
            ".authoring-palette:hover .authoring-palette-content",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            ".authoring-palette.is-open .authoring-palette-content",
            html,
            StringComparison.Ordinal);
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
        Assert.Contains("\"flowbit.authoringPalettePinned\"", html, StringComparison.Ordinal);
        Assert.Contains("\"flowbit.inspectorPinned\"", html, StringComparison.Ordinal);
        Assert.Contains("releaseUnpinnedButtonFocus(inspectorPin, pinned);", html, StringComparison.Ordinal);
        Assert.Contains("releaseUnpinnedButtonFocus(diagramToolsPin, pinned);", html, StringComparison.Ordinal);
        Assert.Contains("releaseUnpinnedButtonFocus(authoringPalettePin, pinned);", html, StringComparison.Ordinal);
        Assert.Contains("function setAuthoringPalettePinned(pinned, persist = true)", html, StringComparison.Ordinal);
        Assert.Contains("function closeAuthoringPalette(restoreFocus = false)", html, StringComparison.Ordinal);
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
