// ======================================================================
//  SS14 Prototype Editor – DOM Helpers & Utilities
// ======================================================================

'use strict';

function esc(s) { const d = document.createElement('div'); d.textContent = String(s); return d.innerHTML; }
function _el(tag) { return document.createElement(tag); }
function _div(cls) { const d = document.createElement('div'); if (cls) d.className = cls; return d; }
function _divClass(cls) { const d = document.createElement('div'); d.className = cls; return d; }

// ======================== TYPE NAME PRETTIFIER =========================
// .NET Type.FullName for closed generics looks like
//   System.Collections.Generic.Dictionary`2[[System.String, mscorlib, Version=...,
//     PublicKeyToken=...],[Robust.Shared.Prototypes.ProtoId`1[[Content.Shared...,
//     ...PublicKeyToken=null]], ..., PublicKeyToken=null]]
// which is unreadable in tooltips. Convert it to C#-style
//   Dictionary<string, ProtoId<AntagPrototype>>
// by stripping namespaces, assembly qualifiers, arity backticks and aliasing
// primitives. Pure string parser, no eval, returns the original on any error.
const _PRIMITIVE_ALIASES = {
    'String': 'string', 'Int32': 'int', 'Int64': 'long', 'Single': 'float',
    'Double': 'double', 'Boolean': 'bool', 'Byte': 'byte', 'SByte': 'sbyte',
    'Int16': 'short', 'UInt16': 'ushort', 'UInt32': 'uint', 'UInt64': 'ulong',
    'Char': 'char', 'Object': 'object', 'Decimal': 'decimal', 'Void': 'void',
};
function prettyTypeName(input) {
    if (!input || typeof input !== 'string') return input || 'unknown';
    const s = input;
    let i = 0;
    function skipSpaces() { while (i < s.length && s[i] === ' ') i++; }
    function shortenName(raw) {
        let n = raw.split('+').pop();
        const lastDot = n.lastIndexOf('.');
        if (lastDot >= 0) n = n.slice(lastDot + 1);
        const tick = n.indexOf('`');
        if (tick >= 0) n = n.slice(0, tick);
        return _PRIMITIVE_ALIASES[n] || n;
    }
    function parseType() {
        skipSpaces();
        const start = i;
        while (i < s.length && s[i] !== '[' && s[i] !== ',' && s[i] !== ']') i++;
        const name = s.slice(start, i);
        const args = [];
        if (s[i] === '[' && s[i + 1] === '[') {
            i++; // outer '['
            while (s[i] === '[') {
                i++; // inner '['
                args.push(parseType());
                // skip assembly qualifiers up to matching ']'
                let depth = 1;
                while (i < s.length && depth > 0) {
                    if (s[i] === '[') depth++;
                    else if (s[i] === ']') depth--;
                    if (depth > 0) i++;
                }
                if (s[i] === ']') i++;
                skipSpaces();
                if (s[i] === ',') { i++; skipSpaces(); }
            }
            if (s[i] === ']') i++;
        }
        const short = shortenName(name);
        return args.length ? short + '<' + args.join(', ') + '>' : short;
    }
    try { return parseType() || input; } catch { return input; }
}

// ======================== SMART SEARCH =================================
// Subsequence match: letters of `pattern` must appear in `text` in order
// but not necessarily contiguously. Whitespace in `query` splits it into
// independent tokens that may match in any order. Empty query matches
// everything.
//
// Examples:
//   smartMatch('CEStaminaThrowable', 'throw stam') === true
//   smartMatch('CEStaminaThrowable', 'stmth')      === true
//   smartMatch('CEStaminaThrowable', 'xyz')        === false
//
// This is the single search predicate used by every dropdown, picker,
// file tree, and prototype list in the redactor. Keep it pure & fast.
function smartMatch(text, query) {
    if (!query) return true;
    if (text == null) return false;
    const hay = String(text).toLowerCase();
    const q = String(query).toLowerCase().trim();
    if (!q) return true;
    const tokens = q.split(/\s+/);
    return tokens.every(tok => _isSubsequence(hay, tok));
}

function _isSubsequence(hay, needle) {
    if (!needle) return true;
    let i = 0;
    for (let h = 0; h < hay.length && i < needle.length; h++) {
        if (hay[h] === needle[i]) i++;
    }
    return i === needle.length;
}

// ======================== TOAST ========================================
function toast(msg, type = 'info') {
    const c = document.getElementById('toast-container');
    const t = _div(`toast toast-${type}`); t.textContent = msg;
    c.appendChild(t);
    requestAnimationFrame(() => t.classList.add('visible'));
    setTimeout(() => { t.classList.remove('visible'); setTimeout(() => t.remove(), 300); }, 2200);
}
