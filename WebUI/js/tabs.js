// ======================================================================
//  SS14 Prototype Editor – Tabs & Editor Groups
// ======================================================================
//
//  Architecture: multiple editor groups arranged side-by-side (VS Code
//  style).  Each group has its own tab bar + content area.
//  state.openFiles is shared across all groups; file data is loaded once.
//  state.groups = [{ id, tabs: string[], activeTab: string|null }]
//  state.activeGroupId = id of the group with keyboard focus.

'use strict';

// ======================== GROUP ID COUNTER =============================
let _gidSeq = 1;
function _nextGid() { return 'g' + (++_gidSeq); }

// ======================== CURRENT DRAG SESSION =========================
let _dragData = null; // { groupId, path }

// Global safety net: if dragend never fires on the tab element (e.g. DOM
// replacement during drag), clean up the dragging state from the document.
document.addEventListener('dragend', () => {
    _dragData = null;
    document.getElementById('app')?.classList.remove('tab-dragging');
    document.querySelectorAll('.tab.dragging, .tab.drag-over-left, .tab.drag-over-right')
        .forEach(t => t.classList.remove('dragging', 'drag-over-left', 'drag-over-right'));
});

// ======================== GROUP RESIZER (global mouse handlers) =========
let _grResizing = false, _grResizer = null, _grStartX = 0, _grStartWidths = [];

document.addEventListener('mousedown', e => {
    const resizer = e.target.closest?.('.group-resizer');
    if (!resizer) return;
    const container = document.getElementById('editor-groups');
    if (!container) return;
    const groupEls = [...container.querySelectorAll('.editor-group')];
    _grResizing = true;
    _grResizer = resizer;
    _grStartX = e.clientX;
    _grStartWidths = groupEls.map(el => el.getBoundingClientRect().width);
    resizer.classList.add('dragging');
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    e.preventDefault();
});

document.addEventListener('mousemove', e => {
    if (!_grResizing || !_grResizer) return;
    const afterGroupId = _grResizer.dataset.afterGroup;
    const afterIdx = state.groups.findIndex(g => g.id === afterGroupId);
    if (afterIdx < 0 || afterIdx >= state.groups.length - 1) return;
    const container = document.getElementById('editor-groups');
    if (!container) return;
    const groupEls = [...container.querySelectorAll('.editor-group')];
    const containerW = container.getBoundingClientRect().width;
    if (!containerW) return;
    const delta = e.clientX - _grStartX;
    const w1 = Math.max(150, _grStartWidths[afterIdx] + delta);
    const w2 = Math.max(150, _grStartWidths[afterIdx + 1] - delta);
    // Store as percentages so groups reflow correctly when the container resizes
    // (e.g. sidebar collapse / window resize).
    if (groupEls[afterIdx])     groupEls[afterIdx].style.flex     = `0 0 ${(w1 / containerW * 100).toFixed(2)}%`;
    if (groupEls[afterIdx + 1]) groupEls[afterIdx + 1].style.flex = `0 0 ${(w2 / containerW * 100).toFixed(2)}%`;
});

document.addEventListener('mouseup', () => {
    if (!_grResizing) return;
    _grResizing = false;
    if (_grResizer) { _grResizer.classList.remove('dragging'); _grResizer = null; }
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
});

// ======================== RENDER ALL GROUPS ============================
// Full rebuild: used only when the group layout changes (groups added/removed).
function renderGroups() {
    const container = document.getElementById('editor-groups');
    if (!container) return;
    container.innerHTML = '';
    state.groups.forEach((group, i) => {
        if (i > 0) container.appendChild(_makeGroupResizer(state.groups[i - 1].id));
        container.appendChild(_makeGroupEl(group));
    });
    // Populate each group's content area after the DOM is ready.
    state.groups.forEach(group => renderEditor(group.id));
}

// Lightweight update: only re-renders tab bars (not content areas).
// Call this after tab metadata changes (modified dot, active tab, add/remove tab)
// when the editor content itself has not changed.
function renderTabs() {
    const container = document.getElementById('editor-groups');
    if (!container) return;
    // Check if the group layout matches what's in the DOM; full rebuild if not.
    const existingGroupEls = [...container.querySelectorAll('.editor-group')];
    const groupIds = state.groups.map(g => g.id);
    const domGroupIds = existingGroupEls.map(el => el.dataset.groupId);
    const layoutChanged = groupIds.join(',') !== domGroupIds.join(',');
    if (layoutChanged) { renderGroups(); return; }
    // Only rebuild tab bars, leaving content areas (and scroll position) intact.
    state.groups.forEach(group => {
        const groupEl = container.querySelector(`.editor-group[data-group-id="${group.id}"]`);
        if (!groupEl) return;
        groupEl.classList.toggle('active-group', group.id === state.activeGroupId);
        const oldTabBar = groupEl.querySelector('.group-tabs');
        if (!oldTabBar) return;
        const newTabBar = document.createElement('div');
        newTabBar.className = 'tabs group-tabs';
        group.tabs.forEach(path => newTabBar.appendChild(_makeTab(path, group)));
        newTabBar.addEventListener('dragover', e => {
            if (!_dragData || e.target.closest('.tab')) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
        });
        newTabBar.addEventListener('drop', e => {
            if (!_dragData || e.target.closest('.tab')) return;
            e.preventDefault();
            _dropTabOnGroup(_dragData, group.id, group.tabs.length);
        });
        newTabBar.addEventListener('wheel', e => {
            if (e.deltaY === 0) return;
            e.preventDefault();
            newTabBar.scrollLeft += e.deltaY;
        }, { passive: false });
        groupEl.replaceChild(newTabBar, oldTabBar);
    });
}

// ======================== GROUP / TAB ELEMENT FACTORIES ================
function _makeGroupEl(group) {
    const el = document.createElement('div');
    el.className = 'editor-group' + (group.id === state.activeGroupId ? ' active-group' : '');
    el.dataset.groupId = group.id;

    // Tab bar
    const tabBar = document.createElement('div');
    tabBar.className = 'tabs group-tabs';
    group.tabs.forEach(path => tabBar.appendChild(_makeTab(path, group)));
    // Drop on the tab bar background (not on a specific tab) → append to this group.
    tabBar.addEventListener('dragover', e => {
        if (!_dragData || e.target.closest('.tab')) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
    });
    tabBar.addEventListener('drop', e => {
        if (!_dragData || e.target.closest('.tab')) return;
        e.preventDefault();
        _dropTabOnGroup(_dragData, group.id, group.tabs.length);
    });
    tabBar.addEventListener('wheel', e => {
        if (e.deltaY === 0) return;
        e.preventDefault();
        tabBar.scrollLeft += e.deltaY;
    }, { passive: false });
    el.appendChild(tabBar);

    // Content area
    const content = document.createElement('div');
    content.className = 'editor-area group-content';
    // Update activeGroupId / currentFile when the user interacts here so all
    // mutation callbacks inside editor.js reference the correct file.
    content.addEventListener('focusin', () => _activateGroup(group.id), { capture: true });
    // Drop on the content area → move tab to this group (no split).
    content.addEventListener('dragover', e => {
        if (!_dragData) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
    });
    content.addEventListener('drop', e => {
        if (!_dragData) return;
        e.preventDefault();
        _dropTabOnGroup(_dragData, group.id, group.tabs.length);
    });
    el.appendChild(content);

    // Split drop zones (left / right edges — appear only while dragging a tab)
    ['left', 'right'].forEach(side => {
        const zone = document.createElement('div');
        zone.className = `split-zone split-zone-${side}`;
        zone.addEventListener('dragover', e => {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            zone.classList.add('drop-active');
        });
        zone.addEventListener('dragleave', () => zone.classList.remove('drop-active'));
        zone.addEventListener('drop', e => {
            e.preventDefault();
            zone.classList.remove('drop-active');
            if (_dragData) _splitDrop(_dragData, group.id, side);
        });
        el.appendChild(zone);
    });

    // Any click in the group activates it.
    el.addEventListener('mousedown', () => {
        if (state.activeGroupId !== group.id) _activateGroup(group.id);
    }, { capture: true });

    return el;
}

function _makeGroupResizer(afterGroupId) {
    const el = document.createElement('div');
    el.className = 'group-resizer';
    el.dataset.afterGroup = afterGroupId;
    return el;
}

function _makeTab(path, group) {
    const fs = state.openFiles.get(path);
    const gitClass = (typeof gitClassForFile === 'function') ? gitClassForFile(path) : '';
    const tab = document.createElement('div');
    tab.className = `tab${path === group.activeTab ? ' active' : ''}${gitClass}`;
    tab.dataset.path = path;
    tab.draggable = true;
    const shortName = path.split('/').pop() + (fs?.modified ? ' •' : '');
    tab.innerHTML = `<span class="tab-name">${esc(shortName)}</span><button class="tab-close" tabindex="-1">×</button>`;

    tab.querySelector('.tab-name').addEventListener('click', () => switchTab(path, group.id));
    tab.querySelector('.tab-close').addEventListener('click', e => { e.stopPropagation(); closeTab(path, group.id); });
    tab.addEventListener('mousedown', e => { if (e.button === 1) { e.preventDefault(); closeTab(path, group.id); } });

    // Drag: start
    tab.addEventListener('dragstart', e => {
        // Ensure any stale drag state from a previous drag is cleared before
        // starting a new one (defensive: dragend is not always reliable).
        document.getElementById('app')?.classList.remove('tab-dragging');
        _dragData = { groupId: group.id, path };
        e.dataTransfer.setData('text/plain', path); // required by Firefox
        e.dataTransfer.effectAllowed = 'move';
        tab.classList.add('dragging');
        document.getElementById('app').classList.add('tab-dragging');
    });
    tab.addEventListener('dragend', () => {
        _dragData = null;
        tab.classList.remove('dragging');
        document.getElementById('app').classList.remove('tab-dragging');
        document.querySelectorAll('.tab.drag-over-left, .tab.drag-over-right')
            .forEach(t => t.classList.remove('drag-over-left', 'drag-over-right'));
    });

    // Drag: over another tab — show left/right insertion indicator
    tab.addEventListener('dragover', e => {
        if (!_dragData) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        document.querySelectorAll('.tab.drag-over-left, .tab.drag-over-right')
            .forEach(t => t.classList.remove('drag-over-left', 'drag-over-right'));
        const rect = tab.getBoundingClientRect();
        tab.classList.add(e.clientX < rect.left + rect.width / 2 ? 'drag-over-left' : 'drag-over-right');
    });
    tab.addEventListener('dragleave', () => {
        tab.classList.remove('drag-over-left', 'drag-over-right');
    });

    // Drag: drop on tab — reorder or move to this group
    tab.addEventListener('drop', e => {
        e.preventDefault();
        tab.classList.remove('drag-over-left', 'drag-over-right');
        if (!_dragData) return;
        const rect = tab.getBoundingClientRect();
        const before = e.clientX < rect.left + rect.width / 2;
        const targetIdx = group.tabs.indexOf(path);
        _dropTabOnGroup(_dragData, group.id, before ? targetIdx : targetIdx + 1);
    });

    // Context menu
    tab.addEventListener('contextmenu', e => {
        e.preventDefault();
        const isReadOnly = state.openFiles.get(path)?.readOnly ?? false;
        showContextMenu(e.clientX, e.clientY, [
            { label: 'Close',        action: () => closeTab(path, group.id) },
            { label: 'Close Others', action: () => closeOtherTabs(path, group.id) },
            { label: 'Close All',    action: () => closeAllTabs() },
            '---',
            ...fileMenuItems(path, isReadOnly),
        ]);
    });

    return tab;
}

// ======================== GROUP ACTIVATION =============================
function _activateGroup(gid) {
    const group = state.groups.find(g => g.id === gid);
    if (!group) return;
    state.activeGroupId = gid;
    state.currentFile = group.activeTab;
    document.querySelectorAll('.editor-group')
        .forEach(el => el.classList.toggle('active-group', el.dataset.groupId === gid));
}

// ======================== PUBLIC TAB / GROUP OPERATIONS ================
async function openFile(path, targetGroupId) {
    const gid = targetGroupId ?? state.activeGroupId;
    let group = state.groups.find(g => g.id === gid) ?? state.groups[0];
    if (!group) return;

    // Already in this group → switch.
    if (group.tabs.includes(path)) {
        group.activeTab = path;
        _activateGroup(group.id);
        renderTabs();
        renderEditor(group.id);
        return;
    }

    group.tabs.push(path);
    group.activeTab = path;
    _activateGroup(group.id);

    // Already loaded (open in another group) → just render.
    if (state.openFiles.has(path)) {
        renderTabs();
        renderEditor(group.id);
        return;
    }

    // Placeholder → shows spinner immediately.
    const placeholder = new FileState(path, '');
    placeholder.loading = true;
    placeholder.yaml = [];
    state.openFiles.set(path, placeholder);
    renderTabs();
    renderEditor(group.id);

    try {
        const resp = await api.loadFile(path);
        if (!state.openFiles.has(path) || state.openFiles.get(path) !== placeholder) return;
        placeholder.content    = resp.content;
        placeholder.history    = [resp.content];
        placeholder.historyIdx = 0;
        const { protos, doc }  = parseYamlDoc(resp.content);
        placeholder.yaml       = protos;
        placeholder.doc        = doc;
        placeholder.dirtyProtos = new Set();
        placeholder.readOnly   = !!resp.readOnly || path.startsWith('__engine__/');
        placeholder.loading    = false;
        try {
            const stamps = await api.fileStamps([path]);
            if (stamps[path]) state.fileStamps.set(path, stamps[path]);
        } catch {}
        renderTabs();
        state.groups.filter(g => g.activeTab === path).forEach(g => renderEditor(g.id));
    } catch (e) {
        console.error('[Tabs] Open file failed:', path, e);
        toast(`Open failed: ${e.message}`, 'error');
        if (state.openFiles.get(path) === placeholder) state.openFiles.delete(path);
        state.groups.forEach(gr => {
            gr.tabs = gr.tabs.filter(t => t !== path);
            if (gr.activeTab === path) gr.activeTab = gr.tabs.at(-1) ?? null;
        });
        _syncCurrentFile();
        renderTabs();
        renderEditor();
    }
}

function switchTab(path, groupId) {
    const gid = groupId ?? state.activeGroupId;
    const group = state.groups.find(g => g.id === gid);
    if (!group || !group.tabs.includes(path)) return;
    group.activeTab = path;
    _activateGroup(gid);
    renderTabs();
    renderEditor(gid);
}

function closeTab(path, groupId) {
    const gid = groupId ?? state.activeGroupId;
    const group = state.groups.find(g => g.id === gid);
    if (!group) return;
    group.tabs = group.tabs.filter(t => t !== path);
    if (group.activeTab === path) group.activeTab = group.tabs.at(-1) ?? null;
    if (!state.groups.some(g => g.tabs.includes(path))) state.openFiles.delete(path);
    if (group.tabs.length === 0 && state.groups.length > 1) {
        state.groups = state.groups.filter(g => g.id !== gid);
        if (state.activeGroupId === gid) state.activeGroupId = state.groups[0].id;
    }
    _syncCurrentFile();
    renderTabs();
    renderEditor();
}

function closeOtherTabs(keep, groupId) {
    const gid = groupId ?? state.activeGroupId;
    const group = state.groups.find(g => g.id === gid);
    if (!group) return;
    const removed = group.tabs.filter(t => t !== keep);
    group.tabs = [keep];
    group.activeTab = keep;
    removed.forEach(p => { if (!state.groups.some(g => g.tabs.includes(p))) state.openFiles.delete(p); });
    _activateGroup(gid);
    renderTabs();
    renderEditor(gid);
}

function closeAllTabs() {
    state.openFiles.clear();
    state.groups = [{ id: state.groups[0]?.id ?? 'g1', tabs: [], activeTab: null }];
    state.activeGroupId = state.groups[0].id;
    state.currentFile = null;
    renderTabs();
    renderEditor();
}

// ======================== DRAG OPERATIONS ==============================
function _dropTabOnGroup(data, targetGroupId, insertIdx) {
    const { groupId: srcGroupId, path } = data;
    const srcGroup = state.groups.find(g => g.id === srcGroupId);
    const tgtGroup = state.groups.find(g => g.id === targetGroupId);
    if (!srcGroup || !tgtGroup) return;

    if (srcGroupId === targetGroupId) {
        const oldIdx = srcGroup.tabs.indexOf(path);
        let newIdx = insertIdx;
        if (newIdx > oldIdx) newIdx--;
        srcGroup.tabs.splice(oldIdx, 1);
        srcGroup.tabs.splice(Math.max(0, newIdx), 0, path);
    } else {
        srcGroup.tabs = srcGroup.tabs.filter(t => t !== path);
        if (srcGroup.activeTab === path) srcGroup.activeTab = srcGroup.tabs.at(-1) ?? null;
        tgtGroup.tabs.splice(Math.max(0, Math.min(insertIdx, tgtGroup.tabs.length)), 0, path);
        if (srcGroup.tabs.length === 0 && state.groups.length > 1)
            state.groups = state.groups.filter(g => g.id !== srcGroupId);
    }

    tgtGroup.activeTab = path;
    _activateGroup(targetGroupId);
    renderTabs();
    renderEditor(targetGroupId);
}

function _splitDrop(data, targetGroupId, side) {
    const { groupId: srcGroupId, path } = data;
    const srcGroup = state.groups.find(g => g.id === srcGroupId);
    const targetGroup = state.groups.find(g => g.id === targetGroupId);
    if (!srcGroup || !targetGroup) return;

    if (srcGroupId !== targetGroupId || srcGroup.tabs.length > 1) {
        srcGroup.tabs = srcGroup.tabs.filter(t => t !== path);
        if (srcGroup.activeTab === path) srcGroup.activeTab = srcGroup.tabs.at(-1) ?? null;
    }

    const newGroup = { id: _nextGid(), tabs: [path], activeTab: path };
    const targetIdx = state.groups.findIndex(g => g.id === targetGroupId);
    state.groups.splice(side === 'left' ? targetIdx : targetIdx + 1, 0, newGroup);

    if (srcGroup.tabs.length === 0 && state.groups.length > 1)
        state.groups = state.groups.filter(g => g.id !== srcGroupId);

    _activateGroup(newGroup.id);
    renderTabs();
    renderEditor(newGroup.id);
}

// ======================== HELPERS =====================================
function _syncCurrentFile() {
    const group = state.groups.find(g => g.id === state.activeGroupId) ?? state.groups[0];
    if (group) { state.activeGroupId = group.id; state.currentFile = group.activeTab; }
    else state.currentFile = null;
}

// Render initial empty state as soon as the DOM is available so the
// editor area isn't a blank void before any file is opened.
document.addEventListener('DOMContentLoaded', () => renderGroups());

