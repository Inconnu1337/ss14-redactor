/**
 * Tests for dumpYamlRespectful — verifies that all comment styles survive
 * a respectful save when the proto that contains them is not dirty.
 *
 * Run from repo root:
 *   node tests/yaml/yaml-respectful.test.js
 *
 * No npm dependencies: uses yaml-lib.js (committed IIFE bundle) + Node builtins.
 */
'use strict';

const assert = require('assert');
const path   = require('path');
const fs     = require('fs');
const vm     = require('vm');

// ── Load yaml-lib.js (IIFE bundle → sandbox.YAML) ────────────────────────────
const repoRoot   = path.resolve(__dirname, '../..');
const yamlLibSrc = fs.readFileSync(path.join(repoRoot, 'WebUI/js/yaml-lib.js'), 'utf8');
const libSandbox = { console };
vm.runInNewContext(yamlLibSrc, libSandbox);
const YAML = libSandbox.YAML;
if (!YAML || typeof YAML.parseDocument !== 'function') {
    throw new Error('yaml-lib.js did not expose a usable YAML global');
}

// ── Load yaml.js in a sandbox that has YAML available ────────────────────────
// `state` is intentionally absent → _canonicalizeProto skips field ordering.
const yamlJsSrc = fs.readFileSync(path.join(repoRoot, 'WebUI/js/yaml.js'), 'utf8');
const jsSandbox = { YAML, console };
vm.runInNewContext(yamlJsSrc, jsSandbox);
const { parseYamlDoc, dumpYamlRespectful, dumpYaml } = jsSandbox;
if (typeof dumpYamlRespectful !== 'function') {
    throw new Error('yaml.js did not expose dumpYamlRespectful');
}

// ── Minimal test runner ───────────────────────────────────────────────────────
let passed = 0, failed = 0;
function test(name, fn) {
    try {
        fn();
        console.log(`  ✓  ${name}`);
        passed++;
    } catch (e) {
        console.error(`  ✗  ${name}`);
        console.error(`     ${e.message}`);
        failed++;
    }
}
function assertContains(haystack, needle, label) {
    assert(haystack.includes(needle), `Expected output to contain ${label ?? JSON.stringify(needle)}`);
}

// ── All comment styles from the task specification ───────────────────────────
//
//   # outer comment                         ← file-level (before first item)
//   group_abc:    # group_abc comment        ← inline after mapping key
//   # group_xyz outer comment               ← between mapping keys
//   - DOGE       # asset comment            ← inline after sequence value
//   # default group inner comment           ← inside nested sequence
//
// The commented proto (proto1) must come out byte-for-byte identical in the
// output because only proto2 is marked dirty.
const COMMENTED_YAML = `# outer comment
- type: SomeProto
  id: proto1
  asset_groups:
    group_abc:    # group_abc comment
      - BTC
      - ETH
      - SOL
    # group_xyz outer comment
    group_xyz:
      - DOGE       # asset comment
      - PEPE
    default:
      # default group inner comment
      - 1INCH
      - ATOM
      - BNB
      - LINK
      - XRP
- type: OtherProto
  id: proto2
  value: 1
`;

// ── Tests ─────────────────────────────────────────────────────────────────────

test('file-level outer comment is preserved', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, '# outer comment');
});

test('inline mapping-key comment is preserved (group_abc)', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, 'group_abc:    # group_abc comment');
});

test('between-key comment is preserved (group_xyz outer)', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, '# group_xyz outer comment');
});

test('inline sequence-value comment is preserved (DOGE)', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, '- DOGE       # asset comment');
});

test('nested-sequence inner comment is preserved (default group)', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, '# default group inner comment');
});

test('dirty proto value is updated correctly', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 99;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assertContains(out, 'value: 99');
    assert(!out.includes('value: 1'), 'old value must not appear');
});

test('output contains no double sequence indicator (-- type:)', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    assert(!out.includes('- - type:'), 'double dash must not appear');
    assert(!out.includes('-- type:'),  'double dash (no space) must not appear');
});

test('output is valid YAML with exactly 2 root prototypes', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 7;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));
    const reparsed = parseYamlDoc(out).protos;
    assert.strictEqual(reparsed.length, 2, 'must still have 2 root entries');
    assert.strictEqual(reparsed[0].id, 'proto1');
    assert.strictEqual(reparsed[1].id, 'proto2');
    assert.strictEqual(reparsed[1].value, 7);
});

test('zero dirty protos → output identical to original text', () => {
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set());
    assert.strictEqual(out, COMMENTED_YAML);
});

test('falls back to dumpYaml when proto count changes (delete)', () => {
    const text = `- type: A\n  id: a\n- type: B\n  id: b\n`;
    const { protos, doc } = parseYamlDoc(text);
    protos.splice(0, 1);           // remove proto A → count mismatch
    const out = dumpYamlRespectful(protos, doc, text, new Set([0]));
    const reparsed = parseYamlDoc(out).protos;
    assert.strictEqual(reparsed.length, 1, 'fallback: should have 1 proto');
    assert.strictEqual(reparsed[0].id, 'b');
});

test('clean proto text preserved verbatim (byte-for-byte slice check)', () => {
    // The clean proto's slice in the output must equal the original slice.
    const { protos, doc } = parseYamlDoc(COMMENTED_YAML);
    protos[1].value = 2;
    const out = dumpYamlRespectful(protos, doc, COMMENTED_YAML, new Set([1]));

    // Find the section of the original that belongs to proto1 (from '- type: SomeProto' onwards)
    const p1Start = COMMENTED_YAML.indexOf('- type: SomeProto');
    const p2Start = COMMENTED_YAML.indexOf('- type: OtherProto');
    const proto1Original = COMMENTED_YAML.slice(p1Start, p2Start);

    assertContains(out, proto1Original, 'verbatim proto1 slice');
});

// ── Summary ───────────────────────────────────────────────────────────────────
console.log(`\n${passed + failed} tests: ${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);
