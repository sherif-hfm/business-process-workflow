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
    [InlineData("01-async-before-after.json")]
    [InlineData("02-intermediate-timer-delay.json")]
    [InlineData("03-recurring-timer-start.json")]
    [InlineData("04-absolute-timer-start.json")]
    [InlineData("05-user-task-reminder-and-deadline.json")]
    [InlineData("06-multi-instance-reminder.json")]
    public void DurableAsyncAndTimerExampleLoadsAndRendersInEditor(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "examples", fileName);
        var json = File.ReadAllText(path);
        var engine = CreateEditorEngine();
        engine.SetValue("durableExampleJson", json);

        var exception = Record.Exception(() => engine.Execute(
            """
            loadFromObject(JSON.parse(durableExampleJson));
            render();
            """));

        Assert.Null(exception);
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
