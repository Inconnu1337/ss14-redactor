// ======================================================================
//  SS14 Prototype Redactor – Editor & Proto Cards
// ======================================================================

'use strict';

// ======================== EDITOR =======================================
// renderEditor() rebuilds the whole right pane.  This is intentionally
// blunt — the editor is rarely the bottleneck — but rapid mutations
// (color pickers, sliders, batched undo) used to trigger many renders
// per frame.  scheduleRenderEditor() coalesces those into one rAF tick.
let _renderScheduled = false;
function scheduleRenderEditor() {
    if (_renderScheduled) return;
    _renderScheduled = true;
    requestAnimationFrame(() => {
        _renderScheduled = false;
        renderEditor();
    });
}

function renderEditor() {
    const area = document.getElementById('editor-area');
    if (!state.currentFile) {
        area.innerHTML = `<div class="empty-state"><div class="empty-icon">📋</div>
            <h2>SS14 Prototype Redactor</h2>
            <p>Open a YAML file from the sidebar to start editing prototypes visually.</p>
            <p class="hint">Ctrl+S — force save</p></div>`;
        return;
    }
    const fs = state.openFiles.get(state.currentFile);
    if (!fs) return;
    if (fs.loading) {
        area.innerHTML = `<div class="editor-loading"><div class="editor-spinner"></div><div class="editor-loading-label">Loading ${esc(state.currentFile.split('/').pop())}\u2026</div></div>`;
        return;
    }
    const protos = fs.yaml;
    if (!Array.isArray(protos) || protos.length === 0) {
        area.innerHTML = '<div class="empty-state"><p>No prototypes found in this file.</p></div>';
        area.appendChild(buildAddProtoFooter());
        return;
    }

    // Pre-load parent files then build cards
    preloadParents(protos).then(() => {
        state.resolvedCache.clear();

        // Save collapse state before re-render
        const collapseState = saveCollapseState(area);

        area.innerHTML = '';
        for (let i = 0; i < protos.length; i++) {
            try {
                area.appendChild(buildCard(protos[i], i));
            } catch (e) {
                console.error('[Editor] Error building card:', protos[i]?.id || i, e);
                const errCard = _div('proto-card proto-error');
                errCard.innerHTML = `<div class="proto-header"><span class="proto-type-badge">${esc(protos[i]?.type || '?')}</span><span class="proto-id-text">${esc(protos[i]?.id || '?')}</span></div><div class="proto-body" style="color:var(--warning);padding:8px">${esc(e.message)}</div>`;
                area.appendChild(errCard);
            }
        }
        area.appendChild(buildAddProtoFooter());

        // Restore collapse state
        restoreCollapseState(area, collapseState);
    }).catch(e => {
        console.error('[Editor] preloadParents failed:', e);
        area.innerHTML = `<div class="empty-state"><p style="color:var(--warning)">Render error: ${esc(e.message)}</p></div>`;
    });
}

function buildAddProtoFooter() {
    const footer = _div('add-proto-footer');
    const btn = _el('button'); btn.className = 'add-proto-btn'; btn.textContent = '+ Add Prototype';
    btn.addEventListener('click', () => showAddProtoModal());
    footer.appendChild(btn);
    return footer;
}

// Eye-icon collapse/expand toggle. The button visually swaps icon via the
// `.collapsed` class on its nearest ancestor card (proto-card or
// component-card). Hover-only visibility is handled in CSS.
function buildCollapseBtn(getCard) {
    const btn = _el('button');
    btn.className = 'collapse-btn';
    btn.type = 'button';
    btn.title = 'Show only overridden fields';
    btn.setAttribute('aria-label', 'Toggle field visibility');
    btn.innerHTML = `
        <svg class="icon-open" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/></svg>
        <svg class="icon-closed" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9.88 9.88a3 3 0 1 0 4.24 4.24"/><path d="M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68"/><path d="M6.61 6.61A13.526 13.526 0 0 0 2 12s3 7 10 7a9.74 9.74 0 0 0 5.39-1.61"/><line x1="2" x2="22" y1="2" y2="22"/></svg>`;
    btn.addEventListener('click', e => {
        e.stopPropagation();
        const card = getCard();
        if (!card) return;
        card.classList.toggle('collapsed');
        btn.title = card.classList.contains('collapsed')
            ? 'Show all fields'
            : 'Show only overridden fields';
    });
    return btn;
}

function showAddProtoModal() {
    const overlay = _div('modal-overlay');
    const modal = _div('modal');
    modal.innerHTML = `<div class="modal-header"><h3>Add Prototype</h3><button class="modal-close">\u00d7</button></div>
        <div class="modal-body"></div>`;
    overlay.appendChild(modal);
    document.body.appendChild(overlay);
    const body = modal.querySelector('.modal-body');
    modal.querySelector('.modal-close').addEventListener('click', () => overlay.remove());
    overlay.addEventListener('click', e => { if (e.target === overlay) overlay.remove(); });

    // ── Step 1: pick prototype type ──────────────────────────────────
    // List = union of metadata-declared types and types actually present in
    // YAML. The two can diverge when the C# class name doesn't follow the
    // FooPrototype → "foo" convention; using the union ensures the user can
    // always pick a type that has clonable instances on disk.
    function showStepType() {
        modal.querySelector('h3').textContent = 'Add Prototype';
        body.innerHTML = `<input type="text" class="field-input modal-search" placeholder="Search prototype type\u2026" autocomplete="off"><div class="modal-list"></div>`;
        const searchInp = body.querySelector('.modal-search');
        const listEl = body.querySelector('.modal-list');
        const typeSet = new Set();
        if (state.metadata?.prototypes) for (const k of Object.keys(state.metadata.prototypes)) typeSet.add(k);
        if (state.protoIndex) for (const k of Object.keys(state.protoIndex)) typeSet.add(k);
        const types = [...typeSet].sort();
        function renderList(q) {
            listEl.innerHTML = '';
            const filtered = types.filter(t => smartMatch(t, q));
            if (!filtered.length) { listEl.innerHTML = '<div class="dropdown-empty">No types found</div>'; return; }
            for (const t of filtered.slice(0, 100)) {
                const el = _div('modal-list-item');
                el.textContent = t;
                el.addEventListener('click', () => showStepMode(t));
                listEl.appendChild(el);
            }
        }
        renderList('');
        searchInp.addEventListener('input', () => renderList(searchInp.value));
        searchInp.focus();
    }

    // ── Step 2: empty vs copy-from-existing ──────────────────────────
    // Copy is disabled when the picked type has no instances on disk —
    // there's nothing to clone from. Case-insensitive lookup so a
    // metadata-derived key that doesn't exactly match the YAML literal
    // still resolves (matches the server-side Search fallback).
    function showStepMode(type) {
        modal.querySelector('h3').textContent = `Add ${type}`;
        const resolved = findProtoEntries(type);
        const canCopy = resolved.entries.length > 0;
        const copyHint = canCopy
            ? `Pick an existing ${esc(resolved.key)} and clone all its fields.`
            : `No existing <code>${esc(type)}</code> prototypes on disk to clone from.`;
        body.innerHTML = `
            <div class="add-proto-mode-row">
                <button class="add-proto-mode-btn" data-mode="empty">
                    <div class="add-proto-mode-title">Empty prototype</div>
                    <div class="add-proto-mode-hint">Start from a fresh ${esc(type)} with only <code>id</code> set.</div>
                </button>
                <button class="add-proto-mode-btn" data-mode="copy"${canCopy ? '' : ' disabled'}>
                    <div class="add-proto-mode-title">Copy from existing</div>
                    <div class="add-proto-mode-hint">${copyHint}</div>
                </button>
            </div>
            <div class="modal-back-row"><button class="modal-back-btn">\u2190 Back</button></div>`;
        body.querySelector('[data-mode="empty"]').addEventListener('click', () => {
            overlay.remove();
            addNewPrototype(type);
        });
        if (canCopy) {
            body.querySelector('[data-mode="copy"]').addEventListener('click', () => showStepCopy(resolved.key));
        }
        body.querySelector('.modal-back-btn').addEventListener('click', showStepType);
    }

    // ── Step 3: pick source prototype to clone ───────────────────────
    function showStepCopy(type) {
        modal.querySelector('h3').textContent = `Copy ${type}`;
        body.innerHTML = `
            <input type="text" class="field-input modal-search" placeholder="Search ${esc(type)} prototype\u2026" autocomplete="off">
            <div class="modal-list"></div>
            <div class="modal-back-row"><button class="modal-back-btn">\u2190 Back</button></div>`;
        const searchInp = body.querySelector('.modal-search');
        const listEl = body.querySelector('.modal-list');
        body.querySelector('.modal-back-btn').addEventListener('click', () => showStepMode(type));
        let timer;
        async function doSearch(q) {
            try {
                const tokens = String(q || '').trim().split(/\s+/).filter(Boolean);
                const serverHint = tokens[0] || '';
                const res = await api.searchProtos(type, serverHint);
                const refined = tokens.length > 1
                    ? res.filter(r => smartMatch(r.id, q) || smartMatch(r.name || '', q))
                    : res;
                listEl.innerHTML = '';
                if (!refined.length) { listEl.innerHTML = '<div class="dropdown-empty">No prototypes found</div>'; return; }
                for (const r of refined.slice(0, 200)) {
                    const el = _div('modal-list-item');
                    el.textContent = r.id;
                    el.addEventListener('click', () => {
                        overlay.remove();
                        copyPrototype(type, r.id);
                    });
                    listEl.appendChild(el);
                }
            } catch (e) { console.error('[AddProto] search failed:', e); }
        }
        searchInp.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => doSearch(searchInp.value), CFG.searchDebounce); });
        searchInp.focus();
        doSearch('');
    }

    showStepType();
}

/**
 * Find proto-index entries for a given type with a case-insensitive
 * fallback. Returns `{ key, entries }` where `key` is the actual index
 * key that matched (so callers can pass it on to /api/search-protos) and
 * `entries` is the matching array (empty when nothing matches).
 */
function findProtoEntries(type) {
    const idx = state.protoIndex || {};
    if (idx[type]?.length) return { key: type, entries: idx[type] };
    const lc = String(type).toLowerCase();
    for (const k of Object.keys(idx)) {
        if (k.toLowerCase() === lc) return { key: k, entries: idx[k] };
    }
    return { key: type, entries: [] };
}

/**
 * Deep-clone an existing prototype into the currently open file. The
 * source proto's YAML is loaded fresh from disk (it may live in a
 * different file than the current one), all keys are copied verbatim,
 * and the id is mutated to a unique name so the new entry doesn't
 * collide with the original.
 */
async function copyPrototype(type, sourceId) {
    const entries = state.protoIndex?.[type] || [];
    const entry = entries.find(e => e.id === sourceId);
    if (!entry?.file) { toast('Source prototype not found in index'); return; }
    let src;
    try {
        // Prefer the in-memory yaml of an open tab so any unsaved edits
        // are honoured; fall back to a disk read otherwise.
        const openFs = state.openFiles.get(entry.file);
        const yaml = openFs?.yaml ?? parseYaml((await api.loadFile(entry.file)).content);
        src = (Array.isArray(yaml) ? yaml : []).find(p => p?.type === type && p?.id === sourceId);
    } catch (e) {
        toast('Failed to read source file: ' + e.message);
        return;
    }
    if (!src) { toast(`Prototype "${sourceId}" not found in ${entry.file}`); return; }
    const fs = state.openFiles.get(state.currentFile);
    if (!fs) return;
    if (!Array.isArray(fs.yaml)) fs.yaml = [];
    const clone = JSON.parse(JSON.stringify(src));
    clone.id = _generateUniqueProtoId(type, sourceId);
    fs.yaml.push(clone);
    commitChange(fs);
    renderEditor();
    const area = document.getElementById('editor-area');
    area.scrollTop = area.scrollHeight;
}

function _generateUniqueProtoId(type, base) {
    const entries = state.protoIndex?.[type] || [];
    const used = new Set(entries.map(e => e.id));
    let id = base + 'Copy';
    let n = 1;
    while (used.has(id)) { id = base + 'Copy' + n; n++; }
    return id;
}

function addNewPrototype(type) {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs) return;
    if (!Array.isArray(fs.yaml)) fs.yaml = [];
    const proto = { type, id: 'NewPrototype' };
    // NOTE: we intentionally do NOT seed `proto.parent = ''` here. An empty
    // parent slot would force every new prototype into a half-filled state
    // and dump as `parent: ''` until the user manually clears it. The
    // parent control still works without seeding because the user can click
    // "+ Add item" on the parent bar which commits an empty slot via
    // `onParentChange` — keeping the add-item codepath intact.
    fs.yaml.push(proto);
    commitChange(fs);
    renderEditor();
    const area = document.getElementById('editor-area');
    area.scrollTop = area.scrollHeight;
}

// ======================== PROTO CARD ===================================
function buildCard(proto, idx) {
    // Default to collapsed: only overridden (.field-local) rows are visible
    // out of the box; expand reveals every field. Per-card collapse state
    // is preserved across re-renders by save/restoreCollapseState.
    const card = _div('proto-card collapsed');
    const type = proto.type || 'unknown';
    const id   = proto.id   || '(no id)';
    const meta = state.metadata?.prototypes?.[type];
    const inheriting = meta?.inheriting ?? false;
    const hasAbstractField = !!meta?.fields?.some(f => f.isAbstract);
    const isAbstract = !!proto.abstract;

    // Apply abstract styling
    if (isAbstract) card.classList.add('proto-abstract');

    // Single-line proto identity bar: drag handle, type, delete, collapse-eye.
    // All other proto-level fields (id, abstract, parent) render as regular
    // field rows in the body so they obey the same collapse/override rules
    // as everything else parsed from the prototype.
    const hdr = _div('proto-header');
    hdr.innerHTML = `<div class="proto-type-line">
            <span class="proto-drag-handle" draggable="true" title="Drag to reorder">\u22ee\u22ee</span>
            <span class="proto-type-badge" title="${esc(meta?.summary || '')}">${esc(type)}</span>
            <button class="delete-proto-btn" title="Delete prototype">×</button>
        </div>`;
    // Abstract row — only rendered for proto types whose metadata actually
    // declares an abstract field. Built via the standard bool fieldRow so
    // spacing and styling match every other bool field in the editor.
    // Stored for later appending into the body.
    let absRow = null;
    if (hasAbstractField) {
        const absSource = isAbstract ? 'local' : 'default';
        const absMeta = { fieldKind: 'boolean', tag: 'abstract' };
        const onAbsChange = checked => {
            const fs = state.openFiles.get(state.currentFile);
            if (!fs || !fs.yaml[idx]) return;
            if (checked) fs.yaml[idx].abstract = true;
            else delete fs.yaml[idx].abstract;
            state.resolvedCache.clear();
            commitChange(fs);
            renderEditor();
        };
        const onAbsReset = absSource === 'local'
            ? () => { deleteField([idx], 'abstract'); }
            : null;
        absRow = fieldRow('abstract', absMeta, isAbstract, absSource, onAbsChange, onAbsReset);
        absRow.classList.add('proto-abstract-row');
    }

    // ID rename on double-click — span lives in the body now, so we have to
    // resolve it after the body is built. The handler is attached below
    // after `idRow` is created.

    hdr.querySelector('.delete-proto-btn').addEventListener('click', e => {
        e.stopPropagation();
        if (!confirm(`Delete prototype "${id}"?`)) return;
        const fs = state.openFiles.get(state.currentFile);
        if (fs && fs.yaml) {
            fs.yaml.splice(idx, 1);
            commitChange(fs);
            renderEditor();
        }
    });
    // Eye-icon collapse toggle, inserted just before the × delete button so
    // it sits glued next to the type name (never floated to the right edge).
    const protoTypeLine = hdr.querySelector('.proto-type-line');
    const protoDeleteBtn = protoTypeLine.querySelector('.delete-proto-btn');
    protoTypeLine.insertBefore(buildCollapseBtn(() => card), protoDeleteBtn);
    hdr.addEventListener('contextmenu', e => {
        e.preventDefault(); e.stopPropagation();
        const items = [
            { label: 'Collapse / Expand', action: () => hdr.querySelector('.collapse-btn').click() },
        ];        if (meta?.className) items.push({ label: 'Open .cs source', action: () => api.openSource(meta.className) });
        items.push('---', { label: 'Delete prototype', danger: true, action: () => hdr.querySelector('.delete-proto-btn').click() });
        showContextMenu(e.clientX, e.clientY, items);
    });
    card.appendChild(hdr);

    // ── Drag-drop reorder ────────────────────────────────────────────
    // The handle is the only draggable surface so that text-selection in
    // inputs still works normally. Drop targets are the sibling cards.
    card.dataset.protoIdx = String(idx);
    card.dataset.protoId = String(id);
    card.dataset.protoType = String(type);
    const dragHandle = hdr.querySelector('.proto-drag-handle');
    dragHandle.addEventListener('dragstart', e => {
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('application/x-proto-idx', String(idx));
        // Some browsers require a text/plain fallback or dragstart is rejected.
        e.dataTransfer.setData('text/plain', String(idx));
        card.classList.add('dragging');
    });
    dragHandle.addEventListener('dragend', () => card.classList.remove('dragging'));
    card.addEventListener('dragover', e => {
        if (!e.dataTransfer.types.includes('application/x-proto-idx')) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        card.classList.add('drop-target');
    });
    card.addEventListener('dragleave', e => {
        if (e.target === card) card.classList.remove('drop-target');
    });
    card.addEventListener('drop', e => {
        card.classList.remove('drop-target');
        if (!e.dataTransfer.types.includes('application/x-proto-idx')) return;
        e.preventDefault();
        const from = parseInt(e.dataTransfer.getData('application/x-proto-idx'), 10);
        const to = idx;
        if (Number.isNaN(from) || from === to) return;
        const fs = state.openFiles.get(state.currentFile);
        if (!fs || !Array.isArray(fs.yaml) || from < 0 || from >= fs.yaml.length) return;
        const [moved] = fs.yaml.splice(from, 1);
        fs.yaml.splice(to, 0, moved);
        commitChange(fs);
        renderEditor();
    });

    // Parent field, rendered as a regular fieldRow in the body (same as
    // every other proto-level field) so collapse + override semantics work
    // uniformly.
    let parentRow = null;
    if (inheriting) {
        // Normalize parent value to always be an array for the list control.
        // Critical: an empty string is treated as a single in-progress slot,
        // NOT as "no parent". This is what makes "+ Add item" work – clicking
        // it persists `proto.parent = ''` (via onParentChange below), the card
        // re-renders, and the slot survives as an empty protoId search row
        // for the user to fill in instead of vanishing.
        let parentVal = proto.parent;
        if (parentVal == null) parentVal = [];
        else if (!Array.isArray(parentVal)) parentVal = [parentVal];

        const parentMeta = {
            fieldKind: 'list', tag: 'parent',
            element: { kind: 'protoId', fullType: 'protoId', protoTypeArg: type },
            required: false
        };
        const parentSource = proto.parent !== undefined ? 'local' : 'default';
        const onParentChange = v => {
            // Preserve empty-string slots so "+ Add item" doesn't immediately
            // erase itself (listCtrl now commits on add so PrototypeLayerData
            // and friends persist – an empty parent slot is a valid in-progress
            // state that the user fills in next).
            const arr = Array.isArray(v) ? v.filter(x => x != null) : [];
            if (arr.length === 0) deleteField([idx], 'parent');
            else if (arr.length === 1) setFieldValue([idx], 'parent', arr[0]);
            else setFieldValue([idx], 'parent', arr);
        };
        const onParentReset = parentSource === 'local' ? () => deleteField([idx], 'parent') : null;
        parentRow = fieldRow('parent', parentMeta, parentVal, parentSource, onParentChange, onParentReset);
    }

    // body
    const body = _div('proto-body');

    // Proto-level fields rendered as regular field rows so collapse / override
    // semantics are identical to the metadata-driven fields below.
    // 1. ID row — uses the same fieldRow codepath as every other field so
    //    indentation, override-bar, and reset slot all line up. The id is
    //    mandatory: editing it clears the resolved-inheritance cache because
    //    other prototypes may reference it as a parent.
    const idMeta = { fieldKind: 'string', tag: 'id', required: true };
    const onIdChange = v => {
        const fs = state.openFiles.get(state.currentFile);
        if (!fs || !fs.yaml[idx]) return;
        const newId = String(v ?? '').trim();
        if (!newId || newId === fs.yaml[idx].id) return;
        fs.yaml[idx].id = newId;
        state.resolvedCache.clear();
        commitChange(fs);
    };
    body.appendChild(fieldRow('id', idMeta, String(id), 'local', onIdChange, null));
    // 2. Abstract row (only when meta declares the field).
    if (absRow) body.appendChild(absRow);
    // 3. Parent row (only for inheriting prototypes).
    if (parentRow) body.appendChild(parentRow);

    // Resolve inherited values
    let inherited = {};
    if (inheriting && proto.parent) inherited = resolveInheritance(type, proto.parent);

    // Render ALL metadata fields with override tracking
    const renderedTags = new Set(['type']);
    if (meta) {
        for (const f of meta.fields) {
            if (f.isId || f.isAbstract || f.isParent) { renderedTags.add(f.tag); continue; } // rendered in header/sub-header
            if (f.tag === 'components') { renderedTags.add('components'); continue; }
            renderedTags.add(f.tag);

            const { value, source } = getFieldValue(proto, f.tag, inherited, f.default);

            const onReset = source === 'local' ? () => deleteField([idx], f.tag) : null;
            body.appendChild(fieldRow(f.tag, f, value, source, v => setFieldValue([idx], f.tag, v), onReset));
        }
    }

    // Extra fields not in metadata (custom user fields in YAML)
    for (const [k, v] of Object.entries(proto)) {
        if (k.startsWith('__') || renderedTags.has(k)) continue;
        const source = 'local'; // if in proto, it's local
        body.appendChild(genericRow(k, v, source, nv => setFieldValue([idx], k, nv), () => deleteField([idx], k)));
    }

    // Inherited-only fields not yet shown
    for (const [k, v] of Object.entries(inherited)) {
        if (['type', 'id', 'parent', 'abstract', 'components'].includes(k)) continue;
        if (renderedTags.has(k) || proto[k] !== undefined) continue;
        body.appendChild(genericRow(k, v, 'inherited', nv => setFieldValue([idx], k, nv), null));
    }

    // Components section
    if (type === 'entity' || proto.components) {
        const cs = buildComponentsSection(proto, idx, inherited);
        if (cs) body.appendChild(cs);
    }

    card.appendChild(body);

    // Context menu on editor area background for collapse/expand all
    body.addEventListener('contextmenu', e => {
        if (e.target.closest('.field-row, .component-card, .component-header')) return;
        e.preventDefault();
        showContextMenu(e.clientX, e.clientY, [
            { label: 'Collapse all prototypes', action: () => collapseAllProtos(true) },
            { label: 'Expand all prototypes', action: () => collapseAllProtos(false) },
        ]);
    });

    return card;
}

// ======================== COLLAPSE / EXPAND ALL ========================
// Collapse state is keyed by *prototype id* and *component type*, not by
// index — adding / removing / reordering a component must not bleed the
// expanded/collapsed flag onto an unrelated card.
function saveCollapseState(area) {
    const state = { protos: {}, comps: {} };
    area.querySelectorAll('.proto-card').forEach(card => {
        const pid = card.dataset.protoId || card.querySelector('.proto-id-text')?.textContent || '';
        if (!pid) return;
        state.protos[pid] = card.classList.contains('collapsed');
        state.comps[pid] = {};
        card.querySelectorAll('.component-card').forEach(comp => {
            const ct = comp.querySelector('.component-type')?.textContent || '';
            if (!ct) return;
            state.comps[pid][ct] = comp.classList.contains('collapsed');
        });
    });
    return state;
}

function restoreCollapseState(area, saved) {
    area.querySelectorAll('.proto-card').forEach(card => {
        const pid = card.dataset.protoId || card.querySelector('.proto-id-text')?.textContent || '';
        if (!pid) return;
        if (saved.protos[pid] !== undefined) {
            card.classList.toggle('collapsed', saved.protos[pid]);
        }
        const cm = saved.comps[pid];
        if (cm) {
            card.querySelectorAll('.component-card').forEach(comp => {
                const ct = comp.querySelector('.component-type')?.textContent || '';
                if (!ct) return;
                if (cm[ct] !== undefined) {
                    comp.classList.toggle('collapsed', cm[ct]);
                }
            });
        }
    });
}

function collapseAllProtos(collapse) {
    const area = document.getElementById('editor-area');
    area.querySelectorAll('.proto-card').forEach(card => {
        card.classList.toggle('collapsed', collapse);
    });
}

// Editor area context menu for collapse/expand all
document.addEventListener('DOMContentLoaded', () => {
    const area = document.getElementById('editor-area');
    if (area) {
        area.addEventListener('contextmenu', e => {
            // Only handle when clicking on empty bg area
            if (e.target.closest('.proto-card, .field-row, .component-card, .add-proto-footer')) return;
            e.preventDefault();
            showContextMenu(e.clientX, e.clientY, [
                { label: 'Collapse all prototypes', action: () => collapseAllProtos(true) },
                { label: 'Expand all prototypes', action: () => collapseAllProtos(false) },
            ]);
        });
    }
});

// ======================== COMPONENTS SECTION ============================
function buildComponentsSection(proto, protoIdx, inherited) {
    const sec = _div('components-section');
    sec.innerHTML = `<div class="components-header"><span>components</span><button class="add-component-btn" title="Add component">+</button></div>`;
    sec.querySelector('.add-component-btn').addEventListener('click', () => showAddComponentModal(proto, protoIdx));

    const localComps = proto.components || [];
    const inhComps   = inherited.components || [];
    // The components-section behaves like a single "row" at the proto-body
    // level: it carries `field-local` when this prototype actually defines
    // local components, otherwise `inherited`. That puts it on the same
    // visibility codepath as every other row inside `.proto-body`
    // (collapsed cards hide non-`.field-local` children uniformly).
    sec.classList.add(localComps.length > 0 ? 'field-local' : 'inherited');
    // Reset button: drop the whole local `components:` block, falling back
    // to the inherited list. Same affordance as the per-field reset (↺).
    if (localComps.length > 0) {
        const reset = _el('button');
        reset.className = 'field-reset-btn';
        reset.textContent = '↺';
        reset.title = 'Reset components (revert to inherited)';
        reset.addEventListener('click', e => {
            e.stopPropagation();
            const fs = state.openFiles.get(state.currentFile);
            if (!fs || !fs.yaml[protoIdx]) return;
            delete fs.yaml[protoIdx].components;
            state.resolvedCache.clear();
            commitChange(fs); renderEditor();
        });
        sec.querySelector('.components-header').appendChild(reset);
    }
    const localMap = new Map();
    localComps.forEach((c, i) => { if (c && c.type) localMap.set(c.type, { data: c, idx: i }); });
    const inhMap = new Map();
    for (const c of inhComps) { if (c && c.type && !localMap.has(c.type)) inhMap.set(c.type, c); }

    for (const [ct, { data, idx }] of localMap) sec.appendChild(compCard(ct, data, false, protoIdx, idx, inherited));
    for (const [ct, data]          of inhMap)   sec.appendChild(compCard(ct, data, true,  protoIdx, -1, inherited));
    return sec;
}

function showAddComponentModal(proto, protoIdx) {
    const existing = new Set((proto.components || []).map(c => c?.type).filter(Boolean));
    if (proto.parent) {
        const inh = resolveInheritance(proto.type, proto.parent);
        if (inh?.components) for (const c of inh.components) { if (c?.type) existing.add(c.type); }
    }
    pickComponentType(existing, (t) => {
        const fs = state.openFiles.get(state.currentFile);
        if (!fs || !fs.yaml[protoIdx]) return;
        if (!fs.yaml[protoIdx].components) fs.yaml[protoIdx].components = [];
        fs.yaml[protoIdx].components.push({ type: t });
        commitChange(fs); renderEditor();
    });
}

/**
 * Generic component-type picker modal. Used by the prototype components:
 * block AND by the ComponentRegistry field control. `excludeSet` is a
 * Set of component-type strings that should not appear in the list
 * (already-present components). `onPick(type)` is invoked when the user
 * selects a row.
 */
function pickComponentType(excludeSet, onPick) {
    const overlay = _div('modal-overlay');
    const modal = _div('modal');
    modal.innerHTML = `<div class="modal-header"><h3>Add Component</h3><button class="modal-close">\u00d7</button></div>
        <div class="modal-body">
            <input type="text" class="field-input modal-search" placeholder="Search component\u2026" autocomplete="off">
            <div class="modal-list"></div>
        </div>`;
    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    const searchInp = modal.querySelector('.modal-search');
    const listEl = modal.querySelector('.modal-list');
    const types = state.metadata?.components ? Object.keys(state.metadata.components).sort().filter(t => !excludeSet.has(t)) : [];

    function renderList(q) {
        listEl.innerHTML = '';
        const filtered = types.filter(t => smartMatch(t, q));
        if (!filtered.length) { listEl.innerHTML = '<div class="dropdown-empty">No components found</div>'; return; }
        for (const t of filtered.slice(0, 100)) {
            const el = _div('modal-list-item');
            el.textContent = t;
            el.addEventListener('click', () => { overlay.remove(); onPick(t); });
            listEl.appendChild(el);
        }
    }
    renderList('');
    searchInp.addEventListener('input', () => renderList(searchInp.value));
    searchInp.focus();
    modal.querySelector('.modal-close').addEventListener('click', () => overlay.remove());
    overlay.addEventListener('click', e => { if (e.target === overlay) overlay.remove(); });
}

/** Copy an inherited component to local YAML so it can be edited. Returns new compIdx. */
function localizeComponent(protoIdx, compType) {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs || !fs.yaml[protoIdx]) return -1;
    if (!fs.yaml[protoIdx].components) fs.yaml[protoIdx].components = [];
    // Check if already localized
    const existing = fs.yaml[protoIdx].components.findIndex(c => c && c.type === compType);
    if (existing >= 0) return existing;
    fs.yaml[protoIdx].components.push({ type: compType });
    return fs.yaml[protoIdx].components.length - 1;
}

function compCard(compType, data, isInh, protoIdx, compIdx, inherited, ctx) {
    // `ctx` (optional) decouples this card from prototype state so it can
    // also be used by the ComponentRegistry field control. When provided,
    // it intercepts mutations: { write(tag, value), reset(tag), remove(),
    // localize() -> newIdx }. Inheritance UI is suppressed in ctx mode
    // because a ComponentRegistry has no parent chain.
    if (ctx) isInh = false;
    // Default to collapsed for every component (inherited or local). Only
    // overridden field rows show until the user expands via the eye icon.
    const startCollapsed = true;
    // A local component that ALSO exists in the inherited chain is an
    // override – the `×` action effectively *resets* it to the inherited
    // value rather than truly deleting it. Surface that distinction in
    // the icon/tooltip so the design matches plain field rows (↺ for
    // override-reset, × for outright remove).
    const isOverride = !isInh && !ctx && Array.isArray(inherited?.components)
        && inherited.components.some(c => c && c.type === compType);
    const card = _div('component-card' + (startCollapsed ? ' collapsed' : '') + (isInh ? ' inherited' : ' comp-local'));
    const cMeta = state.metadata?.components?.[compType];
    const hdr = _div('component-header');
    hdr.innerHTML = `<span class="component-type" title="${esc(cMeta?.summary || '')}">${esc(compType)}</span>`;
    // Eye-icon collapse toggle, right after the component name. The reset
    // / remove button (when present) appears AFTER the eye, matching the
    // field-row pattern "label: data [eye] [reset/delete]".
    hdr.appendChild(buildCollapseBtn(() => card));
    if (!isInh && compIdx >= 0) {
        const rmBtn = _el('button');
        rmBtn.className = 'field-reset-btn comp-remove-btn';
        if (isOverride) {
            rmBtn.title = 'Reset to inherited value';
            rmBtn.textContent = '↺';
        } else {
            rmBtn.title = 'Remove component';
            rmBtn.textContent = '×';
        }
        rmBtn.addEventListener('click', e => {
            e.stopPropagation();
            if (ctx) { ctx.remove(); return; }
            const fs = state.openFiles.get(state.currentFile);
            if (fs && fs.yaml[protoIdx]?.components) {
                fs.yaml[protoIdx].components.splice(compIdx, 1);
                commitChange(fs); renderEditor();
            }
        });
        hdr.appendChild(rmBtn);
    }
    hdr.addEventListener('contextmenu', e => {
        e.preventDefault(); e.stopPropagation();
        const items = [];
        if (cMeta?.className) items.push({ label: 'Open .cs source', action: () => api.openSource(cMeta.className) });
        if (!isInh && compIdx >= 0) {
            items.push('---', { label: isOverride ? 'Reset to inherited' : 'Remove component', danger: !isOverride, action: () => {
                if (ctx) { ctx.remove(); return; }
                const fs = state.openFiles.get(state.currentFile);
                if (fs && fs.yaml[protoIdx]?.components) {
                    fs.yaml[protoIdx].components.splice(compIdx, 1);
                    commitChange(fs); renderEditor();
                }
            }});
        }
        items.push('---');
        items.push({ label: 'Collapse all components', action: () => collapseAllComponents(true) });
        items.push({ label: 'Expand all components', action: () => collapseAllComponents(false) });
        if (items.length) showContextMenu(e.clientX, e.clientY, items);
    });
    card.appendChild(hdr);

    const body = _div('component-body');
    const renderedTags = new Set(['type']);

    // Find inherited component data for this specific component type
    let inhCompData = {};
    if (inherited && inherited.components) {
        const inhC = (Array.isArray(inherited.components) ? inherited.components : [])
            .find(c => c && c.type === compType);
        if (inhC) inhCompData = inhC;
    }

    // For inherited components, editing any field "localizes" the component first
    function compOnChange(tag, nv) {
        if (ctx) { ctx.write(tag, nv); return; }
        if (isInh) {
            const newIdx = localizeComponent(protoIdx, compType);
            if (newIdx < 0) return;
            setFieldValue([protoIdx, 'components', newIdx], tag, nv);
        } else {
            setFieldValue([protoIdx, 'components', compIdx], tag, nv);
        }
    }
    function compOnReset(tag) {
        if (ctx) { ctx.reset(tag); return; }
        if (!isInh && compIdx >= 0) deleteField([protoIdx, 'components', compIdx], tag);
    }

    if (cMeta) {
        for (const f of cMeta.fields) {
            renderedTags.add(f.tag);

            let source, value;
            if (isInh) {
                source = 'inherited';
                value = data[f.tag];
            } else if (Object.prototype.hasOwnProperty.call(data, f.tag)) {
                source = 'local';
                value = data[f.tag];
            } else if (inhCompData[f.tag] !== undefined) {
                source = 'inherited';
                value = inhCompData[f.tag];
            } else {
                source = 'default';
                value = f.default;
            }

            const onReset = (!isInh && source === 'local') ? () => compOnReset(f.tag) : null;
            body.appendChild(fieldRow(f.tag, f, value, source, nv => compOnChange(f.tag, nv), onReset));
        }
    }

    // Extra fields in YAML not in metadata
    for (const [k, v] of Object.entries(data)) {
        if (k === 'type' || k.startsWith('__') || renderedTags.has(k)) continue;
        const source = isInh ? 'inherited' : 'local';
        const onReset = (!isInh) ? () => compOnReset(k) : null;
        body.appendChild(genericRow(k, v, source, nv => compOnChange(k, nv), onReset));
    }

    card.appendChild(body);
    return card;
}

function collapseAllComponents(collapse) {
    const area = document.getElementById('editor-area');
    area.querySelectorAll('.component-card').forEach(card => {
        card.classList.toggle('collapsed', collapse);
    });
}

// ======================== DATA UPDATES =================================
// Unified field update: works for proto fields, component fields, and datadef fields.
// `path` is an array of keys to reach the target object, e.g.:
//   proto field:     [protoIdx]               → fs.yaml[protoIdx]
//   component field: [protoIdx, 'components', compIdx] → fs.yaml[protoIdx].components[compIdx]
function setFieldValue(path, tag, value) {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs) return;
    let obj = fs.yaml;
    for (const key of path) { obj = obj?.[key]; }
    if (!obj) return;
    obj[tag] = value;
    state.resolvedCache.clear();
    commitChange(fs);
    scheduleRenderEditor();
}

function deleteField(path, tag) {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs) return;
    let obj = fs.yaml;
    for (const key of path) { obj = obj?.[key]; }
    if (!obj) return;
    delete obj[tag];
    state.resolvedCache.clear();
    commitChange(fs);
    scheduleRenderEditor();
}



function commitChange(fs) {
    const nc = dumpYaml(fs.yaml);
    state.resolvedCache.clear();
    fs.pushHistory(nc); renderTabs(); scheduleAutosave(fs);
}

// ======================== AUTOSAVE =====================================
function scheduleAutosave(fs) {
    if (fs.readOnly) return;
    clearTimeout(fs._saveTimer);
    fs._saveTimer = setTimeout(async () => {
        try {
            await api.saveFile(fs.path, fs.content);
            fs.modified = false; renderTabs(); toast('Saved', 'success');
            try {
                const stamps = await api.fileStamps([fs.path]);
                if (stamps[fs.path]) state.fileStamps.set(fs.path, stamps[fs.path]);
            } catch {}
            // Refresh git decorations so the tree + tabs colour the file as
            // modified. The Ctrl+S handler does the same — autosave used to
            // skip it, leaving the tree stale until the next SSE event.
            scheduleGitRefresh(100);
        }
        catch (e) {
            console.error('[Editor] Save failed:', fs.path, e);
            toast(`Save failed: ${e.message}`, 'error');
        }
    }, CFG.autosaveDelay);
}

// ======================== UNDO / REDO ==================================
function handleUndo() {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs || !fs.undo()) return;
    fs.yaml = parseYaml(fs.content);
    state.resolvedCache.clear();
    renderEditor(); renderTabs(); scheduleAutosave(fs);
}

function handleRedo() {
    const fs = state.openFiles.get(state.currentFile);
    if (!fs || !fs.redo()) return;
    fs.yaml = parseYaml(fs.content);
    state.resolvedCache.clear();
    renderEditor(); renderTabs(); scheduleAutosave(fs);
}
