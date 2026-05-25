using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Content.Redactor.Redactor;

/// <summary>
/// Reads instance-constructor IL from compiled assemblies without executing
/// any code, and recovers literal field-initializer values
/// (e.g. <c>public bool Enabled = true;</c>) so they can be surfaced as
/// "schema defaults" alongside the rest of the prototype metadata.
///
/// <para>
/// Iteration 1 recognises only a curated set of patterns:
/// <list type="bullet">
///   <item>integer literals — <c>ldc.i4.*</c>, <c>ldc.i4.s</c>, <c>ldc.i4</c>, <c>ldc.i8</c></item>
///   <item>floats and doubles — <c>ldc.r4</c>, <c>ldc.r8</c></item>
///   <item>strings — <c>ldstr</c></item>
///   <item>null — <c>ldnull</c></item>
///   <item>enum members — <c>ldsfld &lt;EnumType&gt;::MemberName</c></item>
///   <item>single-arg wrapper <c>newobj</c> over the previous string literal
///         (covers <c>EntProtoId</c>, <c>ProtoId&lt;T&gt;</c>, <c>LocId</c>)</item>
/// </list>
/// Anything else (collection literals, <c>TimeSpan.FromMinutes</c>, etc.)
/// leaves the field's default as unknown — the WebUI then keeps its
/// existing <c>#</c> placeholder.
/// </para>
///
/// <para>
/// Field initializers compile into the body of every instance constructor
/// that does NOT chain via <c>: this(...)</c>. For SS14 components and
/// data-definitions there is virtually always a parameterless ctor that
/// owns the initializers, so the scanner picks the instance ctor with the
/// fewest parameters per type.
/// </para>
///
/// <para>
/// The scanner only looks at the leading "initializer prologue" of the
/// chosen ctor — the run of <c>ldarg.0 / &lt;const&gt; / stfld</c> triples
/// that the C# compiler emits before the <c>base..ctor()</c> call. As soon
/// as it encounters something it doesn't recognise it stops scanning that
/// method, which keeps the result conservative.
/// </para>
/// </summary>
public sealed class CtorDefaultsScanner
{
    // typeFullName -> (memberName -> boxed literal value)
    private readonly Dictionary<string, Dictionary<string, object?>> _byType
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Defaults discovered for <paramref name="typeFullName"/>, or null when
    /// nothing was found. Keys are the C# field name OR, for auto-properties,
    /// the property name (the scanner strips <c>&lt;Prop&gt;k__BackingField</c>).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? GetDefaultsFor(string typeFullName)
    {
        return _byType.TryGetValue(typeFullName, out var m) ? m : null;
    }

    /// <summary>
    /// Number of types for which at least one literal default was recovered.
    /// Surfaced for diagnostic logging only.
    /// </summary>
    public int TypesWithDefaults => _byType.Count;

    /// <summary>
    /// Open <paramref name="dllPath"/> via <see cref="PEReader"/> and accumulate
    /// any literal field-initializer defaults found in its types. Failures on
    /// individual types/methods are swallowed — the worst outcome is "no
    /// default known", never a thrown exception that breaks extraction.
    /// </summary>
    public void ScanAssembly(string dllPath)
    {
        FileStream fs;
        try { fs = File.OpenRead(dllPath); }
        catch { return; }
        using (fs)
        using (var pe = new PEReader(fs))
        {
            if (!pe.HasMetadata) return;
            MetadataReader md;
            try { md = pe.GetMetadataReader(); }
            catch { return; }

            foreach (var th in md.TypeDefinitions)
            {
                try { ScanType(pe, md, th); }
                catch { /* per-type best-effort */ }
            }
        }
    }

    private void ScanType(PEReader pe, MetadataReader md, TypeDefinitionHandle th)
    {
        var td = md.GetTypeDefinition(th);
        var fullName = BuildTypeFullName(md, td);
        if (fullName == null) return;

        // Pick the instance ctor with the fewest parameters — that's the one
        // the compiler emits field initializers into (non-chained ctors).
        MethodDefinition bestCtor = default;
        var bestParamCount = int.MaxValue;
        var found = false;
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if ((m.Attributes & System.Reflection.MethodAttributes.Static) != 0) continue;
            var name = md.GetString(m.Name);
            if (name != ".ctor") continue;
            if (m.RelativeVirtualAddress == 0) continue;
            // ParameterHandles count is cheap and accurate enough for ordering.
            int paramCount = 0;
            foreach (var _ in m.GetParameters()) paramCount++;
            if (paramCount < bestParamCount)
            {
                bestParamCount = paramCount;
                bestCtor = m;
                found = true;
            }
        }
        if (!found) return;

        // Map FieldDefinitionHandle row -> declared name (for stfld targets).
        var fieldNames = new Dictionary<int, string>();
        foreach (var fh in td.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            fieldNames[MetadataTokens.GetRowNumber(fh)] = md.GetString(f.Name);
        }

        MethodBodyBlock body;
        try { body = pe.GetMethodBody(bestCtor.RelativeVirtualAddress); }
        catch { return; }

        var defaults = ParseInitializerProlog(body.GetILBytes() ?? Array.Empty<byte>(),
            md, fieldNames);
        if (defaults.Count == 0) return;

        if (!_byType.TryGetValue(fullName, out var existing))
        {
            _byType[fullName] = defaults;
        }
        else
        {
            foreach (var kv in defaults) existing[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Build the namespace-qualified name (with <c>+</c> separators for
    /// nested types) so the result matches <see cref="Type.FullName"/> as
    /// returned by <c>MetadataLoadContext</c>.
    /// </summary>
    private static string? BuildTypeFullName(MetadataReader md, TypeDefinition td)
    {
        var name = md.GetString(td.Name);
        if (string.IsNullOrEmpty(name) || name == "<Module>") return null;

        var ns = md.GetString(td.Namespace);
        var declHandle = td.GetDeclaringType();
        if (declHandle.IsNil)
        {
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        // Nested: prefix with declaring type's full name + '+'.
        var declFull = BuildTypeFullName(md, md.GetTypeDefinition(declHandle));
        if (declFull == null) return null;
        return declFull + "+" + name;
    }

    /// <summary>
    /// Walk the ctor body and record every literal-stfld pair we recognise.
    /// Unlike a stack-accurate IL interpreter, this parser only tracks a
    /// single "value slot" filled by ldarg.0 → &lt;value-producing op&gt; →
    /// stfld sequences. When a field is initialised via a static factory
    /// call, newobj of an opaque type, or any other expression we cannot
    /// fold to a literal, we mark the slot as UNKNOWN and the corresponding
    /// stfld is silently skipped — but scanning *continues* past it so we
    /// still pick up later literal initialisers in the same ctor.
    /// </summary>
    private static Dictionary<string, object?> ParseInitializerProlog(
        byte[] il, MetadataReader md, Dictionary<int, string> fieldNames)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        int i = 0;
        bool slotKnown = false;
        object? slot = null;

        while (i < il.Length)
        {
            byte op = il[i];

            // ret — clean end of method.
            if (op == 0x2A) break;

            // nop / ldarg.0 — 1 byte, no slot change (ldarg.0 starts a new
            // field-init sequence).
            if (op == 0x00) { i++; continue; }
            if (op == 0x02) { slotKnown = false; slot = null; i++; continue; }

            // === Literal-producing opcodes (fill the slot) =================
            if (op == 0x14) { slot = null;  slotKnown = true; i++; continue; }       // ldnull
            if (op == 0x15) { slot = -1;    slotKnown = true; i++; continue; }       // ldc.i4.m1
            if (op >= 0x16 && op <= 0x1E)
            { slot = (int)(op - 0x16); slotKnown = true; i++; continue; }            // ldc.i4.0..8
            if (op == 0x1F) { slot = (int)(sbyte)il[i + 1]; slotKnown = true; i += 2; continue; }
            if (op == 0x20) { slot = BitConverter.ToInt32(il, i + 1);  slotKnown = true; i += 5; continue; }
            if (op == 0x21) { slot = BitConverter.ToInt64(il, i + 1);  slotKnown = true; i += 9; continue; }
            if (op == 0x22) { slot = BitConverter.ToSingle(il, i + 1); slotKnown = true; i += 5; continue; }
            if (op == 0x23) { slot = BitConverter.ToDouble(il, i + 1); slotKnown = true; i += 9; continue; }
            if (op == 0x72)
            {
                var tok = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var ush = MetadataTokens.UserStringHandle(tok & 0x00FFFFFF);
                    slot = md.GetUserString(ush);
                    slotKnown = true;
                }
                catch { slotKnown = false; slot = null; }
                i += 5;
                continue;
            }
            // ldsfld <fieldToken> — enum-literal recognition; otherwise opaque.
            if (op == 0x7E)
            {
                var tok = BitConverter.ToInt32(il, i + 1);
                if (TryResolveStaticFieldEnumName(md, tok, out var enumMember))
                { slot = enumMember; slotKnown = true; }
                else
                { slotKnown = false; slot = null; }
                i += 5;
                continue;
            }

            // newobj — only preserve the slot for true single-arg wrappers
            // (EntProtoId(string), ProtoId<T>(string), LocId(string), etc.).
            // For multi-arg ctors the most recent ldstr would otherwise be
            // mis-recorded as the field's default. Decode the called ctor's
            // signature to count parameters.
            if (op == 0x73)
            {
                var tok = BitConverter.ToInt32(il, i + 1);
                int? paramCount = TryGetCtorParamCount(md, tok);
                if (paramCount == 1 && slotKnown)
                {
                    // keep slot — single-arg wrapper around the existing literal
                }
                else
                {
                    slotKnown = false;
                    slot = null;
                }
                i += 5;
                continue;
            }

            // conv.r4 / conv.r8 — float widening preserves slot.
            if (op == 0x6C)
            {
                if (slotKnown && slot is int iv1) slot = (double)iv1;
                else if (slotKnown && slot is long lv1) slot = (double)lv1;
                else if (slotKnown && slot is float fv1) slot = (double)fv1;
                i++; continue;
            }
            if (op == 0x6D)
            {
                if (slotKnown && slot is int iv2) slot = (float)iv2;
                else if (slotKnown && slot is double dv2) slot = (float)dv2;
                i++; continue;
            }

            // === Opaque value-producers (5-byte form) ======================
            // call / callvirt / ldfld / box / ldtoken — slot becomes unknown
            // but we keep scanning. The corresponding stfld will be skipped.
            if (op == 0x28 || op == 0x6F || op == 0x7B || op == 0x8C || op == 0xD0)
            {
                slotKnown = false; slot = null;
                i += 5; continue;
            }
            // ldsflda / ldflda — 5 byte form, opaque pointer; treat as unknown.
            if (op == 0x7C || op == 0x7F) { slotKnown = false; slot = null; i += 5; continue; }

            // dup / pop — keep slot conservative.
            if (op == 0x25) { i++; continue; }
            if (op == 0x26) { slotKnown = false; slot = null; i++; continue; }

            // ldarg.s / ldarga.s / starg.s / ldloc.s / ldloca.s / stloc.s — 2 bytes.
            if (op == 0x0E || op == 0x0F || op == 0x10
                || op == 0x11 || op == 0x12 || op == 0x13)
            { slotKnown = false; slot = null; i += 2; continue; }

            // ldarg / ldarga / starg / ldloc / ldloca / stloc — 0xFE prefix forms — bail.

            // ldarg.1..3, ldloc.0..3, stloc.0..3 — 1 byte, opaque.
            if ((op >= 0x03 && op <= 0x05) || (op >= 0x06 && op <= 0x0D))
            { slotKnown = false; slot = null; i++; continue; }

            // === stfld — commit (or skip) =================================
            if (op == 0x7D)
            {
                var tok = BitConverter.ToInt32(il, i + 1);
                if (slotKnown && (tok & 0xFF000000) == 0x04000000)
                {
                    var row = tok & 0x00FFFFFF;
                    if (fieldNames.TryGetValue(row, out var fname))
                    {
                        if (fname.Length > 2 && fname[0] == '<')
                        {
                            var gt = fname.IndexOf('>');
                            if (gt > 1) fname = fname.Substring(1, gt - 1);
                        }
                        result[fname] = slot;
                    }
                }
                slotKnown = false; slot = null;
                i += 5; continue;
            }
            // stsfld — 5 byte, no recording (writes static).
            if (op == 0x80) { slotKnown = false; slot = null; i += 5; continue; }

            // Unknown opcode — stop scanning conservatively.
            return result;
        }

        return result;
    }

    /// <summary>
    /// Decode the signature of a <c>newobj</c> target (MethodDef or MemberRef
    /// token) and return its parameter count, or null when the signature
    /// can't be read. Used to distinguish 1-arg wrapper ctors
    /// (<c>EntProtoId(string)</c>) from multi-arg constructors where naively
    /// preserving the slot would record the wrong literal.
    /// </summary>
    private static int? TryGetCtorParamCount(MetadataReader md, int token)
    {
        try
        {
            var kind = token & 0xFF000000;
            var row = token & 0x00FFFFFF;
            BlobHandle sigHandle;
            if (kind == 0x06000000) // MethodDef
            {
                var mdh = MetadataTokens.MethodDefinitionHandle(row);
                sigHandle = md.GetMethodDefinition(mdh).Signature;
            }
            else if (kind == 0x0A000000) // MemberRef
            {
                var mrh = MetadataTokens.MemberReferenceHandle(row);
                sigHandle = md.GetMemberReference(mrh).Signature;
            }
            else if (kind == 0x2B000000) // MethodSpec — generic instantiation
            {
                var msh = MetadataTokens.MethodSpecificationHandle(row);
                var ms = md.GetMethodSpecification(msh);
                return TryGetCtorParamCount(md, MetadataTokens.GetToken(ms.Method));
            }
            else
            {
                return null;
            }

            var reader = md.GetBlobReader(sigHandle);
            var header = reader.ReadByte();
            // If GENERIC bit (0x10) is set, generic arg count follows.
            if ((header & 0x10) != 0) reader.ReadCompressedInteger();
            return reader.ReadCompressedInteger();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="fieldToken"/> refers to a static literal
    /// field on an enum type. Returns the enum member name on success.
    /// Used to translate <c>ldsfld FixtureMood::Happy</c> → <c>"Happy"</c>.
    /// </summary>
    private static bool TryResolveStaticFieldEnumName(MetadataReader md, int fieldToken,
        out string memberName)
    {
        memberName = "";
        var kind = fieldToken & 0xFF000000;
        var row = fieldToken & 0x00FFFFFF;
        try
        {
            if (kind == 0x04000000) // FieldDef
            {
                var fh = MetadataTokens.FieldDefinitionHandle(row);
                var fd = md.GetFieldDefinition(fh);
                if ((fd.Attributes & System.Reflection.FieldAttributes.Static) == 0) return false;
                if ((fd.Attributes & System.Reflection.FieldAttributes.Literal) == 0) return false;
                memberName = md.GetString(fd.Name);
                return true;
            }
            if (kind == 0x0A000000) // MemberRef
            {
                // MemberRef points into another assembly — we can't cheaply
                // verify the parent is an enum, and ldsfld on plain static
                // readonly fields (e.g. TimeSpan.Zero) is far more common
                // than cross-assembly enum members in practice. Be
                // conservative and return false; the slot will become
                // unknown and the field skipped.
                return false;
            }
        }
        catch { /* fall through */ }
        return false;
    }
}
