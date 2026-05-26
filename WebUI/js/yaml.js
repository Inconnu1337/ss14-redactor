// ======================================================================
//  SS14 Prototype Editor – YAML helpers (eemeli/yaml v2)
// ======================================================================

'use strict';

// ──────────────────────────────────────────────────────────────────────
//  AST → JS  (respects !type:Foo tags → { __yamlTag: 'Foo', ... })
// ──────────────────────────────────────────────────────────────────────
function _nodeToJs(node) {
    if (node == null) return null;
    if (YAML.isAlias(node)) return _nodeToJs(node.resolve());
    if (YAML.isScalar(node)) {
        if (node.tag && node.tag.startsWith('!type:')) {
            return { __yamlTag: node.tag.slice(6) };
        }
        return node.value;
    }
    if (YAML.isMap(node)) {
        const obj = {};
        if (node.tag && node.tag.startsWith('!type:')) {
            obj.__yamlTag = node.tag.slice(6);
        }
        for (const pair of node.items) {
            const k = YAML.isScalar(pair.key) ? pair.key.value : String(pair.key);
            if (k != null) obj[String(k)] = _nodeToJs(pair.value);
        }
        return obj;
    }
    if (YAML.isSeq(node)) {
        return node.items.map(_nodeToJs);
    }
    return null;
}

// ──────────────────────────────────────────────────────────────────────
//  JS → AST  (respects __yamlTag → !type:Foo tags)
// ──────────────────────────────────────────────────────────────────────
function _jsToNode(val, doc) {
    if (val === null || val === undefined) {
        return doc.createNode(null);
    }
    if (typeof val !== 'object') {
        return doc.createNode(val);
    }
    if (Array.isArray(val)) {
        const seq = doc.createNode([]);
        seq.items = val.map(item => _jsToNode(item, doc));
        return seq;
    }
    const tag = val.__yamlTag ? '!type:' + val.__yamlTag : null;
    const keys = Object.keys(val).filter(k => k !== '__yamlTag');
    if (tag && keys.length === 0) {
        // Parameterless polymorphic item → bare tagged scalar (`!type:Foo`)
        const scalar = doc.createNode(null);
        scalar.tag = tag;
        return scalar;
    }
    const map = doc.createNode({});
    map.items = keys.map(k =>
        new YAML.Pair(doc.createNode(k), _jsToNode(val[k], doc))
    );
    if (tag) map.tag = tag;
    return map;
}

function parseYaml(text) {
    try {
        const doc = YAML.parseDocument(text, { logLevel: 'error' });
        if (!doc.contents) return [];
        return YAML.isSeq(doc.contents)
            ? doc.contents.items.map(_nodeToJs)
            : [_nodeToJs(doc.contents)];
    } catch (e) {
        console.error('YAML parse error', e);
        return [];
    }
}

/**
 * Parse YAML text and return both the JS proto array AND the Document
 * object (for the respectful save path).  Returns { protos, doc }.
 */
function parseYamlDoc(text) {
    try {
        const doc = YAML.parseDocument(text, { logLevel: 'error' });
        const protos = doc.contents && YAML.isSeq(doc.contents)
            ? doc.contents.items.map(_nodeToJs)
            : [];
        return { protos, doc };
    } catch (e) {
        console.error('YAML parse error', e);
        return { protos: [], doc: null };
    }
}

/**
 * Parse a single arbitrary YAML value (mapping, sequence, or scalar).
 * Used for the raw-YAML textarea widget in field controls.
 */
function parseYamlValue(text) {
    try {
        const doc = YAML.parseDocument(text, { logLevel: 'error' });
        return doc.contents ? _nodeToJs(doc.contents) : null;
    } catch (e) {
        console.error('YAML parse error', e);
        return undefined;
    }
}

// ──────────────────────────────────────────────────────────────────────
//  Key ordering helpers
// ──────────────────────────────────────────────────────────────────────

// `type` and `id` are the structural YAML discriminators every prototype
// must carry — they always come first.  Everything else (parent, abstract,
// name, description, …) is taken from the metadata field order so the YAML
// matches the redactor's visual layout exactly.
const _PROTO_STRUCTURAL_HEAD = ['type', 'id'];

/**
 * Returns a copy of `obj` whose keys are in metadata-defined order so the
 * serialized YAML matches the visual layout of the redactor, not the
 * accidental order in which the user happened to override fields.
 */
function _orderKeys(obj, fieldOrder, structuralHead) {
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return obj;
    const seen = new Set();
    const out = {};
    if (obj.__yamlTag !== undefined) { out.__yamlTag = obj.__yamlTag; seen.add('__yamlTag'); }
    if (structuralHead) {
        for (const k of structuralHead) {
            if (Object.prototype.hasOwnProperty.call(obj, k) && !seen.has(k)) {
                out[k] = obj[k]; seen.add(k);
            }
        }
    }
    if (fieldOrder) {
        for (const tag of fieldOrder) {
            if (Object.prototype.hasOwnProperty.call(obj, tag) && !seen.has(tag)) {
                out[tag] = obj[tag]; seen.add(tag);
            }
        }
    }
    for (const k of Object.keys(obj)) {
        if (!seen.has(k)) out[k] = obj[k];
    }
    return out;
}

function _fieldOrderFor(meta) {
    if (!meta?.fields) return null;
    return meta.fields.map(f => f.tag);
}

function _canonicalizeProto(proto) {
    if (!proto || typeof proto !== 'object') return proto;
    const type = proto.type;
    const metaProto = (typeof state !== 'undefined' && state.metadata?.prototypes?.[type]) || null;
    const fieldOrder = _fieldOrderFor(metaProto);
    const ordered = _orderKeys(proto, fieldOrder, _PROTO_STRUCTURAL_HEAD);
    if (Array.isArray(ordered.components)) {
        ordered.components = ordered.components.map(c => _canonicalizeComponent(c));
    }
    return ordered;
}

function _canonicalizeComponent(comp) {
    if (!comp || typeof comp !== 'object') return comp;
    const compType = comp.type;
    const metaComp = (typeof state !== 'undefined' && state.metadata?.components?.[compType]) || null;
    const fieldOrder = _fieldOrderFor(metaComp);
    return _orderKeys(comp, fieldOrder, ['type']);
}

// ──────────────────────────────────────────────────────────────────────
//  Stringify helpers
// ──────────────────────────────────────────────────────────────────────
const _DUMP_OPTS = { indent: 2, lineWidth: -1, singleQuote: true };

function _dumpSingleProto(proto) {
    const doc = new YAML.Document();
    const seq = doc.createNode([]);
    seq.add(_jsToNode(_canonicalizeProto(proto), doc));
    doc.contents = seq;
    return doc.toString(_DUMP_OPTS);
}

/**
 * Full serialize: rebuild YAML text from scratch (no comment preservation).
 * Used for structural changes (add / delete / reorder prototype).
 */
function dumpYaml(data) {
    if (Array.isArray(data)) {
        return data.map(item => _dumpSingleProto(item).trimEnd()).join('\n\n') + '\n';
    }
    const doc = new YAML.Document();
    doc.contents = _jsToNode(data, doc);
    return doc.toString(_DUMP_OPTS);
}

/**
 * Respectful serialize: keeps original text (including comments) for
 * untouched prototypes; re-serializes only the dirty ones.
 *
 * Falls back to dumpYaml() if the prototype count changed (structural
 * change) or if positional range data is missing.
 *
 * IMPORTANT: in eemeli/yaml, items[i].range[0] points to the start of the
 * VALUE node AFTER the '- ' indicator.  We scan back to include the '- '
 * so gaps contain only blank lines and clean items include their '- '.
 */
function dumpYamlRespectful(yamlArray, doc, originalText, dirtyIndices) {
    if (!doc || !YAML.isSeq(doc.contents) ||
        yamlArray.length !== doc.contents.items.length) {
        return dumpYaml(yamlArray);
    }
    const items = doc.contents.items;
    const parts = [];
    let pos = 0;
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        if (!item.range) return dumpYaml(yamlArray); // safety: no range info
        const [nodeStart, , nodeEnd] = item.range;
        // Scan back from the value-node start to find the '-' sequence indicator.
        // items[i].range[0] is after '- ', so the gap between items otherwise
        // includes the '-' and would double it when a dirty item is re-serialized.
        const itemStart = _seqEntryStart(originalText, nodeStart);
        // Text between end of previous item (or start of file) and this item
        // (blank lines, inter-item comments, leading file comments, etc.)
        parts.push(originalText.slice(pos, itemStart));
        if (dirtyIndices.has(i)) {
            // _dumpSingleProto returns '- type: ...\n'; the gap has no '- '
            parts.push(_dumpSingleProto(yamlArray[i]));
        } else {
            // Slice from '-' to nodeEnd (inclusive of trailing newline)
            parts.push(originalText.slice(itemStart, nodeEnd));
        }
        pos = nodeEnd;
    }
    // Trailing content after the last item (trailing newline, etc.)
    parts.push(originalText.slice(pos));
    return parts.join('');
}

/**
 * Scan backwards from a value-node start offset to find the '-' sequence
 * indicator that introduces the block-sequence entry.
 */
function _seqEntryStart(text, nodeRangeStart) {
    let i = nodeRangeStart - 1;
    while (i >= 0 && (text[i] === ' ' || text[i] === '\t')) i--;
    if (i >= 0 && text[i] === '-') return i;
    return nodeRangeStart; // fallback (should not happen in a block sequence)
}
