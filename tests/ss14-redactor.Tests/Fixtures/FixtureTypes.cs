using System;

#pragma warning disable CS0649 // Fixture fields are never written: they exist for reflection only.

namespace Content.Redactor.Tests.Fixtures;

// Fixture attributes mimicking SS14 ones. MetadataExtractor / FieldExtractor
// recognise attributes purely by Name (string), so we do not need to reference
// the real SS14 types.

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class PrototypeAttribute : Attribute
{
    public string YamlType { get; }
    public PrototypeAttribute(string yamlType) { YamlType = yamlType; }
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class RegisterComponentAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class DataDefinitionAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
internal sealed class ImplicitDataDefinitionForInheritorsAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class DataFieldAttribute : Attribute
{
    public string? Tag { get; }
    public bool Required { get; set; }
    public DataFieldAttribute() { }
    public DataFieldAttribute(string tag) { Tag = tag; }
    // 4-arg ctor used by ResolveRequired's positional-arg path.
    public DataFieldAttribute(string tag, Type? serializer, bool readOnly, bool required)
    { Tag = tag; Required = required; }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class IdDataFieldAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class ParentDataFieldAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class AbstractDataFieldAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class AlwaysPushInheritanceAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class NeverPushInheritanceAttribute : Attribute { }

internal interface IInheritingPrototype { }

internal enum FixtureMood { Happy, Sad, Neutral }

[Flags]
internal enum FixtureFlags { None = 0, A = 1, B = 2 }

// ---------- Prototype with various field shapes ----------

[Prototype("fixtureEntity")]
internal sealed class FixtureEntityPrototype : IInheritingPrototype
{
    [IdDataField] public string Id { get; set; } = "";
    [ParentDataField] public string? Parent { get; set; }
    [AbstractDataField] public bool Abstract { get; set; }

    [DataField] public string? Name;
    [DataField("renamed")] public int Count;
    [DataField(Required = true)] public bool Enabled;
    [DataField] public FixtureMood Mood;
    [DataField] public FixtureFlags Flags;
    [DataField] public System.Collections.Generic.List<string>? Tags;
    [DataField] public System.Collections.Generic.Dictionary<string, int>? Counters;
    [DataField] public FixtureDataDef? Nested;
    // Deeply nested collection to exercise BuildTypeNode recursion across
    // any combination of List/Array/Dictionary nesting.
    [DataField] public System.Collections.Generic.List<
        System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, string>>>? DeepLayers;
}

[Prototype("fixtureSimple")]
internal sealed class FixtureSimplePrototype
{
    [IdDataField] public string Id { get; set; } = "";
}

[DataDefinition]
internal sealed class FixtureDataDef
{
    [DataField] public string? Label;
    [DataField] public float Weight;
}

[ImplicitDataDefinitionForInheritors]
internal abstract class FixtureBaseEffect
{
    [DataField] public float Probability;
}

internal sealed class FixtureConcreteEffect : FixtureBaseEffect
{
    [DataField] public string? Message;
}

[RegisterComponent]
internal sealed class FixtureLightComponent
{
    [DataField] public float Radius;
    [DataField] public string? Sprite;
}

// ---------- Fixture for CtorDefaultsScanner ----------
//
// Each field carries a literal initializer the scanner should detect. Some
// "negative" cases are also included: `false`/`0`/`null` default-init the
// field anyway, so the C# compiler omits the stfld and the scanner returns
// no value for them. Tests assert both positive and negative outcomes.

[DataDefinition]
internal sealed class FixtureDefaultsDef
{
    [DataField] public bool BoolTrue = true;
    [DataField] public bool BoolFalseDefault = false;          // compiler omits stfld
    [DataField] public int IntFive = 5;
    [DataField] public int IntZeroDefault = 0;                  // compiler omits stfld
    [DataField] public int IntNegative = -7;
    [DataField] public long LongBig = 1234567890123L;
    [DataField] public float FloatPi = 3.14f;
    [DataField] public double DoubleE = 2.71828;
    [DataField] public string StrHello = "hello";
    [DataField] public string? StrNullDefault = null;           // compiler omits stfld
    [DataField] public FixtureMood MoodHappy = FixtureMood.Happy;
    [DataField] public string? PropWithDefault { get; set; } = "viaProp";
    [DataField] public int PropInt { get; set; } = 42;
}

// ---------- Regression fixtures (defaults edge cases) ----------

internal static class FixtureOpaque
{
    public static string MakeStr() => "factory";
    public static object MakeObj() => new object();
}

internal sealed class FixtureTwoArgWrapper
{
    public FixtureTwoArgWrapper(string a, string b) { _ = a; _ = b; }
}

// Field initialised via a static factory call; the next field is a plain
// literal. The scanner must skip past the opaque call without losing the
// trailing literal default. Mirrors ActionComponent.CheckCanInteract = true.
[DataDefinition]
internal sealed class FixtureOpaqueCtorDef
{
    [DataField] public string OpaqueByCall = FixtureOpaque.MakeStr();
    [DataField] public bool TrueAfterCall = true;
}

// Field initialised via multi-arg newobj where the LAST constructor arg is
// a string literal. Naively preserving the slot would record "second" as
// the default. The scanner must mark this slot opaque. The next plain
// literal field must still be recovered. Mirrors SpriteSpecifier.Rsi(ResPath, "tester_0").
[DataDefinition]
internal sealed class FixtureMultiArgNewObjDef
{
    [DataField] public FixtureTwoArgWrapper Wrapped = new FixtureTwoArgWrapper("first", "second");
    [DataField] public int RecoveredAfter = 9;
}

// Field initialised via `ldsfld` of a cross-assembly non-enum static
// readonly field (TimeSpan.Zero). The scanner must NOT record "Zero" as
// an enum member name. Followed by a literal that must still be recovered.
[DataDefinition]
internal sealed class FixtureStaticLdsfldDef
{
    [DataField] public TimeSpan SpanZero = TimeSpan.Zero;
    [DataField] public int AfterStatic = 11;
}

// Field with an explicit `= null` initializer on a reference type is
// elided by Roslyn (default field value is already null), so no fixture
// exercises that path: it's covered ad-hoc against real SS14 assemblies.

// Mimic the SS14 runtime-only handles.

internal sealed class FixtureWithRuntimeHandlesDef
{
    [DataField] public Robust.Shared.GameObjects.EntityUid Owner;
    [DataField] public Robust.Shared.GameObjects.NetEntity NetOwner;
    [DataField] public Robust.Shared.GameObjects.EntityUid? OptionalOwner;
    [DataField] public System.Collections.Generic.List<Robust.Shared.GameObjects.EntityUid>? OwnerList;
    [DataField] public string KeepMe = "visible";
}



