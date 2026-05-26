using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Content.Editor.Editor;

public sealed class MetadataRoot
{
    public Dictionary<string, PrototypeMetadata> Prototypes { get; set; } = new();
    public Dictionary<string, ComponentMetadata> Components { get; set; } = new();
    public Dictionary<string, DataDefinitionMetadata> DataDefinitions { get; set; } = new();

    /// <summary>
    /// Maps an abstract / polymorphic DataDefinition base type's full name
    /// (e.g. <c>Content.Shared._CE.EntityEffects.CEEntityEffect</c>) to the
    /// list of concrete implementor full names that can be picked at the
    /// <c>!type:</c> YAML tag site. Empty list means "no implementors found".
    /// </summary>
    public Dictionary<string, List<string>> PolymorphicTypes { get; set; } = new();
}

public sealed class PrototypeMetadata
{
    public string ClassName { get; set; } = "";
    public string YamlType { get; set; } = "";
    public bool Inheriting { get; set; }
    public string? Summary { get; set; }
    public List<FieldMetadata> Fields { get; set; } = new();
}

public sealed class ComponentMetadata
{
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Summary { get; set; }
    public List<FieldMetadata> Fields { get; set; } = new();
}

public sealed class DataDefinitionMetadata
{
    public string ClassName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string? Summary { get; set; }
    public List<FieldMetadata> Fields { get; set; } = new();
}

public sealed class FieldMetadata
{
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Type { get; set; } = "";
    public string FullType { get; set; } = "";
    public string FieldKind { get; set; } = "text";
    public bool Required { get; set; }
    public bool IsId { get; set; }
    public bool IsParent { get; set; }
    public bool IsAbstract { get; set; }
    public bool? AlwaysPushInheritance { get; set; }
    public bool? NeverPushInheritance { get; set; }
    public string? ProtoTypeArg { get; set; }
    public string[]? EnumValues { get; set; }

    // Schema-level default extracted from the C# field/property initializer
    // by scanning constructor IL. When <see cref="HasDefault"/> is false the
    // default is genuinely unknown and the WebUI keeps its '(unknown default)'
    // placeholder. When HasDefault is true, the Default value is meaningful
    // — including JSON null for fields explicitly initialised to null. The
    // boxed value is a JSON-friendly primitive (bool, long, double, string);
    // enums become the member name as a string, ProtoId/EntProtoId/LocId
    // wrappers become the inner string literal.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasDefault { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; set; }

    // Recursive type tree for collection elements / dict key+value.
    // Mirrors the same shape as a field's classification and recurses
    // through any depth of List/Array/Dictionary nesting, so the editor
    // can render an arbitrarily deep type (e.g.
    // Dictionary<string, Dictionary<string, List<int>>>).
    public FieldTypeNode? Element { get; set; }
    public FieldTypeNode? Key { get; set; }
    public FieldTypeNode? Value { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldTypeNode[]? TupleElements { get; set; }

    // DataDefinition reference
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDataDefinition { get; set; }
    public string? DataDefinitionType { get; set; }

    // XML doc summary
    public string? Summary { get; set; }
}

/// <summary>
/// Recursive type-tree node describing one level of a field's declared
/// type. <see cref="Element"/> applies to lists/arrays; <see cref="Key"/>
/// + <see cref="Value"/> apply to dictionaries. Children may themselves
/// have children (e.g. <c>Dictionary&lt;string, Dictionary&lt;string, string&gt;&gt;</c>
/// produces a node with a <see cref="Value"/> whose own <see cref="Key"/>
/// and <see cref="Value"/> are set), supporting arbitrary depth.
/// </summary>
public sealed class FieldTypeNode
{
    public string Kind { get; set; } = "text";
    public string? FullType { get; set; }
    public string? ProtoTypeArg { get; set; }
    public string[]? EnumValues { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDataDefinition { get; set; }
    public string? DataDefinitionType { get; set; }
    public FieldTypeNode? Element { get; set; }
    public FieldTypeNode? Key { get; set; }
    public FieldTypeNode? Value { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldTypeNode[]? TupleElements { get; set; }
}
