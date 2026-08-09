using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorRuntimeSmokeTests
{
    [Fact]
    public void UserTaskInspectorRendersWithoutLeakingTypeChangeCallbackState()
    {
        var engine = CreateEditorEngine();

        var exception = Record.Exception(() => engine.Execute(
            """
            model = {
              id: 1,
              name: 'Inspector smoke',
              initialEventId: null,
              variables: [],
              lanes: [],
              flowNodes: [{
                id: 1,
                name: 'Approve',
                type: NODE_TYPE.USER_TASK,
                laneId: null,
                x: 0,
                y: 0,
                roles: [],
                variables: [],
                requiresClaim: false,
                claimMode: 'fresh',
                requiresAssignment: false,
                assignmentMode: 'fresh',
                asyncBefore: true
              }],
              sequenceFlows: []
            };
            selected = { kind: 'node', nodeId: 1 };
            renderInspector();
            """));

        Assert.Null(exception);
    }

    [Fact]
    public void EightTimerBoundariesAreDistributedAroundCompactHost()
    {
        var engine = CreateEditorEngine();
        using var positions = JsonDocument.Parse(engine.Evaluate(
            """
            model = {
              id: 1,
              name: 'Boundary layout',
              initialEventId: null,
              variables: [],
              lanes: [],
              flowNodes: [
                {
                  id: 1,
                  name: 'Wait',
                  type: NODE_TYPE.TIMER_CATCH_EVENT,
                  laneId: null,
                  x: 100,
                  y: 100,
                  timer: { timeDuration: 'PT1H' }
                },
                ...Array.from({ length: 8 }, (_, index) => ({
                  id: index + 2,
                  name: 'Timer ' + (index + 1),
                  type: NODE_TYPE.TIMER_BOUNDARY_EVENT,
                  laneId: null,
                  x: 100,
                  y: 100,
                  attachedToRef: 1,
                  cancelActivity: false,
                  timer: { timeDuration: 'PT1H' }
                }))
              ],
              sequenceFlows: []
            };
            JSON.stringify(model.flowNodes.slice(1).map(nodePosition));
            """).AsString());

        var coordinates = positions.RootElement
            .EnumerateArray()
            .Select(point => (
                X: point.GetProperty("x").GetDouble(),
                Y: point.GetProperty("y").GetDouble()))
            .ToArray();

        Assert.Equal(8, coordinates.Distinct().Count());
        Assert.Contains(coordinates, point => point.X < 100);
        Assert.Contains(coordinates, point => point.X > 136);
        Assert.Contains(coordinates, point => point.Y < 100);
        Assert.Contains(coordinates, point => point.Y > 136);
    }

    [Fact]
    public void TimerBoundaryFlowRoutesOutsideItsAttachedHost()
    {
        var engine = CreateEditorEngine();
        using var route = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'boundary-route',
                name: 'Boundary route',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 2,
                    name: 'First approval',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 367,
                    y: 109
                  },
                  {
                    id: 3,
                    name: 'Second approval',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 810,
                    y: 128
                  },
                  {
                    id: 5,
                    name: 'Second approval timer',
                    type: NODE_TYPE.TIMER_BOUNDARY_EVENT,
                    laneId: null,
                    x: 814,
                    y: 113,
                    attachedToRef: 3,
                    cancelActivity: true,
                    timer: { timeDuration: 'PT1M' }
                  }
                ],
                sequenceFlows: [{
                  id: 105,
                  name: 'auto-back',
                  sourceRef: 5,
                  targetRef: 2
                }]
              };
              const flow = model.sequenceFlows[0];
              const group = { a: 2, b: 5, flows: [flow], laneSpacing: 0 };
              const geometry = routePairGeometries(group).get(flow.id);
              const hostBounds = edgeNodeBounds(getNode(3));
              return JSON.stringify({
                startsInsideHost: edgePointInsideBox(geometry.start, hostBounds),
                crossesHost: edgeCurveHitsBox(geometry, hostBounds)
              });
            })()
            """).AsString());

        Assert.False(route.RootElement.GetProperty("startsInsideHost").GetBoolean());
        Assert.False(route.RootElement.GetProperty("crossesHost").GetBoolean());
    }

    [Fact]
    public void LoaderPreservesMalformedDurableMetadataForValidation()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            loadFromObject({
              id: 'malformed-import',
              name: 'Malformed import',
              variables: [],
              lanes: [],
              flowNodes: [
                {
                  id: 1,
                  name: 'Host',
                  type: 'task',
                  asyncBefore: 'false',
                  asyncAfter: null,
                  job: {
                    failureHandling: 7,
                    retryDelays: 'PT1S'
                  }
                },
                {
                  id: 2,
                  name: 'Timer',
                  type: 'timerBoundaryEvent',
                  attachedToRef: 1,
                  cancelActivity: 'false',
                  timer: {
                    timeDate: 5,
                    timeDuration: 'PT1H'
                  }
                }
              ],
              sequenceFlows: []
            });
            model.flowNodes.forEach(applyTypeInvariants);
            JSON.stringify(model.flowNodes);
            """).AsString());

        var host = loaded.RootElement[0];
        Assert.Equal("false", host.GetProperty("asyncBefore").GetString());
        Assert.Equal(JsonValueKind.Null, host.GetProperty("asyncAfter").ValueKind);
        Assert.Equal(7, host.GetProperty("job").GetProperty("failureHandling").GetInt32());
        Assert.Equal(
            "PT1S",
            host.GetProperty("job").GetProperty("retryDelays").GetString());

        var timer = loaded.RootElement[1];
        Assert.Equal("false", timer.GetProperty("cancelActivity").GetString());
        Assert.Equal(5, timer.GetProperty("timer").GetProperty("timeDate").GetInt32());
        Assert.Equal(
            "PT1H",
            timer.GetProperty("timer").GetProperty("timeDuration").GetString());
    }

    [Fact]
    public void LoaderDropsLegacyAdministrativeFlagsAndPreservesNormalFlowRoles()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            loadFromObject({
              id: 'legacy-administrative-flags',
              name: 'Legacy administrative flags',
              variables: [],
              lanes: [],
              flowNodes: [
                { id: 1, name: 'Start', type: 'startEvent', x: 0, y: 0 },
                { id: 2, name: 'First approval', type: 'userTask', x: 200, y: 0 },
                { id: 3, name: 'Second approval', type: 'userTask', x: 400, y: 0 }
              ],
              sequenceFlows: [
                { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                {
                  id: 201,
                  name: 'Back',
                  sourceRef: 3,
                  targetRef: 2,
                  roles: ['admin'],
                  isAdministrative: true,
                  isBatchable: true
                }
              ]
            });
            JSON.stringify(model.sequenceFlows.find(flow => flow.id === 201));
            """).AsString());

        Assert.Equal("admin", loaded.RootElement.GetProperty("roles")[0].GetString());
        Assert.False(loaded.RootElement.TryGetProperty("isAdministrative", out _));
        Assert.False(loaded.RootElement.TryGetProperty("isBatchable", out _));
    }

    [Fact]
    public void LoaderPreservesOrderedAttributesAndCanonicalizesOnlyKeys()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-round-trip',
                name: 'Attribute round trip',
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Start',
                    type: 'startEvent',
                    attributes: [
                      { key: '  Owner  ', value: '  Alice\nBob  ' },
                      { key: 'Second', value: '' },
                      { key: '\uFEFFkeep-bom\uFEFF', value: 'bom remains part of the key' },
                      { key: '\u0085trim-nel\u0085', value: 'NEL is .NET whitespace' }
                    ]
                  },
                  { id: 2, name: 'Review', type: 'userTask', attributes: null },
                  { id: 3, name: 'End', type: 'endEvent' }
                ],
                sequenceFlows: [
                  {
                    id: 101,
                    name: 'Continue',
                    sourceRef: 1,
                    targetRef: 2,
                    attributes: [{ key: '  route  ', value: ' keep spaces ' }]
                  },
                  { id: 201, name: 'Finish', sourceRef: 2, targetRef: 3 }
                ]
              });
              const before = JSON.parse(JSON.stringify({
                node: model.flowNodes[0].attributes,
                nullNode: model.flowNodes[1].attributes,
                missingNode: model.flowNodes[2].attributes,
                flow: model.sequenceFlows[0].attributes,
                missingFlow: model.sequenceFlows[1].attributes
              }));
              model.flowNodes.forEach(canonicalizeAttributeKeys);
              model.sequenceFlows.forEach(canonicalizeAttributeKeys);
              model.flowNodes[1].type = 'exclusiveGateway';
              applyTypeInvariants(model.flowNodes[1]);
              return JSON.stringify({
                before,
                canonicalNode: model.flowNodes[0].attributes,
                canonicalFlow: model.sequenceFlows[0].attributes,
                afterTypeChange: model.flowNodes[1].attributes
              });
            })()
            """).AsString());

        var before = loaded.RootElement.GetProperty("before");
        var nodeAttributes = before.GetProperty("node");
        Assert.Equal("  Owner  ", nodeAttributes[0].GetProperty("key").GetString());
        Assert.Equal("  Alice\nBob  ", nodeAttributes[0].GetProperty("value").GetString());
        Assert.Equal("Second", nodeAttributes[1].GetProperty("key").GetString());
        Assert.Equal(string.Empty, nodeAttributes[1].GetProperty("value").GetString());
        Assert.Equal("\uFEFFkeep-bom\uFEFF", nodeAttributes[2].GetProperty("key").GetString());
        Assert.Equal("\u0085trim-nel\u0085", nodeAttributes[3].GetProperty("key").GetString());
        Assert.Empty(before.GetProperty("nullNode").EnumerateArray());
        Assert.Empty(before.GetProperty("missingNode").EnumerateArray());
        Assert.Empty(before.GetProperty("missingFlow").EnumerateArray());

        Assert.Equal(
            "Owner",
            loaded.RootElement.GetProperty("canonicalNode")[0].GetProperty("key").GetString());
        Assert.Equal(
            "\uFEFFkeep-bom\uFEFF",
            loaded.RootElement.GetProperty("canonicalNode")[2].GetProperty("key").GetString());
        Assert.Equal(
            "trim-nel",
            loaded.RootElement.GetProperty("canonicalNode")[3].GetProperty("key").GetString());
        Assert.Equal(
            "  Alice\nBob  ",
            loaded.RootElement.GetProperty("canonicalNode")[0].GetProperty("value").GetString());
        Assert.Equal(
            "route",
            loaded.RootElement.GetProperty("canonicalFlow")[0].GetProperty("key").GetString());
        Assert.Equal(
            " keep spaces ",
            loaded.RootElement.GetProperty("canonicalFlow")[0].GetProperty("value").GetString());
        Assert.Empty(loaded.RootElement.GetProperty("afterTypeChange").EnumerateArray());
    }

    [Fact]
    public void LegacyLoaderPreservesStepAndActionAttributes()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'legacy-attributes',
                name: 'Legacy attributes',
                initialStepId: 1,
                phases: [],
                steps: [
                  {
                    id: 1,
                    name: 'Review',
                    type: 'task',
                    attributes: [{ key: 'node-key', value: 'node-value' }],
                    actions: [{
                      id: 201,
                      name: 'Finish',
                      toStepId: 2,
                      attributes: [{ key: 'flow-key', value: 'flow-value' }]
                    }]
                  },
                  { id: 2, name: 'End', type: 'end', attributes: null, actions: [] }
                ]
              });
              return JSON.stringify({
                node: model.flowNodes.find(node => node.id === 1).attributes,
                end: model.flowNodes.find(node => node.id === 2).attributes,
                flow: model.sequenceFlows.find(flow => flow.id === 201).attributes
              });
            })()
            """).AsString());

        Assert.Equal(
            "node-value",
            loaded.RootElement.GetProperty("node")[0].GetProperty("value").GetString());
        Assert.Empty(loaded.RootElement.GetProperty("end").EnumerateArray());
        Assert.Equal(
            "flow-value",
            loaded.RootElement.GetProperty("flow")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void MalformedImportedAttributesDoNotCrashInspectorsAndRemainInvalid()
    {
        var engine = CreateEditorEngine();
        var exception = Record.Exception(() => engine.Execute(
            """
            loadFromObject({
              id: 'malformed-attributes',
              name: 'Malformed attributes',
              initialEventId: 1,
              variables: [],
              lanes: [],
              flowNodes: [
                { id: 1, name: 'Start', type: 'startEvent', attributes: 'not-a-list' },
                { id: 2, name: 'End', type: 'endEvent' }
              ],
              sequenceFlows: [{
                id: 101,
                name: '',
                sourceRef: 1,
                targetRef: 2,
                attributes: [null, { key: 7, value: null }]
              }]
            });
            selected = { kind: 'node', nodeId: 1 };
            renderInspector();
            selected = { kind: 'flow', flowId: 101 };
            renderInspector();
            """));

        Assert.Null(exception);
        using var errors = JsonDocument.Parse(engine.Evaluate(
            "JSON.stringify(validateModelForSave(model));").AsString());
        var messages = errors.RootElement.EnumerateArray()
            .Select(error => error.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains(messages, message =>
            message.Contains("Flow node #1 attributes must be an array", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #1 must be an object", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #2 key must be a string", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #2 value must be a non-null string", StringComparison.Ordinal));
    }

    [Fact]
    public void NewNodesBoundariesAndFlowsStartWithEmptyAttributes()
    {
        var engine = CreateEditorEngine();
        using var created = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = newModel();
              addNode();
              const host = model.flowNodes[0];
              const flow = createFlow(host.id, host.id);
              addTimerBoundary(host);
              addErrorBoundary(host);
              return JSON.stringify({
                node: host.attributes,
                flow: flow.attributes,
                timerBoundary: model.flowNodes.find(node => node.type === 'timerBoundaryEvent').attributes,
                errorBoundary: model.flowNodes.find(node => node.type === 'errorBoundaryEvent').attributes
              });
            })()
            """).AsString());

        Assert.All(created.RootElement.EnumerateObject(), property =>
            Assert.Empty(property.Value.EnumerateArray()));
    }

    [Fact]
    public void AttributeInspectorAddsEditsAndDeletesRowsInOrder()
    {
        var engine = CreateEditorEngine();
        using var edited = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-editor',
                name: 'Attribute editor',
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Task',
                  type: 'userTask',
                  attributes: [
                    { key: 'first', value: 'one' },
                    { key: 'second', value: 'two' }
                  ]
                }],
                sequenceFlows: []
              });
              selected = { kind: 'node', nodeId: 1 };
              renderInspector();

              const descendants = root => {
                const result = [root];
                for (const child of root.children || []) result.push(...descendants(child));
                return result;
              };
              const latest = predicate => descendants(inspector).filter(predicate).pop();
              latest(element => element.textContent === '+ Add attribute').onclick();

              const deleteButtons = descendants(inspector)
                .filter(element => element.textContent === 'Delete attribute');
              deleteButtons.slice(-3)[1].onclick();

              const keyField = latest(element => element.innerHTML === '<label>Key</label>');
              const valueField = latest(element => element.innerHTML === '<label>Value</label>');
              keyField.children[0].value = 'third';
              keyField.children[0].oninput();
              valueField.children[0].value = 'three\nlines';
              valueField.children[0].oninput();
              return JSON.stringify(model.flowNodes[0].attributes);
            })()
            """).AsString());

        Assert.Equal(2, edited.RootElement.GetArrayLength());
        Assert.Equal("first", edited.RootElement[0].GetProperty("key").GetString());
        Assert.Equal("one", edited.RootElement[0].GetProperty("value").GetString());
        Assert.Equal("third", edited.RootElement[1].GetProperty("key").GetString());
        Assert.Equal("three\nlines", edited.RootElement[1].GetProperty("value").GetString());
    }

    [Fact]
    public void AttributeEditsRoundTripThroughUndoAndRedoHistory()
    {
        var engine = CreateEditorEngine();
        using var history = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-history',
                name: 'Attribute history',
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Task',
                  type: 'userTask',
                  attributes: [{ key: 'state', value: 'before' }]
                }],
                sequenceFlows: []
              });
              model.flowNodes[0].attributes[0].value = 'after';
              commitHistory();
              undo();
              const undone = model.flowNodes[0].attributes[0].value;
              redo();
              const redone = model.flowNodes[0].attributes[0].value;
              return JSON.stringify({ undone, redone });
            })()
            """).AsString());

        Assert.Equal("before", history.RootElement.GetProperty("undone").GetString());
        Assert.Equal("after", history.RootElement.GetProperty("redone").GetString());
    }

    [Fact]
    public void FriendlyDurationCycleAndLocalDateHelpersProduceCanonicalIsoValues()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const local = '2027-01-15T12:34:56';
              const utc = browserLocalDateTimeToUtcIso(local);
              return JSON.stringify({
                seconds: formatSimpleIsoDuration('10', 'seconds'),
                minutes: formatSimpleIsoDuration('5', 'minutes'),
                hours: formatSimpleIsoDuration('3', 'hours'),
                days: formatSimpleIsoDuration('2', 'days'),
                weeks: formatSimpleIsoDuration('1', 'weeks'),
                decimal: formatSimpleIsoDuration('0.5', 'seconds'),
                forever: formatSimpleIsoCycle('forever', null, 'P2D'),
                limited: formatSimpleIsoCycle('limited', 5, 'PT1H'),
                compoundIsAdvanced: parseSimpleIsoDuration('P1DT2H') === null,
                utc,
                localRoundTrip: isoToBrowserLocalDateTime(utc)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("PT10S", root.GetProperty("seconds").GetString());
        Assert.Equal("PT5M", root.GetProperty("minutes").GetString());
        Assert.Equal("PT3H", root.GetProperty("hours").GetString());
        Assert.Equal("P2D", root.GetProperty("days").GetString());
        Assert.Equal("P1W", root.GetProperty("weeks").GetString());
        Assert.Equal("PT0.5S", root.GetProperty("decimal").GetString());
        Assert.Equal("R/P2D", root.GetProperty("forever").GetString());
        Assert.Equal("R5/PT1H", root.GetProperty("limited").GetString());
        Assert.True(root.GetProperty("compoundIsAdvanced").GetBoolean());
        Assert.EndsWith("Z", root.GetProperty("utc").GetString(), StringComparison.Ordinal);
        Assert.Equal("2027-01-15T12:34:56", root.GetProperty("localRoundTrip").GetString());
    }

    [Fact]
    public void FriendlyEditorsPreserveImportedValuesUntilTheUserEditsThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function byData(root, key) {
                return descendants(root).find(element => element.dataset[key] === 'true');
              }

              let durationValue = 'P1DT2H';
              let durationChanges = 0;
              const durationParent = fakeElement();
              const duration = renderDurationEditor(
                durationParent,
                'Delay',
                durationValue,
                value => { durationValue = value; durationChanges++; });
              const durationAdvancedInitiallyOpen = byData(duration, 'advancedIso').open === true;
              const amount = byData(duration, 'durationAmount');
              const unit = byData(duration, 'durationUnit');
              amount.value = '2';
              unit.value = 'days';
              amount.oninput();

              let cycleValue = 'R/P1DT2H';
              let cycleChanges = 0;
              const cycle = renderCycleEditor(
                fakeElement(),
                cycleValue,
                value => { cycleValue = value; cycleChanges++; });

              let dateValue = '2027-01-15T12:34:56+03:00';
              let dateChanges = 0;
              const absolute = renderAbsoluteTimerEditor(
                fakeElement(),
                dateValue,
                value => { dateValue = value; dateChanges++; });

              return JSON.stringify({
                durationAdvancedInitiallyOpen,
                durationValue,
                durationChanges,
                cycleAdvancedOpen: byData(cycle, 'advancedIso').open === true,
                cycleValue,
                cycleChanges,
                dateAdvancedOpen: byData(absolute, 'advancedIso').open === true,
                dateValue,
                dateChanges
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("durationAdvancedInitiallyOpen").GetBoolean());
        Assert.Equal("P2D", root.GetProperty("durationValue").GetString());
        Assert.Equal(1, root.GetProperty("durationChanges").GetInt32());
        Assert.True(root.GetProperty("cycleAdvancedOpen").GetBoolean());
        Assert.Equal("R/P1DT2H", root.GetProperty("cycleValue").GetString());
        Assert.Equal(0, root.GetProperty("cycleChanges").GetInt32());
        Assert.False(root.GetProperty("dateAdvancedOpen").GetBoolean());
        Assert.Equal("2027-01-15T12:34:56+03:00", root.GetProperty("dateValue").GetString());
        Assert.Equal(0, root.GetProperty("dateChanges").GetInt32());
    }

    [Fact]
    public void TimerInspectorFriendlyControlsUpdateTheExistingIsoProperties()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function byData(root, key) {
                return descendants(root).find(element => element.dataset[key] === 'true');
              }
              function renderTimer(node) {
                inspector.replaceChildren();
                renderTimerSection(node);
              }

              const durationNode = { timer: { timeDate: null, timeDuration: 'PT1H', timeCycle: null } };
              renderTimer(durationNode);
              let amount = byData(inspector, 'durationAmount');
              let unit = byData(inspector, 'durationUnit');
              amount.value = '2';
              unit.value = 'days';
              amount.oninput();

              const cycleNode = { timer: { timeDate: null, timeDuration: null, timeCycle: 'R/P2D' } };
              renderTimer(cycleNode);
              const mode = byData(inspector, 'cycleMode');
              const count = byData(inspector, 'cycleCount');
              count.value = '3';
              mode.value = 'limited';
              mode.onchange();

              const dateNode = {
                timer: { timeDate: '2027-01-15T12:34:56+03:00', timeDuration: null, timeCycle: null }
              };
              const dateBeforeRender = dateNode.timer.timeDate;
              renderTimer(dateNode);
              const dateAfterRender = dateNode.timer.timeDate;
              const local = byData(inspector, 'localDateTime');
              local.value = '2027-02-16T08:15:30';
              local.oninput();

              const compoundNode = {
                timer: { timeDate: null, timeDuration: 'P1DT2H', timeCycle: null }
              };
              const compoundBefore = JSON.stringify(compoundNode.timer);
              renderTimer(compoundNode);

              return JSON.stringify({
                duration: durationNode.timer.timeDuration,
                cycle: cycleNode.timer.timeCycle,
                dateBeforeRender,
                dateAfterRender,
                editedDate: dateNode.timer.timeDate,
                compoundPreserved: JSON.stringify(compoundNode.timer) === compoundBefore,
                compoundAdvancedOpen: byData(inspector, 'advancedIso').open === true
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("P2D", root.GetProperty("duration").GetString());
        Assert.Equal("R3/P2D", root.GetProperty("cycle").GetString());
        Assert.Equal("2027-01-15T12:34:56+03:00", root.GetProperty("dateBeforeRender").GetString());
        Assert.Equal(root.GetProperty("dateBeforeRender").GetString(), root.GetProperty("dateAfterRender").GetString());
        Assert.EndsWith("Z", root.GetProperty("editedDate").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("compoundPreserved").GetBoolean());
        Assert.True(root.GetProperty("compoundAdvancedOpen").GetBoolean());
    }

    [Fact]
    public void RetryDelayCardsAddRemoveReorderAndEnforceTheLimit()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function renderPolicy(node) {
                inspector.replaceChildren();
                renderAsyncJobSection(node);
                return descendants(inspector);
              }
              function button(elements, text) {
                return elements.find(element => element.tagName === 'BUTTON' && element.textContent === text);
              }

              const node = {
                id: 901,
                type: NODE_TYPE.TASK,
                asyncBefore: true,
                asyncAfter: false,
                job: { failureHandling: 'boundaryFirst', retryDelays: ['PT10S', 'PT1M', 'PT5M'] }
              };
              let elements = renderPolicy(node);
              const firstCard = elements.find(element => element.dataset.retryDelayIndex === '0');
              button(descendants(firstCard), 'Move down').onclick();
              const reordered = [...node.job.retryDelays];

              elements = renderPolicy(node);
              button(elements, 'Remove').onclick();
              const afterRemove = [...node.job.retryDelays];

              node.job.retryDelays = [];
              elements = renderPolicy(node);
              button(elements, '+ Add retry delay').onclick();
              const afterAdd = [...node.job.retryDelays];

              node.job.retryDelays = Array.from({ length: 10 }, () => 'PT1S');
              elements = renderPolicy(node);
              const limitButton = button(elements, 'Retry limit reached');

              return JSON.stringify({
                reordered,
                afterRemove,
                afterAdd,
                limitDisabled: limitButton.disabled,
                limitCount: node.job.retryDelays.length
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(new[] { "PT1M", "PT10S", "PT5M" },
            root.GetProperty("reordered").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "PT10S", "PT5M" },
            root.GetProperty("afterRemove").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "PT10S" },
            root.GetProperty("afterAdd").EnumerateArray().Select(value => value.GetString()));
        Assert.True(root.GetProperty("limitDisabled").GetBoolean());
        Assert.Equal(10, root.GetProperty("limitCount").GetInt32());
    }

    [Theory]
    [MemberData(nameof(ExampleWorkflowData.All), MemberType = typeof(ExampleWorkflowData))]
    public void ExampleWorkflowLoadsValidatesAndRendersInEditor(string fileName)
    {
        var json = ExampleWorkflowData.Read(fileName);
        var engine = CreateEditorEngine();
        engine.SetValue("exampleWorkflowJson", json);
        string? validationJson = null;

        var exception = Record.Exception(() =>
        {
            engine.Execute(
                """
                loadFromObject(JSON.parse(exampleWorkflowJson));
                render();
                """);
            validationJson = engine.Evaluate(
                "JSON.stringify(validateModelForSave(model));").AsString();
        });

        Assert.Null(exception);
        using var validation = JsonDocument.Parse(validationJson!);
        Assert.Empty(validation.RootElement.EnumerateArray());
        Assert.StartsWith("example-", engine.Evaluate("model.id").AsString(), StringComparison.Ordinal);
    }

    private static Engine CreateEditorEngine()
    {
        var html = ReadEditorSource();
        var scripts = Regex.Matches(
            html,
            @"<script(?:\s[^>]*)?>(?<code>[\s\S]*?)</script>");
        Assert.True(scripts.Count >= 2, "The editor's main inline script was not found.");

        var engine = new Engine();
        engine.Execute(DomStubs);
        engine.Execute(scripts[^1].Groups["code"].Value);
        return engine;
    }

    private const string DomStubs =
        """
        const fakeClassList = {
          add() {},
          remove() {},
          toggle() { return false; },
          contains() { return false; }
        };
        function fakeElement(tag = 'div') {
          return {
            tagName: String(tag).toUpperCase(),
            style: { setProperty() {} },
            dataset: {},
            classList: fakeClassList,
            children: [],
            attributes: {},
            value: '',
            checked: false,
            disabled: false,
            hidden: false,
            textContent: '',
            innerHTML: '',
            clientWidth: 1000,
            clientHeight: 700,
            appendChild(child) { this.children.push(child); return child; },
            replaceChildren(...children) { this.children = children; },
            remove() {},
            addEventListener() {},
            setAttribute(key, value) { this.attributes[key] = String(value); },
            setAttributeNS(_namespace, key, value) { this.attributes[key] = String(value); },
            getAttribute(key) { return this.attributes[key] ?? null; },
            removeAttribute(key) { delete this.attributes[key]; },
            querySelectorAll() { return []; },
            querySelector() { return null; },
            closest() { return null; },
            matches() { return false; },
            contains() { return false; },
            focus() {},
            blur() {},
            click() {},
            setPointerCapture() {},
            releasePointerCapture() {},
            getBoundingClientRect() {
              return {
                x: 0, y: 0, left: 0, top: 0,
                right: 1000, bottom: 700, width: 1000, height: 700
              };
            },
            getTotalLength() { return 100; },
            getPointAtLength(value) { return { x: value, y: 0 }; },
            createSVGPoint() {
              return {
                x: 0,
                y: 0,
                matrixTransform() { return { x: this.x, y: this.y }; }
              };
            },
            getScreenCTM() {
              return { inverse() { return {}; } };
            }
          };
        }
        const fakeElements = new Map();
        const document = {
          documentElement: fakeElement('html'),
          body: fakeElement('body'),
          activeElement: null,
          getElementById(id) {
            if (!fakeElements.has(id)) {
              fakeElements.set(id, fakeElement(id === 'svg' ? 'svg' : 'div'));
            }
            return fakeElements.get(id);
          },
          querySelector(selector) {
            if (selector === 'main') return this.getElementById('main');
            if (selector === '.canvas-wrap') return this.getElementById('canvas-wrap');
            return fakeElement();
          },
          querySelectorAll() { return []; },
          createElement: fakeElement,
          createElementNS(_namespace, tag) { return fakeElement(tag); },
          createTextNode(text) {
            const node = fakeElement('#text');
            node.textContent = String(text);
            return node;
          },
          addEventListener() {}
        };
        const localStorage = {
          getItem() { return null; },
          setItem() {}
        };
        const fakeUrl = {
          createObjectURL() { return 'blob:test'; },
          revokeObjectURL() {}
        };
        const window = {
          document,
          localStorage,
          URL: fakeUrl,
          innerWidth: 1400,
          innerHeight: 900,
          addEventListener() {},
          removeEventListener() {},
          matchMedia() {
            return { matches: false, addEventListener() {} };
          },
          showSaveFilePicker: null
        };
        function Blob() {}
        function FileReader() {}
        function ResizeObserver() { this.observe = function() {}; }
        function alert() {}
        function confirm() { return true; }
        function setTimeout() { return 0; }
        function clearTimeout() {}
        function requestAnimationFrame(callback) { callback(); return 0; }
        function structuredClone(value) {
          return JSON.parse(JSON.stringify(value));
        }
        const navigator = {};
        const URL = fakeUrl;
        window.requestAnimationFrame = requestAnimationFrame;
        """;

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }
}
