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
const { parseYamlDoc, dumpYamlRespectful, dumpYaml, docSetField, docDeleteField } = jsSandbox;
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
function assertNotContains(haystack, needle, label) {
    assert(!haystack.includes(needle), `Expected output NOT to contain ${label ?? JSON.stringify(needle)}`);
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
    docSetField(doc, [1], 'value', 99);
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
    docSetField(doc, [1], 'value', 7);
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

test('internal comments in dirty proto are preserved after docSetField', () => {
    const text = [
        '- type: Entity',
        '  id: penguinA',
        '  components:',
        '  - type: MeleeWeapon',
        '    # too angry to juke',
        '    damage: 5',
        '    # no service',
        '    range: 10',
        '- type: Entity',
        '  id: penguinB',
        '  components: []',
        '',
    ].join('\n');

    const { protos, doc } = parseYamlDoc(text);

    // Mutate the first proto: change 'damage' via docSetField to keep AST in sync.
    protos[0].components[0].damage = 9;
    docSetField(doc, [0, 'components', 0], 'damage', 9);

    const out = dumpYamlRespectful(protos, doc, text, new Set([0]));

    assertContains(out, '# too angry to juke', 'internal comment before damage');
    assertContains(out, '# no service',         'internal comment before range');
    assertContains(out, 'damage: 9',             'mutated damage value');
    assertContains(out, 'range: 10',             'unchanged range value');
    assertContains(out, 'id: penguinB',          'second proto still present');
});

test('internal comments in dirty proto are preserved after docDeleteField', () => {
    const text = [
        '- type: Entity',
        '  id: deleteTest',
        '  # comment before desc',
        '  desc: hello',
        '  # comment before value',
        '  value: 42',
        '',
    ].join('\n');

    const { protos, doc } = parseYamlDoc(text);

    delete protos[0].desc;
    docDeleteField(doc, [0], 'desc');

    const out = dumpYamlRespectful(protos, doc, text, new Set([0]));

    assertNotContains(out, 'desc:',                'deleted field absent');
    assertContains(out, '# comment before value', 'internal comment before value');
    assertContains(out, 'value: 42',               'remaining field present');
});

test('deeply nested field change preserves inter-item comments (grenadepenguin scenario)', () => {
    // Mirrors the real grenadepenguin.yml structure:
    // branches[0].tasks[0].operator.removeKeyOnFinish is changed,
    // but # too angry to juke (commentBefore on tasks[1]) must survive.
    const text = [
        '- type: htnCompound',
        '  id: GrenadePenguinMeleeCombatCompound',
        '  branches:',
        '  - tasks:',
        '    - !type:HTNPrimitiveTask',
        '      operator: !type:MoveToOperator',
        '        removeKeyOnFinish: false',
        '        rangeKey: MeleeRange',
        '    # too angry to juke',
        '    - !type:HTNPrimitiveTask',
        '      operator: !type:MeleeOperator',
        '        targetKey: Target',
        '',
    ].join('\n');

    const { protos, doc } = parseYamlDoc(text);

    // Simulate what the editor does: mutate the JS value and call setFieldValue.
    // The entire branches value is replaced (deep-clone with one scalar changed).
    const newBranches = JSON.parse(JSON.stringify(protos[0].branches));
    newBranches[0].tasks[0].operator.removeKeyOnFinish = true;
    protos[0].branches = newBranches;
    // docSetField must patch branches in-place, preserving the comment.
    docSetField(doc, [0], 'branches', newBranches);

    const out = dumpYamlRespectful(protos, doc, text, new Set([0]));

    assertContains(out, '# too angry to juke',         'inter-task comment preserved');
    assertContains(out, 'removeKeyOnFinish: true',      'mutated scalar reflected');
    assertContains(out, 'rangeKey: MeleeRange',         'unchanged sibling field preserved');
    assertContains(out, 'targetKey: Target',             'second task preserved');
    assertContains(out, '!type:HTNPrimitiveTask',        '!type: tag on task node preserved');
    assertContains(out, '!type:MoveToOperator',          '!type: tag on operator node preserved');
    assertContains(out, '!type:MeleeOperator',           '!type: tag on second operator preserved');
    assertNotContains(out, '!<HTNPrimitiveTask>',        'no verbatim tag form !<...>');
    assertNotContains(out, '!<MoveToOperator>',          'no verbatim tag form !<...>');
});

test('!type: tags are preserved verbatim after docSetField on scalar sibling', () => {
    // Regression: _patchAstNode was setting astNode.tag = '__yamlTag' (the short
    // name) instead of '!type:' + __yamlTag, producing '!<Foo>' verbatim form.
    const text = [
        '- type: htnCompound',
        '  id: tagTest',
        '  branches:',
        '  - tasks:',
        '    - !type:HTNPrimitiveTask',
        '      operator: !type:MoveToOperator',
        '        speed: 1.5',
        '        removeKeyOnFinish: false',
        '',
    ].join('\n');

    const { protos, doc } = parseYamlDoc(text);
    const newBranches = JSON.parse(JSON.stringify(protos[0].branches));
    newBranches[0].tasks[0].operator.removeKeyOnFinish = true;
    protos[0].branches = newBranches;
    docSetField(doc, [0], 'branches', newBranches);

    const out = dumpYamlRespectful(protos, doc, text, new Set([0]));

    assertContains(out, 'removeKeyOnFinish: true',   'scalar mutation applied');
    assertContains(out, '!type:HTNPrimitiveTask',     '!type: tag preserved on task');
    assertContains(out, '!type:MoveToOperator',       '!type: tag preserved on operator');
    assertNotContains(out, '!<HTNPrimitiveTask>',     'no verbatim !<...> form on task');
    assertNotContains(out, '!<MoveToOperator>',       'no verbatim !<...> form on operator');
});

test('sequence items sit at the same indent as their parent key (indentSeq: false)', () => {
    // SS14 convention: sequences are NOT indented relative to their parent.
    // Wrong:  components:\n    - type: Foo   (4 spaces)
    // Right:  components:\n  - type: Foo     (2 spaces, same as parent)
    const text = [
        '- type: Entity',
        '  id: indentTest',
        '  components:',
        '  - type: MeleeWeapon',
        '    damage: 5',
        '',
    ].join('\n');

    const { protos, doc } = parseYamlDoc(text);
    protos[0].components[0].damage = 10;
    docSetField(doc, [0, 'components', 0], 'damage', 10);

    // dumpYamlRespectful: dirty proto re-serialised from AST node.
    const respectful = dumpYamlRespectful(protos, doc, text, new Set([0]));
    // dumpYaml: full rebuild from JS (e.g. after structural changes).
    const full = dumpYaml(protos);

    // Both paths must produce 2-space seq items (indentSeq: false).
    assertContains   (respectful, '\n  - type: MeleeWeapon', 'respectful: 2-space seq item');
    assertNotContains(respectful, '\n    - type: MeleeWeapon', 'respectful: not 4-space seq item');
    assertContains   (full, '\n  - type: MeleeWeapon', 'full: 2-space seq item');
    assertNotContains(full, '\n    - type: MeleeWeapon', 'full: not 4-space seq item');
});


console.log(`\n${passed + failed} tests: ${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);
