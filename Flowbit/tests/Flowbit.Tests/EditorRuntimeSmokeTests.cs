using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorRuntimeSmokeTests
{
    [Fact]
    public void ToolTransitionsFollowFileAndCreationActions()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const startup = {
                activeTool,
                svgTool: svg.dataset.activeTool,
                handleTool: authoringPaletteHandle.dataset.activeTool,
                handleExpanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed')
              };

              model.flowNodes.push({
                id: 99,
                name: 'Existing node',
                type: NODE_TYPE.TASK,
                laneId: null,
                x: 100,
                y: 100,
                attributes: [],
                roles: [],
                variables: []
              });
              fileMenu.open = true;
              document.getElementById('newBtn').onclick();
              const afterNew = {
                activeTool,
                menuClosed: !fileMenu.open,
                nodesCleared: model.flowNodes.length === 0,
                connectMode
              };

              setActiveTool('pan');
              document.getElementById('addStepBtn').onclick();
              const afterAddNode = {
                activeTool,
                selectedKind: selected?.kind,
                nodeCount: model.flowNodes.length
              };

              setActiveTool('pan');
              document.getElementById('addPhaseBtn').onclick();
              const afterAddLane = {
                activeTool,
                selectedKind: selected?.kind,
                laneCount: model.lanes.length
              };

              setActiveTool('pan');
              document.getElementById('connectBtn').onclick();
              const afterConnect = {
                activeTool,
                connectMode,
                connectPressed: document.getElementById('connectBtn').getAttribute('aria-pressed')
              };
              setConnectMode(false);

              setActiveTool('select');
              const originalFileReader = FileReader;
              FileReader = function() {
                this.readAsText = file => {
                  this.result = file.contents;
                  this.onload();
                };
              };
              const fileInput = document.getElementById('fileInput');

              fileInput.files = [];
              fileInput.onchange({ target: fileInput });
              const afterCanceledLoad = { activeTool };

              const modelBeforeInvalidLoad = model;
              fileInput.files = [{ contents: '{ invalid json' }];
              fileInput.value = 'invalid-workflow.json';
              fileInput.onchange({ target: fileInput });
              const afterInvalidLoad = {
                activeTool,
                modelUnchanged: model === modelBeforeInvalidLoad,
                inputReset: fileInput.value === ''
              };

              fileInput.files = [{ contents: JSON.stringify(newModel()) }];
              fileInput.value = 'loaded-workflow.json';
              fileInput.onchange({ target: fileInput });
              const afterLoad = {
                activeTool,
                inputReset: fileInput.value === '',
                connectMode
              };
              FileReader = originalFileReader;

              return JSON.stringify({
                startup,
                afterNew,
                afterAddNode,
                afterAddLane,
                afterConnect,
                afterCanceledLoad,
                afterInvalidLoad,
                afterLoad
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var startup = root.GetProperty("startup");
        Assert.Equal("pan", startup.GetProperty("activeTool").GetString());
        Assert.Equal("pan", startup.GetProperty("svgTool").GetString());
        Assert.Equal("pan", startup.GetProperty("handleTool").GetString());
        Assert.Equal("false", startup.GetProperty("handleExpanded").GetString());
        Assert.Equal("false", startup.GetProperty("selectPressed").GetString());
        Assert.Equal("false", startup.GetProperty("rectanglePressed").GetString());
        Assert.Equal("true", startup.GetProperty("panPressed").GetString());

        var afterNew = root.GetProperty("afterNew");
        Assert.Equal("select", afterNew.GetProperty("activeTool").GetString());
        Assert.True(afterNew.GetProperty("menuClosed").GetBoolean());
        Assert.True(afterNew.GetProperty("nodesCleared").GetBoolean());
        Assert.False(afterNew.GetProperty("connectMode").GetBoolean());

        var afterAddNode = root.GetProperty("afterAddNode");
        Assert.Equal("select", afterAddNode.GetProperty("activeTool").GetString());
        Assert.Equal("node", afterAddNode.GetProperty("selectedKind").GetString());
        Assert.Equal(1, afterAddNode.GetProperty("nodeCount").GetInt32());

        var afterAddLane = root.GetProperty("afterAddLane");
        Assert.Equal("select", afterAddLane.GetProperty("activeTool").GetString());
        Assert.Equal("lane", afterAddLane.GetProperty("selectedKind").GetString());
        Assert.Equal(1, afterAddLane.GetProperty("laneCount").GetInt32());

        var afterConnect = root.GetProperty("afterConnect");
        Assert.Equal("select", afterConnect.GetProperty("activeTool").GetString());
        Assert.True(afterConnect.GetProperty("connectMode").GetBoolean());
        Assert.Equal("true", afterConnect.GetProperty("connectPressed").GetString());

        Assert.Equal(
            "select",
            root.GetProperty("afterCanceledLoad").GetProperty("activeTool").GetString());

        var afterInvalidLoad = root.GetProperty("afterInvalidLoad");
        Assert.Equal("select", afterInvalidLoad.GetProperty("activeTool").GetString());
        Assert.True(afterInvalidLoad.GetProperty("modelUnchanged").GetBoolean());
        Assert.True(afterInvalidLoad.GetProperty("inputReset").GetBoolean());

        var afterLoad = root.GetProperty("afterLoad");
        Assert.Equal("pan", afterLoad.GetProperty("activeTool").GetString());
        Assert.True(afterLoad.GetProperty("inputReset").GetBoolean());
        Assert.False(afterLoad.GetProperty("connectMode").GetBoolean());
    }

    [Fact]
    public void ToolbarMenusCloseWithoutChangingSelection()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              selected = { kind: 'node', nodeId: 42 };
              setActiveTool('select');

              fileMenu.open = true;
              editMenu.open = true;
              viewMenu.open = true;
              const helperClosedSibling = closeToolbarMenus(viewMenu);
              const helperState = {
                fileOpen: fileMenu.open,
                editOpen: editMenu.open,
                viewOpen: viewMenu.open
              };

              fileMenu.open = true;
              editMenu.open = true;
              viewMenu.open = true;
              fileMenu.dispatchEvent({ type: 'toggle', target: fileMenu, bubbles: false });
              const toggleState = {
                fileOpen: fileMenu.open,
                editOpen: editMenu.open,
                viewOpen: viewMenu.open
              };

              let fileInputClicked = false;
              document.getElementById('fileInput').click = () => {
                fileInputClicked = true;
              };
              fileMenu.open = true;
              document.getElementById('loadBtn').onclick();
              const fileActionClosed = !fileMenu.open;

              editMenu.open = true;
              document.getElementById('undoBtn').onclick();
              const editActionClosed = !editMenu.open;

              viewMenu.open = true;
              labelModeSelect.value = 'all';
              labelModeSelect.dispatchEvent({
                type: 'change', target: labelModeSelect, bubbles: false
              });
              const viewControlState = {
                stayedOpen: viewMenu.open,
                labelMode
              };
              viewMenu.open = false;
              setActiveTool('pan');
              selected = { kind: 'node', nodeId: 42 };

              const focusedEditSummary = editMenuSummary;
              editMenu.contains = target => target === focusedEditSummary;
              fileMenu.open = true;
              document.dispatchEvent({
                type: 'focusin', target: focusedEditSummary, bubbles: false
              });
              const focusMovedClosedSibling = !fileMenu.open;

              const historyCountBeforeInputUndo = undoHistory.length;
              const inputUndo = {
                type: 'keydown',
                key: 'z',
                code: 'KeyZ',
                target: fakeElement('input'),
                ctrlKey: true,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(inputUndo);
              const nativeInputUndoState = {
                prevented: inputUndo.defaultPrevented,
                historyUnchanged: undoHistory.length === historyCountBeforeInputUndo
              };

              const insideTarget = document.getElementById('newBtn');
              fileMenu.contains = target => target === insideTarget;
              fileMenu.open = true;
              document.dispatchEvent(fakePointerEvent(
                'pointerdown', insideTarget, 301, 0, 0));
              const insidePointerKeptMenuOpen = fileMenu.open;

              document.dispatchEvent(fakePointerEvent(
                'pointerdown', fakeElement('div'), 302, 0, 0));
              const outsidePointerClosedMenus = toolbarMenus.every(menu => !menu.open);

              const focusedViewItem = document.getElementById('themeToggleBtn');
              let viewSummaryFocused = false;
              viewMenu.contains = target => target === focusedViewItem;
              viewMenuSummary.focus = () => { viewSummaryFocused = true; };
              document.activeElement = focusedViewItem;
              viewMenu.open = true;
              document.getElementById('validation-modal').style.display = 'none';
              const escape = {
                type: 'keydown',
                key: 'Escape',
                code: 'Escape',
                target: svg,
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(escape);

              return JSON.stringify({
                helperClosedSibling,
                helperState,
                toggleState,
                fileActionClosed,
                fileInputClicked,
                editActionClosed,
                viewControlState,
                focusMovedClosedSibling,
                nativeInputUndoState,
                insidePointerKeptMenuOpen,
                outsidePointerClosedMenus,
                escapeClosedMenu: !viewMenu.open,
                escapePrevented: escape.defaultPrevented,
                viewSummaryFocused,
                activeToolAfterEscape: activeTool,
                selectedAfterEscape: selected
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("helperClosedSibling").GetBoolean());
        Assert.False(root.GetProperty("helperState").GetProperty("fileOpen").GetBoolean());
        Assert.False(root.GetProperty("helperState").GetProperty("editOpen").GetBoolean());
        Assert.True(root.GetProperty("helperState").GetProperty("viewOpen").GetBoolean());
        Assert.True(root.GetProperty("toggleState").GetProperty("fileOpen").GetBoolean());
        Assert.False(root.GetProperty("toggleState").GetProperty("editOpen").GetBoolean());
        Assert.False(root.GetProperty("toggleState").GetProperty("viewOpen").GetBoolean());

        Assert.True(root.GetProperty("fileActionClosed").GetBoolean());
        Assert.True(root.GetProperty("fileInputClicked").GetBoolean());

        Assert.True(root.GetProperty("editActionClosed").GetBoolean());
        Assert.True(root.GetProperty("viewControlState").GetProperty("stayedOpen").GetBoolean());
        Assert.Equal("all", root.GetProperty("viewControlState").GetProperty("labelMode").GetString());
        Assert.True(root.GetProperty("focusMovedClosedSibling").GetBoolean());
        Assert.False(root.GetProperty("nativeInputUndoState").GetProperty("prevented").GetBoolean());
        Assert.True(root.GetProperty("nativeInputUndoState").GetProperty("historyUnchanged").GetBoolean());
        Assert.True(root.GetProperty("insidePointerKeptMenuOpen").GetBoolean());
        Assert.True(root.GetProperty("outsidePointerClosedMenus").GetBoolean());
        Assert.True(root.GetProperty("escapeClosedMenu").GetBoolean());
        Assert.True(root.GetProperty("escapePrevented").GetBoolean());
        Assert.True(root.GetProperty("viewSummaryFocused").GetBoolean());
        Assert.Equal("pan", root.GetProperty("activeToolAfterEscape").GetString());
        Assert.Equal(42, root.GetProperty("selectedAfterEscape").GetProperty("nodeId").GetInt32());
    }

    [Fact]
    public void AuthoringPaletteOpensPinsPersistsAndClosesOnEscape()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const paletteClasses = new Set();
              authoringPalette.classList = {
                add(name) { paletteClasses.add(name); },
                remove(name) { paletteClasses.delete(name); },
                contains(name) { return paletteClasses.has(name); },
                toggle(name, force) {
                  const enabled = force === undefined ? !paletteClasses.has(name) : Boolean(force);
                  if (enabled) paletteClasses.add(name);
                  else paletteClasses.delete(name);
                  return enabled;
                }
              };
              const handleClasses = new Set();
              authoringPaletteHandle.classList = {
                add(name) { handleClasses.add(name); },
                remove(name) { handleClasses.delete(name); },
                contains(name) { return handleClasses.has(name); },
                toggle(name, force) {
                  const enabled = force === undefined ? !handleClasses.has(name) : Boolean(force);
                  if (enabled) handleClasses.add(name);
                  else handleClasses.delete(name);
                  return enabled;
                }
              };
              authoringPaletteContent.contains = target =>
                [authoringPalettePin, addStepBtn, addPhaseBtn, connectBtn,
                 selectToolBtn, rectangleSelectToolBtn, panToolBtn].includes(target);

              let handleFocused = false;
              let pinBlurred = false;
              authoringPaletteHandle.focus = () => {
                handleFocused = true;
                document.activeElement = authoringPaletteHandle;
              };
              selectToolBtn.focus = () => { document.activeElement = selectToolBtn; };
              authoringPalettePin.blur = () => {
                pinBlurred = true;
                document.activeElement = null;
              };

              setActiveTool('select');
              const initial = {
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed')
              };

              authoringPaletteHandle.dispatchEvent({
                type: 'pointerenter', target: authoringPaletteHandle, bubbles: false
              });
              const hoverOpened = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded')
              };
              authoringPaletteHandle.dispatchEvent({
                type: 'pointerleave', target: authoringPaletteHandle, bubbles: false
              });
              const hoverStayedOpen = paletteClasses.has('is-open');
              selectToolBtn.onclick();
              const hoverActionClosed = !paletteClasses.has('is-open');

              authoringPaletteHandle.onclick();
              const opened = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                focusedTool: document.activeElement === selectToolBtn
              };

              document.activeElement = authoringPalettePin;
              authoringPalettePin.onclick();
              const pinned = {
                pinnedClass: paletteClasses.has('is-pinned'),
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed'),
                stored: localStorage.getItem('flowbit.authoringPalettePinned')
              };

              const nodesBeforePinnedAction = model.flowNodes.length;
              addStepBtn.onclick();
              const pinnedAction = {
                stillPinned: paletteClasses.has('is-pinned'),
                nodeAdded: model.flowNodes.length === nodesBeforePinnedAction + 1
              };

              document.activeElement = authoringPalettePin;
              authoringPalettePin.onclick();
              const unpinned = {
                pinnedClass: paletteClasses.has('is-pinned'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed'),
                stored: localStorage.getItem('flowbit.authoringPalettePinned'),
                pinBlurred
              };

              setConnectMode(true);
              const connectHandle = {
                active: handleClasses.has('active'),
                label: authoringPaletteHandle.getAttribute('aria-label')
              };
              setConnectMode(false);

              setActiveTool('pan');
              selected = { kind: 'node', nodeId: 42 };
              setAuthoringPaletteOpen(true);
              document.activeElement = selectToolBtn;
              document.getElementById('validation-modal').style.display = 'none';
              const escape = {
                type: 'keydown',
                key: 'Escape',
                code: 'Escape',
                target: selectToolBtn,
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(escape);
              const afterEscape = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                handleFocused,
                prevented: escape.defaultPrevented,
                activeTool,
                selected
              };

              return JSON.stringify({
                initial,
                hoverOpened,
                hoverStayedOpen,
                hoverActionClosed,
                opened,
                pinned,
                pinnedAction,
                unpinned,
                connectHandle,
                afterEscape
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("false", root.GetProperty("initial").GetProperty("expanded").GetString());
        Assert.Equal("false", root.GetProperty("initial").GetProperty("pinPressed").GetString());

        var hoverOpened = root.GetProperty("hoverOpened");
        Assert.True(hoverOpened.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", hoverOpened.GetProperty("expanded").GetString());
        Assert.True(root.GetProperty("hoverStayedOpen").GetBoolean());
        Assert.True(root.GetProperty("hoverActionClosed").GetBoolean());

        var opened = root.GetProperty("opened");
        Assert.True(opened.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", opened.GetProperty("expanded").GetString());
        Assert.True(opened.GetProperty("focusedTool").GetBoolean());

        var pinned = root.GetProperty("pinned");
        Assert.True(pinned.GetProperty("pinnedClass").GetBoolean());
        Assert.False(pinned.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", pinned.GetProperty("expanded").GetString());
        Assert.Equal("true", pinned.GetProperty("pinPressed").GetString());
        Assert.Equal("true", pinned.GetProperty("stored").GetString());
        Assert.True(root.GetProperty("pinnedAction").GetProperty("stillPinned").GetBoolean());
        Assert.True(root.GetProperty("pinnedAction").GetProperty("nodeAdded").GetBoolean());

        var unpinned = root.GetProperty("unpinned");
        Assert.False(unpinned.GetProperty("pinnedClass").GetBoolean());
        Assert.Equal("false", unpinned.GetProperty("expanded").GetString());
        Assert.Equal("false", unpinned.GetProperty("pinPressed").GetString());
        Assert.Equal("false", unpinned.GetProperty("stored").GetString());
        Assert.True(unpinned.GetProperty("pinBlurred").GetBoolean());

        var connectHandle = root.GetProperty("connectHandle");
        Assert.True(connectHandle.GetProperty("active").GetBoolean());
        Assert.Equal(
            "Show authoring tools; connect flow mode active",
            connectHandle.GetProperty("label").GetString());

        var afterEscape = root.GetProperty("afterEscape");
        Assert.False(afterEscape.GetProperty("openClass").GetBoolean());
        Assert.Equal("false", afterEscape.GetProperty("expanded").GetString());
        Assert.True(afterEscape.GetProperty("handleFocused").GetBoolean());
        Assert.True(afterEscape.GetProperty("prevented").GetBoolean());
        Assert.Equal("pan", afterEscape.GetProperty("activeTool").GetString());
        Assert.Equal(42, afterEscape.GetProperty("selected").GetProperty("nodeId").GetInt32());

        var restoredEngine = CreateEditorEngine(
            "localStorage.setItem('flowbit.authoringPalettePinned', 'true');");
        Assert.Equal(
            "true",
            restoredEngine.Evaluate(
                "authoringPaletteHandle.getAttribute('aria-expanded')").AsString());
        Assert.Equal(
            "true",
            restoredEngine.Evaluate(
                "authoringPalettePin.getAttribute('aria-pressed')").AsString());
    }

    [Fact]
    public void RectangleSelectToolSelectsIntersectingNodesAndMovesThemAsOneUndoableGroup()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'rectangle-select-smoke',
                name: 'Rectangle select smoke',
                initialEventId: null,
                variables: [],
                lanes: [
                  { id: 10, name: 'Left', x: 0, y: 0, w: 300, h: 300 },
                  { id: 20, name: 'Right', x: 300, y: 0, w: 400, h: 300 }
                ],
                flowNodes: [
                  { id: 1, name: 'One', type: NODE_TYPE.TASK, laneId: null, x: 100, y: 100 },
                  { id: 2, name: 'Two', type: NODE_TYPE.TASK, laneId: null, x: 350, y: 100 },
                  { id: 3, name: 'Outside', type: NODE_TYPE.TASK, laneId: null, x: 800, y: 500 }
                ],
                sequenceFlows: []
              };
              viewState = { x: 25, y: 35, zoom: 1.5 };
              resetHistory();
              setActiveTool('rectangle');

              const viewBefore = JSON.parse(JSON.stringify(viewState));
              svg.dispatchEvent(fakePointerEvent(
                'pointerdown', gridSurface, 301, 80, 80));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 301, 600, 250));
              const previewDrawn = rectangleSelection !== null &&
                selectionOverlayG.children.length > 0;
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 301, 600, 250));

              const afterSelection = {
                ids: [...selectedNodeIds],
                primary: selected,
                previewCleared: rectangleSelection === null && selectionOverlayG.innerHTML === '',
                viewUnchanged: JSON.stringify(viewState) === JSON.stringify(viewBefore),
                canvasPanStopped: canvasPan === null
              };

              onNodePointerDown(
                fakePointerEvent('pointerdown', fakeElement('g'), 302, 110, 110),
                getNode(1));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 302, 160, 150));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 302, 160, 150));
              commitHistory();
              const moved = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));
              const oneHistoryEntry = undoHistory.length === 1;

              undo();
              const undone = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));
              redo();
              const redone = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));

              return JSON.stringify({
                activeTool,
                svgTool: svg.dataset.activeTool,
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed'),
                handleTool: authoringPaletteHandle.dataset.activeTool,
                handleLabel: authoringPaletteHandle.getAttribute('aria-label'),
                hintHidden: hintEl.hidden,
                hintText: hintEl.innerHTML,
                previewDrawn,
                afterSelection,
                moved,
                oneHistoryEntry,
                undone,
                redone,
                selectedIdsAfterRedo: [...selectedNodeIds]
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("rectangle", root.GetProperty("activeTool").GetString());
        Assert.Equal("rectangle", root.GetProperty("svgTool").GetString());
        Assert.Equal("false", root.GetProperty("selectPressed").GetString());
        Assert.Equal("true", root.GetProperty("rectanglePressed").GetString());
        Assert.Equal("false", root.GetProperty("panPressed").GetString());
        Assert.Equal("rectangle", root.GetProperty("handleTool").GetString());
        Assert.Contains("Rectangle select", root.GetProperty("handleLabel").GetString());
        Assert.True(root.GetProperty("hintHidden").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("hintText").GetString());
        Assert.True(root.GetProperty("previewDrawn").GetBoolean());

        var selection = root.GetProperty("afterSelection");
        Assert.Equal(new[] { 1, 2 }, selection.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(1, selection.GetProperty("primary").GetProperty("nodeId").GetInt32());
        Assert.True(selection.GetProperty("previewCleared").GetBoolean());
        Assert.True(selection.GetProperty("viewUnchanged").GetBoolean());
        Assert.True(selection.GetProperty("canvasPanStopped").GetBoolean());

        var moved = root.GetProperty("moved").EnumerateArray().ToArray();
        Assert.Equal((150, 140, 10), (
            moved[0].GetProperty("x").GetInt32(),
            moved[0].GetProperty("y").GetInt32(),
            moved[0].GetProperty("laneId").GetInt32()));
        Assert.Equal((400, 140, 20), (
            moved[1].GetProperty("x").GetInt32(),
            moved[1].GetProperty("y").GetInt32(),
            moved[1].GetProperty("laneId").GetInt32()));
        Assert.Equal((800, 500), (
            moved[2].GetProperty("x").GetInt32(),
            moved[2].GetProperty("y").GetInt32()));
        Assert.True(root.GetProperty("oneHistoryEntry").GetBoolean());

        var undone = root.GetProperty("undone").EnumerateArray().ToArray();
        Assert.Equal((100, 100), (
            undone[0].GetProperty("x").GetInt32(),
            undone[0].GetProperty("y").GetInt32()));
        Assert.Equal((350, 100), (
            undone[1].GetProperty("x").GetInt32(),
            undone[1].GetProperty("y").GetInt32()));
        Assert.Equal(
            root.GetProperty("moved").GetRawText(),
            root.GetProperty("redone").GetRawText());
        Assert.Equal(new[] { 1, 2 }, root.GetProperty("selectedIdsAfterRedo")
            .EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public void RectangleSelectShortcutAndGridSnapPreserveGroupSpacing()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const shortcut = {
                type: 'keydown',
                key: 'r',
                code: 'KeyR',
                target: fakeElement('div'),
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              setActiveTool('pan');
              window.dispatchEvent(shortcut);
              const afterShortcut = {
                activeTool,
                prevented: shortcut.defaultPrevented === true,
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed')
              };

              const inputShortcut = {
                ...shortcut,
                target: fakeElement('input'),
                defaultPrevented: false
              };
              setActiveTool('pan');
              window.dispatchEvent(inputShortcut);
              const editableIgnored = activeTool === 'pan' && !inputShortcut.defaultPrevented;

              model = {
                id: 'rectangle-grid-smoke',
                name: 'Rectangle grid smoke',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Anchor', type: NODE_TYPE.TASK, laneId: null, x: 101, y: 109 },
                  { id: 2, name: 'Follower', type: NODE_TYPE.TASK, laneId: null, x: 349, y: 137 }
                ],
                sequenceFlows: []
              };
              setGridSize(24, false);
              setSnapToGrid(true, false);
              selectNodes([1, 2], 1);
              setActiveTool('rectangle');
              onNodePointerDown(
                fakePointerEvent('pointerdown', fakeElement('g'), 311, 111, 119),
                getNode(1));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 311, 161, 160));
              const moved = model.flowNodes.map(node => ({ x: node.x, y: node.y }));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 311, 161, 160));

              return JSON.stringify({
                afterShortcut,
                editableIgnored,
                moved,
                spacing: {
                  x: model.flowNodes[1].x - model.flowNodes[0].x,
                  y: model.flowNodes[1].y - model.flowNodes[0].y
                }
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var shortcut = root.GetProperty("afterShortcut");
        Assert.Equal("rectangle", shortcut.GetProperty("activeTool").GetString());
        Assert.True(shortcut.GetProperty("prevented").GetBoolean());
        Assert.Equal("true", shortcut.GetProperty("rectanglePressed").GetString());
        Assert.True(root.GetProperty("editableIgnored").GetBoolean());

        var moved = root.GetProperty("moved").EnumerateArray().ToArray();
        Assert.Equal((144, 144), (
            moved[0].GetProperty("x").GetInt32(),
            moved[0].GetProperty("y").GetInt32()));
        Assert.Equal((392, 172), (
            moved[1].GetProperty("x").GetInt32(),
            moved[1].GetProperty("y").GetInt32()));
        Assert.Equal(248, root.GetProperty("spacing").GetProperty("x").GetInt32());
        Assert.Equal(28, root.GetProperty("spacing").GetProperty("y").GetInt32());
    }

    [Fact]
    public void PanToolMovesOnlyTheViewportAndPreservesWorkflowInteractionState()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-tool-smoke',
                name: 'Pan tool smoke',
                initialEventId: 1,
                variables: [],
                lanes: [{ id: 10, name: 'Operations', x: 40, y: 50, w: 700, h: 280 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 120,
                  y: 130
                }],
                sequenceFlows: []
              };
              selected = { kind: 'node', nodeId: 1 };
              connectMode = true;
              connectSource = 1;
              viewState = { x: 20, y: 30, zoom: 2 };
              const before = JSON.parse(JSON.stringify({
                lanes: model.lanes,
                flowNodes: model.flowNodes,
                selected
              }));

              setActiveTool('pan');
              const target = fakeElement('g');
              target.parentNode = svg;
              let targetClickReached = false;
              target.addEventListener('pointerdown', event => onNodePointerDown(event, model.flowNodes[0]));
              target.addEventListener('click', () => { targetClickReached = true; });
              target.dispatchEvent(fakePointerEvent('pointerdown', target, 101, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', svg, 101, 260, 200));
              svg.dispatchEvent(fakePointerEvent('pointerup', svg, 101, 260, 200));
              const click = fakePointerEvent('click', target, 101, 260, 200);
              target.dispatchEvent(click);

              return JSON.stringify({
                before,
                after: {
                  lanes: model.lanes,
                  flowNodes: model.flowNodes,
                  selected
                },
                viewState,
                activeTool,
                svgActiveTool: svg.dataset.activeTool,
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed'),
                connectMode,
                connectSource,
                hintHidden: hintEl.hidden,
                hintText: hintEl.innerHTML,
                panFinished: canvasPan === null,
                itemDragBlocked: drag === null,
                clickPrevented: click.defaultPrevented,
                targetClickReached
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("pan", root.GetProperty("activeTool").GetString());
        Assert.Equal("pan", root.GetProperty("svgActiveTool").GetString());
        Assert.Equal("false", root.GetProperty("selectPressed").GetString());
        Assert.Equal("true", root.GetProperty("panPressed").GetString());
        Assert.False(root.GetProperty("connectMode").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("connectSource").ValueKind);
        Assert.True(root.GetProperty("hintHidden").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("hintText").GetString());
        Assert.True(root.GetProperty("panFinished").GetBoolean());
        Assert.True(root.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(root.GetProperty("clickPrevented").GetBoolean());
        Assert.False(root.GetProperty("targetClickReached").GetBoolean());
        Assert.Equal(
            root.GetProperty("before").GetRawText(),
            root.GetProperty("after").GetRawText());

        var viewState = root.GetProperty("viewState");
        Assert.Equal(-10, viewState.GetProperty("x").GetDouble());
        Assert.Equal(10, viewState.GetProperty("y").GetDouble());
        Assert.Equal(2, viewState.GetProperty("zoom").GetDouble());
    }

    [Fact]
    public void PanToolInspectsNodesAndFlowsWithoutMovingOrDeletingThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-inspection-smoke',
                name: 'Pan inspection smoke',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Review',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 120,
                    y: 130
                  },
                  {
                    id: 2,
                    name: 'Archive',
                    type: NODE_TYPE.TASK,
                    laneId: null,
                    x: 360,
                    y: 130
                  }
                ],
                sequenceFlows: [{
                  id: 101,
                  name: 'Approved',
                  sourceRef: 1,
                  targetRef: 2,
                  attributes: [],
                  roles: [],
                  variables: []
                }]
              };
              selected = null;
              selectedNodeIds.clear();
              viewState = { x: 20, y: 30, zoom: 2 };
              render();
              setActiveTool('pan');

              const descendants = root => [
                root,
                ...(root.children || []).flatMap(descendants)
              ];
              const latestInspectorButton = text => descendants(inspector)
                .filter(element => element.textContent === text)
                .at(-1);
              const panTarget = (kind, id) => {
                const group = fakeElement('g');
                const target = fakeElement(kind === 'node' ? 'rect' : 'path');
                group.parentNode = svg;
                target.parentNode = group;
                if (kind === 'node') group.dataset.id = String(id);
                else group.dataset.flow = String(id);
                target.closest = selector => {
                  if (kind === 'node' && selector === '.node[data-id]') return group;
                  if (kind === 'flow' && selector === '.edge[data-flow]') return group;
                  return null;
                };
                return target;
              };
              const stationaryPanClick = (target, pointerId, x, y, jitter = false) => {
                target.dispatchEvent(fakePointerEvent(
                  'pointerdown', target, pointerId, x, y));
                const endX = x + (jitter ? 2 : 0);
                const endY = y + (jitter ? 1 : 0);
                if (jitter) {
                  svg.dispatchEvent(fakePointerEvent(
                    'pointermove', svg, pointerId, endX, endY));
                }
                svg.dispatchEvent(fakePointerEvent(
                  'pointerup', svg, pointerId, endX, endY));
                const click = fakePointerEvent('click', target, pointerId, endX, endY);
                target.dispatchEvent(click);
              };
              const pressDelete = () => {
                const event = {
                  type: 'keydown',
                  key: 'Delete',
                  code: 'Delete',
                  target: document.body,
                  ctrlKey: false,
                  metaKey: false,
                  altKey: false,
                  shiftKey: false,
                  defaultPrevented: false,
                  preventDefault() { this.defaultPrevented = true; }
                };
                window.dispatchEvent(event);
                return event;
              };

              setActiveTool('select');
              select('flow', 101);
              const preexistingFlowDeleteButton = latestInspectorButton('Delete flow');
              const beforeModeSwitchDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const enabledInSelect = !preexistingFlowDeleteButton.disabled;
              setActiveTool('pan');
              const disabledAfterSwitchToPan = preexistingFlowDeleteButton.disabled;
              preexistingFlowDeleteButton.onclick();
              const modeSwitchProtection = {
                enabledInSelect,
                disabledAfterSwitchToPan,
                deletionProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeModeSwitchDelete
              };

              const nodeTarget = panTarget('node', 2);
              stationaryPanClick(nodeTarget, 201, 400, 180, true);
              const nodeDeleteButton = latestInspectorButton('Delete node');
              const beforeNodeDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              nodeDeleteButton.onclick();
              pressDelete();
              const nodeInspection = {
                selected,
                inspectorShowsNode: inspector.innerHTML.includes('<h2>Node #2</h2>'),
                deleteDisabled: nodeDeleteButton.disabled,
                deleteProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeNodeDelete
              };

              const flowTarget = panTarget('flow', 101);
              stationaryPanClick(flowTarget, 202, 280, 180);
              const flowDeleteButton = latestInspectorButton('Delete flow');
              const beforeFlowDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              flowDeleteButton.onclick();
              pressDelete();
              const flowInspection = {
                selected,
                inspectorShowsFlow: inspector.innerHTML.includes('<h2>Flow #101</h2>'),
                deleteDisabled: flowDeleteButton.disabled,
                deleteProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeFlowDelete
              };

              const beforeDrag = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const viewBeforeDrag = JSON.stringify(viewState);
              nodeTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', nodeTarget, 203, 400, 180));
              [1, 2, 3, 4, 5].forEach(offset => {
                svg.dispatchEvent(fakePointerEvent(
                  'pointermove', svg, 203, 400 + offset, 180));
              });
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 203, 405, 180));
              const dragClick = fakePointerEvent('click', nodeTarget, 203, 405, 180);
              nodeTarget.dispatchEvent(dragClick);
              const dragResult = {
                modelAndSelectionPreserved: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeDrag,
                itemDragBlocked: drag === null,
                clickPrevented: dragClick.defaultPrevented
              };

              const beforeFlowDrag = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const viewBeforeFlowDrag = JSON.stringify(viewState);
              flowTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', flowTarget, 204, 280, 180));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 204, 330, 220));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 204, 330, 220));
              const flowDragClick = fakePointerEvent('click', flowTarget, 204, 330, 220);
              flowTarget.dispatchEvent(flowDragClick);
              const flowDragResult = {
                modelAndSelectionPreserved: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeFlowDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeFlowDrag,
                itemDragBlocked: drag === null,
                clickPrevented: flowDragClick.defaultPrevented
              };

              setActiveTool('select');
              const selectModeDeleteEnabled = !flowDeleteButton.disabled;
              flowDeleteButton.onclick();
              const selectModeDeletion = {
                flowRemoved: model.sequenceFlows.length === 0,
                sourceSelected: selected?.kind === 'node' && selected.nodeId === 1
              };

              return JSON.stringify({
                activeTool,
                modeSwitchProtection,
                nodeInspection,
                flowInspection,
                dragResult,
                flowDragResult,
                selectModeDeleteEnabled,
                selectModeDeletion,
                nodeCount: model.flowNodes.length,
                flowCount: model.sequenceFlows.length
              });
            })()
            """).AsString());

        var root = result.RootElement;

        var modeSwitchProtection = root.GetProperty("modeSwitchProtection");
        Assert.True(modeSwitchProtection.GetProperty("enabledInSelect").GetBoolean());
        Assert.True(modeSwitchProtection.GetProperty("disabledAfterSwitchToPan").GetBoolean());
        Assert.True(modeSwitchProtection.GetProperty("deletionProtected").GetBoolean());

        var nodeInspection = root.GetProperty("nodeInspection");
        Assert.Equal("node", nodeInspection.GetProperty("selected").GetProperty("kind").GetString());
        Assert.Equal(2, nodeInspection.GetProperty("selected").GetProperty("nodeId").GetInt32());
        Assert.True(nodeInspection.GetProperty("inspectorShowsNode").GetBoolean());
        Assert.True(nodeInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(nodeInspection.GetProperty("deleteProtected").GetBoolean());

        var flowInspection = root.GetProperty("flowInspection");
        Assert.Equal("flow", flowInspection.GetProperty("selected").GetProperty("kind").GetString());
        Assert.Equal(101, flowInspection.GetProperty("selected").GetProperty("flowId").GetInt32());
        Assert.True(flowInspection.GetProperty("inspectorShowsFlow").GetBoolean());
        Assert.True(flowInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(flowInspection.GetProperty("deleteProtected").GetBoolean());

        var dragResult = root.GetProperty("dragResult");
        Assert.True(dragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(dragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(dragResult.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(dragResult.GetProperty("clickPrevented").GetBoolean());

        var flowDragResult = root.GetProperty("flowDragResult");
        Assert.True(flowDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(flowDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(flowDragResult.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(flowDragResult.GetProperty("clickPrevented").GetBoolean());

        Assert.Equal("select", root.GetProperty("activeTool").GetString());
        Assert.True(root.GetProperty("selectModeDeleteEnabled").GetBoolean());
        Assert.True(root.GetProperty("selectModeDeletion").GetProperty("flowRemoved").GetBoolean());
        Assert.True(root.GetProperty("selectModeDeletion").GetProperty("sourceSelected").GetBoolean());
        Assert.Equal(2, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, root.GetProperty("flowCount").GetInt32());
    }

    [Fact]
    public void PanToolInspectsLanesAndWorkflowWithoutMovingOrDeletingThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-lane-workflow-smoke',
                name: 'Pan lane and workflow smoke',
                initialEventId: null,
                variables: [],
                cancelRoles: [],
                unclaimRoles: [],
                taskAssignmentRoles: [],
                lanes: [{ id: 10, name: 'Operations', x: 80, y: 90, w: 640, h: 260 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 180,
                  y: 150
                }],
                sequenceFlows: []
              };
              selected = { kind: 'node', nodeId: 1 };
              selectedNodeIds = new Set([1]);
              viewState = { x: 15, y: 25, zoom: 1.5 };
              render();
              setActiveTool('pan');

              const laneGroup = fakeElement('g');
              laneGroup.dataset.id = '10';
              laneGroup.parentNode = svg;
              const laneTarget = fakeElement('rect');
              laneTarget.parentNode = laneGroup;
              laneTarget.closest = selector =>
                selector === '.lane[data-id]' ? laneGroup : null;
              laneGroup.addEventListener('pointerdown', event =>
                onLanePointerDown(event, getLane(10)));

              const workflowTarget = fakeElement('rect');
              workflowTarget.parentNode = svg;
              workflowTarget.closest = () => null;

              const stationaryPanClick = (target, pointerId, x, y, jitter = false) => {
                target.dispatchEvent(fakePointerEvent(
                  'pointerdown', target, pointerId, x, y));
                const endX = x + (jitter ? 2 : 0);
                const endY = y + (jitter ? 1 : 0);
                if (jitter) {
                  svg.dispatchEvent(fakePointerEvent(
                    'pointermove', svg, pointerId, endX, endY));
                }
                svg.dispatchEvent(fakePointerEvent(
                  'pointerup', svg, pointerId, endX, endY));
                target.dispatchEvent(fakePointerEvent(
                  'click', target, pointerId, endX, endY));
              };
              const pressDelete = () => {
                window.dispatchEvent({
                  type: 'keydown',
                  key: 'Delete',
                  code: 'Delete',
                  target: document.body,
                  ctrlKey: false,
                  metaKey: false,
                  altKey: false,
                  shiftKey: false,
                  defaultPrevented: false,
                  preventDefault() { this.defaultPrevented = true; }
                });
              };

              const modelBeforeLaneClick = JSON.stringify(model);
              const viewBeforeLaneClick = JSON.stringify(viewState);
              stationaryPanClick(laneTarget, 301, 260, 180, true);
              const laneDeleteButton = inspectorItemDeleteButtons.at(-1);
              const laneInspection = {
                selectedKind: selected?.kind,
                selectedId: selected?.laneId,
                inspectorShowsLane: inspector.innerHTML.includes('<h2>Lane #10</h2>'),
                deleteDisabled: laneDeleteButton?.disabled === true,
                modelPreserved: JSON.stringify(model) === modelBeforeLaneClick,
                viewportPreserved: JSON.stringify(viewState) === viewBeforeLaneClick,
                itemGesturesBlocked: laneDrag === null && laneResize === null
              };

              const beforeLaneDelete = JSON.stringify({ model, selected });
              laneDeleteButton.onclick();
              pressDelete();
              const laneDeletionProtected =
                JSON.stringify({ model, selected }) === beforeLaneDelete;

              const beforeLaneDrag = JSON.stringify({ model, selected });
              const viewBeforeLaneDrag = JSON.stringify(viewState);
              laneTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', laneTarget, 302, 260, 180));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 302, 315, 220));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 302, 315, 220));
              const laneDragClick = fakePointerEvent(
                'click', laneTarget, 302, 315, 220);
              laneTarget.dispatchEvent(laneDragClick);
              const laneDragResult = {
                modelAndSelectionPreserved:
                  JSON.stringify({ model, selected }) === beforeLaneDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeLaneDrag,
                itemGesturesBlocked: laneDrag === null && laneResize === null,
                clickPrevented: laneDragClick.defaultPrevented
              };

              const beforeWorkflowDrag = JSON.stringify({ model, selected });
              const viewBeforeWorkflowDrag = JSON.stringify(viewState);
              workflowTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', workflowTarget, 303, 800, 520));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 303, 850, 555));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 303, 850, 555));
              const workflowDragClick = fakePointerEvent(
                'click', workflowTarget, 303, 850, 555);
              workflowTarget.dispatchEvent(workflowDragClick);
              const workflowDragResult = {
                modelAndSelectionPreserved:
                  JSON.stringify({ model, selected }) === beforeWorkflowDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeWorkflowDrag,
                clickPrevented: workflowDragClick.defaultPrevented
              };

              const modelBeforeWorkflowClick = JSON.stringify(model);
              const viewBeforeWorkflowClick = JSON.stringify(viewState);
              stationaryPanClick(workflowTarget, 304, 800, 520, true);
              const workflowInspection = {
                selectedCleared: selected === null && selectedNodeIds.size === 0,
                inspectorShowsWorkflow:
                  inspector.innerHTML.includes('<h2>Workflow</h2>'),
                inspectorMentionsLanes:
                  inspector.innerHTML.includes('node, flow, or lane'),
                viewportPreserved:
                  JSON.stringify(viewState) === viewBeforeWorkflowClick,
                modelPreserved: JSON.stringify(model) === modelBeforeWorkflowClick,
                noItemDeleteButtons: inspectorItemDeleteButtons.length === 0
              };
              pressDelete();

              return JSON.stringify({
                activeTool,
                laneInspection,
                laneDeletionProtected,
                laneDragResult,
                workflowDragResult,
                workflowInspection,
                workflowDeleteProtected:
                  selected === null && JSON.stringify(model) === modelBeforeWorkflowClick,
                laneCount: model.lanes.length,
                nodeCount: model.flowNodes.length
              });
            })()
            """).AsString());

        var root = result.RootElement;

        Assert.Equal("pan", root.GetProperty("activeTool").GetString());

        var laneInspection = root.GetProperty("laneInspection");
        Assert.Equal("lane", laneInspection.GetProperty("selectedKind").GetString());
        Assert.Equal(10, laneInspection.GetProperty("selectedId").GetInt32());
        Assert.True(laneInspection.GetProperty("inspectorShowsLane").GetBoolean());
        Assert.True(laneInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(laneInspection.GetProperty("modelPreserved").GetBoolean());
        Assert.True(laneInspection.GetProperty("viewportPreserved").GetBoolean());
        Assert.True(laneInspection.GetProperty("itemGesturesBlocked").GetBoolean());
        Assert.True(root.GetProperty("laneDeletionProtected").GetBoolean());

        var laneDragResult = root.GetProperty("laneDragResult");
        Assert.True(laneDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(laneDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(laneDragResult.GetProperty("itemGesturesBlocked").GetBoolean());
        Assert.True(laneDragResult.GetProperty("clickPrevented").GetBoolean());

        var workflowDragResult = root.GetProperty("workflowDragResult");
        Assert.True(workflowDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(workflowDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(workflowDragResult.GetProperty("clickPrevented").GetBoolean());

        var workflowInspection = root.GetProperty("workflowInspection");
        Assert.True(workflowInspection.GetProperty("selectedCleared").GetBoolean());
        Assert.True(workflowInspection.GetProperty("inspectorShowsWorkflow").GetBoolean());
        Assert.True(workflowInspection.GetProperty("inspectorMentionsLanes").GetBoolean());
        Assert.True(workflowInspection.GetProperty("viewportPreserved").GetBoolean());
        Assert.True(workflowInspection.GetProperty("modelPreserved").GetBoolean());
        Assert.True(workflowInspection.GetProperty("noItemDeleteButtons").GetBoolean());
        Assert.True(root.GetProperty("workflowDeleteProtected").GetBoolean());
        Assert.Equal(1, root.GetProperty("laneCount").GetInt32());
        Assert.Equal(1, root.GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public void SwitchingToolsFinishesItemAndCanvasGestures()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-tool-switch-smoke',
                name: 'Pan tool switch smoke',
                initialEventId: 1,
                variables: [],
                lanes: [{ id: 10, name: 'Operations', x: 40, y: 50, w: 700, h: 280 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 120,
                  y: 130
                }],
                sequenceFlows: []
              };
              let node = model.flowNodes[0];
              let lane = model.lanes[0];
              resetHistory();

              setActiveTool('select');
              onNodePointerDown(fakePointerEvent('pointerdown', fakeElement('g'), 201, 130, 140), node);
              const nodeDragStarted = drag !== null;
              svg.dispatchEvent(fakePointerEvent('pointermove', svg, 201, 180, 190));
              const nodeWasMoved = node.x === 170 && node.y === 180;
              setActiveTool('pan');
              const nodeDragStopped = drag === null;
              const itemMoveCommitted = undoHistory.length === 1;
              undo();
              const itemMoveUndoable = getNode(1).x === 120 && getNode(1).y === 130;
              node = getNode(1);
              lane = getLane(10);

              setActiveTool('select');
              onLanePointerDown(fakePointerEvent('pointerdown', fakeElement('g'), 202, 50, 60), lane);
              const laneDragStarted = laneDrag !== null;
              setActiveTool('pan');
              const laneDragStopped = laneDrag === null;

              setActiveTool('select');
              onLaneResizePointerDown(fakePointerEvent('pointerdown', fakeElement('rect'), 203, 740, 330), lane);
              const laneResizeStarted = laneResize !== null;
              setActiveTool('pan');
              const laneResizeStopped = laneResize === null;

              viewState = { x: 0, y: 0, zoom: 1 };
              const target = fakeElement('g');
              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 204, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 204, 240, 190));
              const viewBeforeSelect = JSON.parse(JSON.stringify(viewState));
              setActiveTool('select');
              const canvasPanStopped = canvasPan === null;
              const toolSwitchSuppressCleared = suppressCanvasClick === false;
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 204, 300, 240));
              const viewAfterSelect = JSON.parse(JSON.stringify(viewState));

              setActiveTool('pan');
              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 205, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 205, 240, 190));
              window.dispatchEvent({ type: 'blur' });
              const blurCanvasPanStopped = canvasPan === null;
              const blurSuppressCleared = suppressCanvasClick === false;

              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 206, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 206, 240, 190));
              svg.dispatchEvent(fakePointerEvent(
                'lostpointercapture', svg, 206, 240, 190));
              const lostCaptureCanvasPanStopped = canvasPan === null;
              const lostCaptureSuppressCleared = suppressCanvasClick === false;
              setActiveTool('select');

              return JSON.stringify({
                nodeDragStarted,
                nodeWasMoved,
                nodeDragStopped,
                itemMoveCommitted,
                itemMoveUndoable,
                laneDragStarted,
                laneDragStopped,
                laneResizeStarted,
                laneResizeStopped,
                canvasPanStopped,
                toolSwitchSuppressCleared,
                blurCanvasPanStopped,
                blurSuppressCleared,
                lostCaptureCanvasPanStopped,
                lostCaptureSuppressCleared,
                viewBeforeSelect,
                viewAfterSelect,
                activeTool
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("nodeDragStarted").GetBoolean());
        Assert.True(root.GetProperty("nodeWasMoved").GetBoolean());
        Assert.True(root.GetProperty("nodeDragStopped").GetBoolean());
        Assert.True(root.GetProperty("itemMoveCommitted").GetBoolean());
        Assert.True(root.GetProperty("itemMoveUndoable").GetBoolean());
        Assert.True(root.GetProperty("laneDragStarted").GetBoolean());
        Assert.True(root.GetProperty("laneDragStopped").GetBoolean());
        Assert.True(root.GetProperty("laneResizeStarted").GetBoolean());
        Assert.True(root.GetProperty("laneResizeStopped").GetBoolean());
        Assert.True(root.GetProperty("canvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("toolSwitchSuppressCleared").GetBoolean());
        Assert.True(root.GetProperty("blurCanvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("blurSuppressCleared").GetBoolean());
        Assert.True(root.GetProperty("lostCaptureCanvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("lostCaptureSuppressCleared").GetBoolean());
        Assert.Equal("select", root.GetProperty("activeTool").GetString());
        Assert.Equal(
            root.GetProperty("viewBeforeSelect").GetRawText(),
            root.GetProperty("viewAfterSelect").GetRawText());
    }

    [Fact]
    public void NodeDragSnapsToTheConfiguredGridOnlyWhenEnabled()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'grid-snap-smoke',
                name: 'Grid snap smoke',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: null,
                  x: 120,
                  y: 130,
                  attributes: [],
                  roles: [],
                  variables: []
                }],
                sequenceFlows: []
              };
              setActiveTool('select');

              const moveFromOrigin = pointerId => {
                const node = getNode(1);
                node.x = 120;
                node.y = 130;
                onNodePointerDown(
                  fakePointerEvent('pointerdown', fakeElement('g'), pointerId, 130, 140),
                  node);
                svg.dispatchEvent(
                  fakePointerEvent('pointermove', svg, pointerId, 187, 203));
                const position = { x: node.x, y: node.y };
                svg.dispatchEvent(
                  fakePointerEvent('pointerup', svg, pointerId, 187, 203));
                return position;
              };

              snapToGridInput.checked = false;
              snapToGridInput.dispatchEvent({
                type: 'change', target: snapToGridInput, bubbles: false
              });
              const free = moveFromOrigin(401);

              gridSizeInput.value = '20';
              gridSizeInput.dispatchEvent({
                type: 'change', target: gridSizeInput, bubbles: false
              });
              snapToGridInput.checked = true;
              snapToGridInput.dispatchEvent({
                type: 'change', target: snapToGridInput, bubbles: false
              });
              const snapped20 = moveFromOrigin(402);

              gridSizeInput.value = '25';
              gridSizeInput.dispatchEvent({
                type: 'change', target: gridSizeInput, bubbles: false
              });
              const snapped25 = moveFromOrigin(403);

              return JSON.stringify({
                free,
                snapped20,
                snapped25,
                snapEnabled: snapToGrid,
                configuredSize: gridSize,
                sizeInputValue: gridSizeInput.value,
                patternWidth: gridPattern.getAttribute('width'),
                patternHeight: gridPattern.getAttribute('height'),
                storedSnap: localStorage.getItem('flowbit.snapToGrid'),
                storedSize: localStorage.getItem('flowbit.gridSize'),
                dragCleared: drag === null
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(177, root.GetProperty("free").GetProperty("x").GetInt32());
        Assert.Equal(193, root.GetProperty("free").GetProperty("y").GetInt32());
        Assert.Equal(180, root.GetProperty("snapped20").GetProperty("x").GetInt32());
        Assert.Equal(200, root.GetProperty("snapped20").GetProperty("y").GetInt32());
        Assert.Equal(175, root.GetProperty("snapped25").GetProperty("x").GetInt32());
        Assert.Equal(200, root.GetProperty("snapped25").GetProperty("y").GetInt32());
        Assert.True(root.GetProperty("snapEnabled").GetBoolean());
        Assert.Equal(25, root.GetProperty("configuredSize").GetInt32());
        Assert.Equal("25", root.GetProperty("sizeInputValue").GetString());
        Assert.Equal("25", root.GetProperty("patternWidth").GetString());
        Assert.Equal("25", root.GetProperty("patternHeight").GetString());
        Assert.Equal("true", root.GetProperty("storedSnap").GetString());
        Assert.Equal("25", root.GetProperty("storedSize").GetString());
        Assert.True(root.GetProperty("dragCleared").GetBoolean());
    }

    [Fact]
    public void GridPreferencesRestoreOnEditorStartup()
    {
        var engine = CreateEditorEngine(
            """
            localStorage.setItem('flowbit.snapToGrid', 'true');
            localStorage.setItem('flowbit.gridSize', '36');
            """);

        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            JSON.stringify({
              snapToGrid,
              gridSize,
              checked: snapToGridInput.checked,
              inputValue: gridSizeInput.value,
              patternWidth: gridPattern.getAttribute('width'),
              patternHeight: gridPattern.getAttribute('height')
            })
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("snapToGrid").GetBoolean());
        Assert.Equal(36, root.GetProperty("gridSize").GetInt32());
        Assert.True(root.GetProperty("checked").GetBoolean());
        Assert.Equal("36", root.GetProperty("inputValue").GetString());
        Assert.Equal("36", root.GetProperty("patternWidth").GetString());
        Assert.Equal("36", root.GetProperty("patternHeight").GetString());
    }

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

    private static Engine CreateEditorEngine(string? beforeMainScript = null)
    {
        var html = ReadEditorSource();
        var scripts = Regex.Matches(
            html,
            @"<script(?:\s[^>]*)?>(?<code>[\s\S]*?)</script>");
        Assert.True(scripts.Count >= 2, "The editor's main inline script was not found.");

        var engine = new Engine();
        engine.Execute(DomStubs);
        if (!string.IsNullOrWhiteSpace(beforeMainScript)) engine.Execute(beforeMainScript);
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
            eventListeners: {},
            value: '',
            checked: false,
            disabled: false,
            hidden: false,
            textContent: '',
            innerHTML: '',
            clientWidth: 1000,
            clientHeight: 700,
            appendChild(child) {
              this.children.push(child);
              if (child) child.parentNode = this;
              return child;
            },
            replaceChildren(...children) {
              this.children = children;
              children.forEach(child => { if (child) child.parentNode = this; });
            },
            remove() {},
            addEventListener(type, listener, options) {
              const capture = options === true || !!(options && options.capture);
              (this.eventListeners[type] ||= []).push({ listener, capture });
            },
            dispatchEvent(event) {
              event.target ||= this;
              const path = [];
              for (let element = this; element; element = element.parentNode) path.push(element);
              const invoke = (element, capture) => {
                event.currentTarget = element;
                const listeners = element.eventListeners?.[event.type] || [];
                for (const entry of listeners) {
                  if (entry.capture !== capture) continue;
                  entry.listener.call(element, event);
                  if (event.immediatePropagationStopped) break;
                }
              };
              for (let index = path.length - 1;
                   index > 0 && !event.propagationStopped;
                   index--) invoke(path[index], true);
              if (!event.propagationStopped) {
                invoke(path[0], true);
                if (!event.immediatePropagationStopped) invoke(path[0], false);
              }
              if (event.bubbles !== false) {
                for (let index = 1;
                     index < path.length && !event.propagationStopped;
                     index++) invoke(path[index], false);
              }
              return !event.defaultPrevented;
            },
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
        function fakePointerEvent(type, target, pointerId, clientX, clientY, button = 0) {
          return {
            type,
            target,
            pointerId,
            clientX,
            clientY,
            button,
            bubbles: true,
            defaultPrevented: false,
            propagationStopped: false,
            immediatePropagationStopped: false,
            preventDefault() { this.defaultPrevented = true; },
            stopPropagation() { this.propagationStopped = true; },
            stopImmediatePropagation() {
              this.immediatePropagationStopped = true;
              this.propagationStopped = true;
            }
          };
        }
        const fakeElements = new Map();
        const documentEventListeners = {};
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
          addEventListener(type, listener, options) {
            const capture = options === true || !!(options && options.capture);
            (documentEventListeners[type] ||= []).push({ listener, capture });
          },
          dispatchEvent(event) {
            event.target ||= this;
            event.currentTarget = this;
            const listeners = documentEventListeners[event.type] || [];
            for (const capture of [true, false]) {
              for (const entry of listeners) {
                if (entry.capture === capture) entry.listener.call(this, event);
              }
            }
            return !event.defaultPrevented;
          }
        };
        const localStorageValues = new Map();
        const localStorage = {
          getItem(key) {
            return localStorageValues.has(String(key))
              ? localStorageValues.get(String(key))
              : null;
          },
          setItem(key, value) {
            localStorageValues.set(String(key), String(value));
          }
        };
        const fakeUrl = {
          createObjectURL() { return 'blob:test'; },
          revokeObjectURL() {}
        };
        const windowEventListeners = {};
        const window = {
          document,
          localStorage,
          URL: fakeUrl,
          innerWidth: 1400,
          innerHeight: 900,
          addEventListener(type, listener) {
            (windowEventListeners[type] ||= []).push(listener);
          },
          removeEventListener(type, listener) {
            const listeners = windowEventListeners[type] || [];
            const index = listeners.indexOf(listener);
            if (index >= 0) listeners.splice(index, 1);
          },
          dispatchEvent(event) {
            event.target ||= this;
            event.currentTarget = this;
            for (const listener of windowEventListeners[event.type] || []) {
              listener.call(this, event);
            }
            return !event.defaultPrevented;
          },
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
