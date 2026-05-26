using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Content.Redactor.Redactor;

/// <summary>
/// Extracts DataField metadata from type members. Handles classification of
/// field types (boolean, integer, enum, ProtoId, list, map, DataDefinition, etc.).
/// </summary>
public sealed class FieldExtractor
{
    /// <summary>
    /// Fully-qualified type names whose fields the editor never displays —
    /// they refer to runtime engine state (entity handles, network ids)
    /// that is populated by systems at simulation time and is never set
    /// from prototypes. Add new entries here when an extra runtime-only
    /// type starts leaking into the metadata.
    /// </summary>
    private static readonly HashSet<string> RuntimeOnlyTypeNames = new(StringComparer.Ordinal)
    {
        "Robust.Shared.GameObjects.EntityUid",
        "Robust.Shared.GameObjects.NetEntity",
        "Robust.Shared.GameObjects.EntityCoordinates",
        "Robust.Shared.GameObjects.NetCoordinates",
        "Robust.Shared.Map.MapCoordinates",
        "Robust.Shared.Map.MapId",
        "Robust.Shared.Map.GridId",
        "Robust.Shared.Map.EntityCoordinates",
        "Robust.Shared.Map.MapGridComponent",
        "Robust.Shared.Player.ICommonSession",
        "Robust.Shared.Network.NetUserId",
    };

    /// <summary>
    /// True when <paramref name="type"/> (or its inner Nullable&lt;T&gt;) is
    /// a runtime-only handle that should never appear in the prototype
    /// editor. Collections of runtime-only types are also skipped so the
    /// editor doesn't render an empty editable list of EntityUids.
    /// </summary>
    private static bool IsRuntimeOnlyType(Type type)
    {
        var t = type;
        if (t.Name == "Nullable`1" && t.IsGenericType)
        {
            try { var inner = t.GetGenericArguments(); if (inner.Length > 0) t = inner[0]; }
            catch { /* keep t */ }
        }
        var full = t.FullName ?? t.Name;
        if (RuntimeOnlyTypeNames.Contains(full)) return true;

        // Generic single-arg collections (List<T>, HashSet<T>, IReadOnlyList<T>, etc.)
        // and the two-arg dictionary case — recurse into each type argument.
        if (t.IsGenericType)
        {
            try
            {
                foreach (var arg in t.GetGenericArguments())
                    if (IsRuntimeOnlyType(arg)) return true;
            }
            catch { /* fall through */ }
        }
        // Arrays / arrays-of-arrays.
        if (t.IsArray)
        {
            try { var e = t.GetElementType(); if (e != null && IsRuntimeOnlyType(e)) return true; }
            catch { /* keep going */ }
        }
        return false;
    }

    private readonly XmlDocReader _xmlDocs;
    private readonly Dictionary<string, DataDefinitionMetadata> _dataDefinitions;
    private readonly CtorDefaultsScanner? _defaultsScanner;

    public FieldExtractor(XmlDocReader xmlDocs, Dictionary<string, DataDefinitionMetadata> dataDefinitions)
        : this(xmlDocs, dataDefinitions, null) { }

    public FieldExtractor(XmlDocReader xmlDocs,
        Dictionary<string, DataDefinitionMetadata> dataDefinitions,
        CtorDefaultsScanner? defaultsScanner)
    {
        _xmlDocs = xmlDocs;
        _dataDefinitions = dataDefinitions;
        _defaultsScanner = defaultsScanner;
    }

    public List<FieldMetadata> ExtractDataFields(Type type)
    {
        var fields = new List<(int order, FieldMetadata meta)>();
        var seen = new HashSet<string>();

        var current = type;
        var baseOrder = 0;
        while (current != null)
        {
            // Get all backing field tokens for proper source-order interleaving
            var fieldDefs = current.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var backingTokens = new Dictionary<string, int>();
            foreach (var f in fieldDefs)
                backingTokens[f.Name] = f.MetadataToken & 0x00FFFFFF;

            // Collect explicit fields (non-backing)
            var members = new List<(int token, MemberInfo member)>();
            foreach (var f in fieldDefs)
            {
                if (f.Name.EndsWith("k__BackingField")) continue;
                members.Add((f.MetadataToken & 0x00FFFFFF, f));
            }

            // Collect properties, using backing field token for ordering
            foreach (var p in current.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var bfName = $"<{p.Name}>k__BackingField";
                var token = backingTokens.GetValueOrDefault(bfName, int.MaxValue);
                members.Add((token, p));
            }

            members.Sort((a, b) => a.token.CompareTo(b.token));

            foreach (var (token, member) in members)
            {
                if (!seen.Add(member.Name))
                    continue;

                var meta = TryBuildFieldMeta(member);
                if (meta != null)
                    fields.Add((baseOrder + token, meta));
            }

            baseOrder += 100_000;
            current = current.BaseType;
        }

        return fields.OrderBy(f => f.order).Select(f => f.meta).ToList();
    }

    private FieldMetadata? TryBuildFieldMeta(MemberInfo member)
    {
        CustomAttributeData? dfAttr = null;
        bool isId = false, isParent = false, isAbstract = false;
        bool alwaysPush = false, neverPush = false;

        foreach (var a in member.CustomAttributes)
        {
            switch (a.AttributeType.Name)
            {
                case "DataFieldAttribute":
                    dfAttr = a;
                    break;
                case "IdDataFieldAttribute":
                    dfAttr = a;
                    isId = true;
                    break;
                case "ParentDataFieldAttribute":
                    dfAttr = a;
                    isParent = true;
                    break;
                case "AbstractDataFieldAttribute":
                    dfAttr = a;
                    isAbstract = true;
                    break;
                case "AlwaysPushInheritanceAttribute":
                    alwaysPush = true;
                    break;
                case "NeverPushInheritanceAttribute":
                    neverPush = true;
                    break;
            }
        }

        if (dfAttr == null)
            return null;

        Type? memberType = member switch
        {
            FieldInfo fi => fi.FieldType,
            PropertyInfo pi => pi.PropertyType,
            _ => null,
        };
        if (memberType == null)
            return null;

        // Hide runtime-only handles (EntityUid, NetEntity, and collections
        // of them). Prototypes never populate these — they're set by game
        // systems at simulation time.
        if (IsRuntimeOnlyType(memberType))
            return null;

        var tag = ResolveTag(dfAttr, member.Name, isId, isParent, isAbstract);
        var required = ResolveRequired(dfAttr);
        var (fieldKind, enumValues, protoTypeArg) = ClassifyType(memberType);

        var meta = new FieldMetadata
        {
            Name = member.Name,
            Tag = tag,
            Type = memberType.Name,
            FullType = memberType.FullName ?? memberType.Name,
            FieldKind = fieldKind,
            Required = required,
            IsId = isId,
            IsParent = isParent,
            IsAbstract = isAbstract,
            AlwaysPushInheritance = alwaysPush ? true : null,
            NeverPushInheritance = neverPush ? true : null,
            ProtoTypeArg = protoTypeArg,
            EnumValues = enumValues,
            Summary = _xmlDocs.GetMemberSummary(member),
        };

        EnrichFieldTypeInfo(meta, memberType);

        // Schema default (from C# field initializer IL). Looked up against
        // the declaring type so a base class's defaults aren't shadowed by a
        // derived class that happens to share a field name.
        if (_defaultsScanner != null)
        {
            var declaring = member.DeclaringType?.FullName;
            if (!string.IsNullOrEmpty(declaring))
            {
                var map = _defaultsScanner.GetDefaultsFor(declaring!);
                if (map != null && map.TryGetValue(member.Name, out var def))
                {
                    var mapped = MapDefaultToFieldType(def, memberType);
                    // Distinguish "no entry" from "entry with null/unmappable
                    // value" so the UI can show '(unknown default)' only when
                    // we really don't know. NaN/Infinity flow through
                    // MapDefaultToFieldType as null and stay HasDefault=false.
                    if (mapped != null)
                    {
                        meta.Default = mapped;
                        meta.HasDefault = true;
                    }
                    else if (def == null)
                    {
                        // Explicit `= null` initializer.
                        meta.Default = null;
                        meta.HasDefault = true;
                    }
                }
            }
        }

        // Fallbacks for cases where Roslyn omits the stfld because the
        // declared initializer matches the runtime default for that type.
        // Surfacing these explicitly stops the editor from rendering
        // '(unknown)' for every unset boolean / nullable / collection
        // field — by far the common case in SS14 prototypes.
        if (!meta.HasDefault)
        {
            var t = memberType;
            var isNullable = t.Name == "Nullable`1" && t.IsGenericType;
            if (isNullable)
            {
                var inner = t.GetGenericArguments();
                if (inner.Length > 0) t = inner[0];
            }

            if (isNullable)
            {
                meta.Default = null;
                meta.HasDefault = true;
            }
            else if (t.Name == "Boolean")
            {
                meta.Default = false;
                meta.HasDefault = true;
            }
            else if (IsListLikeType(memberType))
            {
                meta.Default = System.Array.Empty<object?>();
                meta.HasDefault = true;
            }
            else if (IsDictionaryLikeType(memberType))
            {
                meta.Default = new Dictionary<string, object?>();
                meta.HasDefault = true;
            }
        }
        return meta;
    }

    private static bool IsListLikeType(Type t)
    {
        if (t.IsArray) return true;
        if (!t.IsGenericType) return false;
        return t.Name switch
        {
            "List`1" or "IList`1" or "IReadOnlyList`1"
                or "IEnumerable`1" or "ICollection`1" or "IReadOnlyCollection`1"
                or "HashSet`1" or "ISet`1" or "IReadOnlySet`1"
                or "Stack`1" or "Queue`1" => true,
            _ => false,
        };
    }

    private static bool IsDictionaryLikeType(Type t)
    {
        if (!t.IsGenericType) return false;
        return t.Name switch
        {
            "Dictionary`2" or "IDictionary`2" or "IReadOnlyDictionary`2"
                or "SortedDictionary`2" or "ConcurrentDictionary`2" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Massage a raw IL-extracted literal into the most useful shape for
    /// the editor. Currently this only handles the enum case: Roslyn folds
    /// enum literals to their underlying numeric value during compile, so
    /// the scanner reports an integer; here we resolve it back to the
    /// member NAME (string) so the WebUI can display "Happy" instead of 0.
    /// </summary>
    private static object? MapDefaultToFieldType(object? raw, Type fieldType)
    {
        if (raw == null) return null;

        // NaN / ±Infinity cannot be represented in standard JSON and the
        // serializer throws when it sees them. Drop them rather than try
        // to encode as strings — the editor would have no good way to
        // display "Infinity" as a schema default anyway.
        if (raw is float fv && !float.IsFinite(fv)) return null;
        if (raw is double dv && !double.IsFinite(dv)) return null;

        // Unwrap Nullable<T> for the type-side check.
        var t = fieldType;
        if (t.Name.StartsWith("Nullable") && t.IsGenericType)
        {
            try { var inner = t.GetGenericArguments(); if (inner.Length > 0) t = inner[0]; }
            catch { /* keep t */ }
        }

        if (t.IsEnum)
        {
            // MetadataLoadContext disallows Enum.GetName, so walk the
            // declared static literal fields manually and compare values.
            long target;
            try { target = Convert.ToInt64(raw); }
            catch { return raw; }

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral) continue;
                try
                {
                    var rawVal = f.GetRawConstantValue();
                    if (rawVal == null) continue;
                    var lv = Convert.ToInt64(rawVal);
                    if (lv == target) return f.Name;
                }
                catch { /* try next */ }
            }
            // Unknown member (could happen for [Flags] combinations) — keep
            // the numeric value so the editor can still show something.
        }

        // Bool fields land in IL as `ldc.i4.0/1` (int). Promote back to a
        // real boolean so the JSON output is `true`/`false`, not `0`/`1`.
        if (t.Name == "Boolean" && raw is int bi)
            return bi != 0;

        return raw;
    }

    private static string ResolveTag(CustomAttributeData attr, string memberName,
        bool isId, bool isParent, bool isAbstract)
    {
        if (isId) return "id";
        if (isParent) return "parent";
        if (isAbstract) return "abstract";

        if (attr.ConstructorArguments.Count > 0 &&
            attr.ConstructorArguments[0].Value is string tag &&
            !string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        return char.ToLowerInvariant(memberName[0]) + memberName[1..];
    }

    private static bool ResolveRequired(CustomAttributeData attr)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.MemberName == "Required" && named.TypedValue.Value is bool r)
                return r;
        }

        if (attr.AttributeType.Name == "DataFieldAttribute" &&
            attr.ConstructorArguments.Count >= 4 &&
            attr.ConstructorArguments[3].Value is bool reqArg)
        {
            return reqArg;
        }

        return false;
    }

    public (string kind, string[]? enumValues, string? protoTypeArg) ClassifyType(Type type)
    {
        var name = type.Name;

        if (name.StartsWith("Nullable") && type.IsGenericType)
        {
            try
            {
                var inner = type.GetGenericArguments();
                if (inner.Length > 0)
                    return ClassifyType(inner[0]);
            }
            catch { /* fall through */ }
        }

        return name switch
        {
            "Boolean" => ("boolean", null, null),
            "String" => ("text", null, null),
            "Int32" or "Int16" or "Int64" or "Byte" or "SByte"
                or "UInt16" or "UInt32" or "UInt64" => ("integer", null, null),
            "Single" or "Double" or "Decimal" => ("float", null, null),
            "EntProtoId" => ("entityProtoId", null, null),
            "Color" => ("color", null, null),
            "SpriteSpecifier" => ("spriteSpecifier", null, null),
            "SoundSpecifier" or "SoundPathSpecifier" or "SoundCollectionSpecifier"
                => ("soundSpecifier", null, null),
            "ResPath" or "ResourcePath" => ("resPath", null, null),
            "Vector2" or "Vector2i" => ("vector2", null, null),
            "Vector3" or "Vector3i" => ("vector3", null, null),
            "Vector4" => ("vector4", null, null),
            "Box2" or "Box2i" => ("box2", null, null),
            "TimeSpan" => ("text", null, null),
            "LocId" => ("text", null, null),
            // ComponentRegistry is structurally a list of component entries
            // (mirrors the top-level `components:` block on a prototype).
            // Surfacing it as its own field-kind lets the WebUI render the
            // same compCard UI used for the prototype components block.
            "ComponentRegistry" => ("componentRegistry", null, null),
            _ when name.StartsWith("ProtoId") && type.IsGenericType => ExtractProtoIdInfo(type),
            _ when name.StartsWith("EntProtoId") && type.IsGenericType => ("entityProtoId", null, null),
            _ when IsValueTupleType(type) => ("tuple", null, null),
            _ when type.IsArray => ("list", null, null),
            _ when IsDictionaryLike(type) => ("map", null, null),
            _ when IsListLike(type) => ("list", null, null),
            _ when type.IsEnum && type.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute")
                => ("flags", SafeEnumValues(type), null),
            _ when type.IsEnum => ("enum", SafeEnumValues(type), null),
            _ when HasDataDefinitionAttributeChain(type) => ("object", null, null),
            _ => ("text", null, null),
        };
    }

    /// <summary>
    /// True when <paramref name="type"/> itself or any of its ancestors is
    /// annotated with <c>[DataDefinition]</c> or
    /// <c>[ImplicitDataDefinitionForInheritors]</c>, OR when the type is
    /// already registered in <c>_dataDefinitions</c> (the discovery pass
    /// registers content-defined interfaces such as <c>IWireAction</c> that
    /// carry no DataDefinition attribute themselves but act as <c>!type:</c>
    /// polymorphic bases for at least one concrete implementor).
    /// </summary>
    private bool HasDataDefinitionAttributeChain(Type type)
    {
        var t = type;
        while (t != null && t.FullName != "System.Object")
        {
            if (t.CustomAttributes.Any(a => a.AttributeType.Name is "DataDefinitionAttribute"
                or "ImplicitDataDefinitionForInheritorsAttribute"))
                return true;
            t = t.BaseType;
        }
        if (type.FullName != null && _dataDefinitions.ContainsKey(type.FullName))
            return true;
        return false;
    }

    /// <summary>
    /// Known generic type names (Name property, with `1/`2 suffix) treated as ordered/unordered lists.
    /// </summary>
    private static readonly HashSet<string> _listTypeNames = new(StringComparer.Ordinal)
    {
        "List`1", "IList`1", "IReadOnlyList`1",
        "ICollection`1", "IReadOnlyCollection`1", "IEnumerable`1",
        "HashSet`1", "ISet`1", "IReadOnlySet`1", "SortedSet`1",
        "Queue`1", "Stack`1", "LinkedList`1",
        "ImmutableArray`1", "ImmutableList`1", "ImmutableHashSet`1",
        "ImmutableQueue`1", "ImmutableStack`1", "ImmutableSortedSet`1",
        "Collection`1", "ReadOnlyCollection`1", "ObservableCollection`1",
    };

    private static readonly HashSet<string> _dictTypeNames = new(StringComparer.Ordinal)
    {
        "Dictionary`2", "IDictionary`2", "IReadOnlyDictionary`2",
        "SortedDictionary`2", "SortedList`2", "ConcurrentDictionary`2",
        "ImmutableDictionary`2", "ImmutableSortedDictionary`2",
    };

    private static bool IsValueTupleType(Type type) =>
        type.IsGenericType && type.Name.StartsWith("ValueTuple`");

    private static bool IsListLike(Type type)
    {
        if (!type.IsGenericType) return false;
        if (_listTypeNames.Contains(type.Name)) return true;
        // Probe interfaces for IEnumerable<T> / ICollection<T> implementations.
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && _listTypeNames.Contains(iface.Name))
                return true;
        }
        return false;
    }

    private static bool IsDictionaryLike(Type type)
    {
        if (!type.IsGenericType) return false;
        if (_dictTypeNames.Contains(type.Name)) return true;
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && _dictTypeNames.Contains(iface.Name))
                return true;
        }
        return false;
    }

    private void EnrichFieldTypeInfo(FieldMetadata field, Type memberType)
    {
        // Unwrap Nullable<T> so the enrichment describes the underlying type.
        if (memberType.Name.StartsWith("Nullable") && memberType.IsGenericType)
        {
            try
            {
                var inner = memberType.GetGenericArguments();
                if (inner.Length > 0) { EnrichFieldTypeInfo(field, inner[0]); return; }
            }
            catch { /* fall through */ }
        }

        // DataDefinition reference
        var fullName = memberType.FullName ?? memberType.Name;
        if (_dataDefinitions.ContainsKey(fullName))
        {
            field.IsDataDefinition = true;
            field.DataDefinitionType = fullName;
        }

        // Recursive type tree — supports arbitrary nesting of
        // List/Array/Dictionary so the editor doesn't need a flat
        // metadata field per nesting level.
        try
        {
            // Check dictionary before list — Dictionary<,> also implements
            // IEnumerable<KeyValuePair<,>> so IsListLike would otherwise match.
            if (IsDictionaryLike(memberType))
            {
                var (kT, vT) = GetDictionaryKeyValueTypes(memberType);
                if (kT != null) field.Key = BuildTypeNode(kT, 0);
                if (vT != null) field.Value = BuildTypeNode(vT, 0);
            }
            else if (IsListLike(memberType))
            {
                var elem = GetListElementType(memberType);
                if (elem != null) field.Element = BuildTypeNode(elem, 0);
            }
            else if (memberType.IsArray)
            {
                var elem = memberType.GetElementType();
                if (elem != null) field.Element = BuildTypeNode(elem, 0);
            }
            else if (IsValueTupleType(memberType))
            {
                var args = memberType.GetGenericArguments();
                var elems = new List<FieldTypeNode>();
                foreach (var arg in args)
                {
                    var en = BuildTypeNode(arg, 0);
                    if (en != null) elems.Add(en);
                }
                if (elems.Count > 0) field.TupleElements = elems.ToArray();
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Recursively build a <see cref="FieldTypeNode"/> describing
    /// <paramref name="t"/> and any nested collection/dictionary
    /// parameters. Depth-limited as a safety net against pathological
    /// recursive types — 8 levels is far more than any real prototype
    /// schema needs.
    /// </summary>
    private FieldTypeNode? BuildTypeNode(Type? t, int depth)
    {
        if (t == null || depth > 8) return null;
        if (t.Name.StartsWith("Nullable") && t.IsGenericType)
        {
            try { var inner = t.GetGenericArguments(); if (inner.Length > 0) return BuildTypeNode(inner[0], depth + 1); }
            catch { /* fall through */ }
        }
        var (kind, ev, p) = ClassifyType(t);
        var node = new FieldTypeNode
        {
            Kind = kind,
            FullType = t.FullName ?? t.Name,
            ProtoTypeArg = p,
            EnumValues = ev,
        };
        var full = t.FullName ?? t.Name;
        if (_dataDefinitions.ContainsKey(full))
        {
            node.IsDataDefinition = true;
            node.DataDefinitionType = full;
        }
        try
        {
            if (IsDictionaryLike(t))
            {
                var (kT, vT) = GetDictionaryKeyValueTypes(t);
                if (kT != null) node.Key = BuildTypeNode(kT, depth + 1);
                if (vT != null) node.Value = BuildTypeNode(vT, depth + 1);
            }
            else if (IsListLike(t))
            {
                var elem = GetListElementType(t);
                if (elem != null) node.Element = BuildTypeNode(elem, depth + 1);
            }
            else if (t.IsArray)
            {
                var elem = t.GetElementType();
                if (elem != null) node.Element = BuildTypeNode(elem, depth + 1);
            }
            else if (IsValueTupleType(t))
            {
                var args = t.GetGenericArguments();
                var elems = new List<FieldTypeNode>();
                foreach (var arg in args)
                {
                    var en = BuildTypeNode(arg, depth + 1);
                    if (en != null) elems.Add(en);
                }
                if (elems.Count > 0) node.TupleElements = elems.ToArray();
            }
        }
        catch { /* ignore — leave children null */ }
        return node;
    }

    private static Type? GetListElementType(Type type)
    {
        // Direct generic argument first (List<T>, HashSet<T>, ImmutableArray<T>, ...)
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (args.Length == 1) return args[0];
        }
        // Otherwise probe IEnumerable<T> interface
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.Name == "IEnumerable`1")
            {
                var args = iface.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }
        }
        return null;
    }

    private static (Type? key, Type? value) GetDictionaryKeyValueTypes(Type type)
    {
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (args.Length >= 2) return (args[0], args[1]);
        }
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType &&
                (iface.Name == "IDictionary`2" || iface.Name == "IReadOnlyDictionary`2"))
            {
                var args = iface.GetGenericArguments();
                if (args.Length == 2) return (args[0], args[1]);
            }
        }
        return (null, null);
    }

    private static (string, string[]?, string?) ExtractProtoIdInfo(Type type)
    {
        try
        {
            var args = type.GetGenericArguments();
            if (args.Length > 0)
            {
                var protoType = args[0];

                // Preferred: read the [Prototype("name")] attribute — the YAML
                // index is keyed by exactly that string.  Falling back to the
                // C# type name only works when the prototype follows the
                // FooPrototype → "foo" convention (e.g. TagPrototype → "tag")
                // but breaks for e.g. ContentTileDefinition → "tile".
                foreach (var attr in protoType.GetCustomAttributesData())
                {
                    if (attr.AttributeType.Name != "PrototypeAttribute") continue;
                    if (attr.ConstructorArguments.Count > 0 &&
                        attr.ConstructorArguments[0].Value is string protoName &&
                        !string.IsNullOrEmpty(protoName))
                    {
                        return ("protoId", null, protoName);
                    }
                }

                var argName = protoType.Name;
                if (argName.EndsWith("Prototype"))
                    argName = argName[..^"Prototype".Length];
                var yamlType = char.ToLowerInvariant(argName[0]) + argName[1..];
                return ("protoId", null, yamlType);
            }
        }
        catch { /* fallback */ }

        return ("protoId", null, null);
    }

    private static string[]? SafeEnumValues(Type type)
    {
        try
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => f.Name)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
