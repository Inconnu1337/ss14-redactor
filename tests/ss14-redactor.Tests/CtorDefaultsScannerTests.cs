using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Content.Redactor.Tests;

using Content.Redactor.Redactor;

/// <summary>
/// Scans this very test DLL with <see cref="CtorDefaultsScanner"/> and asserts
/// that the literal initializers on <c>FixtureDefaultsDef</c> are recovered.
/// Mirrors the production extraction path: the scanner runs once over each
/// DLL up front, then results are looked up by type-full-name + member name.
/// </summary>
public class CtorDefaultsScannerTests
{
    private const string FixtureType = "Content.Redactor.Tests.Fixtures.FixtureDefaultsDef";

    private static CtorDefaultsScanner LoadScanner()
    {
        var dll = typeof(CtorDefaultsScannerTests).Assembly.Location;
        var s = new CtorDefaultsScanner();
        s.ScanAssembly(dll);
        return s;
    }

    [Fact]
    public void Scans_Bool_True()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType);
        Assert.NotNull(map);
        // bool true compiles to ldc.i4.1 → scanner stores as int 1.
        Assert.True(map!.TryGetValue("BoolTrue", out var v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void Skips_BoolFalse_Default()
    {
        // Roslyn omits stfld when the initializer matches default(T),
        // so this field never appears in the recovered map. The editor
        // will just keep its existing `#` placeholder for these.
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType);
        Assert.NotNull(map);
        Assert.False(map!.ContainsKey("BoolFalseDefault"));
    }

    [Fact]
    public void Scans_Int_Positive_And_Negative()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.Equal(5, map["IntFive"]);
        Assert.Equal(-7, map["IntNegative"]);
    }

    [Fact]
    public void Skips_IntZero_Default()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.False(map.ContainsKey("IntZeroDefault"));
    }

    [Fact]
    public void Scans_Long_Float_Double()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.Equal(1234567890123L, map["LongBig"]);
        Assert.Equal(3.14f, (float)map["FloatPi"]!, 4);
        Assert.Equal(2.71828, (double)map["DoubleE"]!, 5);
    }

    [Fact]
    public void Scans_String_Literal()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.Equal("hello", map["StrHello"]);
    }

    [Fact]
    public void Skips_StringNull_Default()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.False(map.ContainsKey("StrNullDefault"));
    }

    [Fact]
    public void Skips_EnumMember_When_Underlying_Is_Zero()
    {
        // FixtureMood.Happy == 0, which Roslyn folds to ldc.i4.0 AND then
        // omits the stfld because the field's default(T) is already 0.
        // So enum-with-value-0 defaults are not recoverable from IL alone.
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.False(map.ContainsKey("MoodHappy"));
    }

    [Fact]
    public void Scans_AutoProperty_Default_String()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        // Auto-property backing field "<PropWithDefault>k__BackingField"
        // is surfaced under the property name itself.
        Assert.Equal("viaProp", map["PropWithDefault"]);
    }

    [Fact]
    public void Scans_AutoProperty_Default_Int()
    {
        var s = LoadScanner();
        var map = s.GetDefaultsFor(FixtureType)!;
        Assert.Equal(42, map["PropInt"]);
    }

    [Fact]
    public void NoEntry_For_Type_Without_Initializers()
    {
        var s = LoadScanner();
        // FixtureSimplePrototype has only a property without an initializer
        // (the `= ""` is on auto-property: ldstr "" → stfld backing, which
        // the scanner WILL pick up). So instead use a type known to have
        // no literal initializers: FixtureBaseEffect has only `float Probability`
        // with no initializer.
        var map = s.GetDefaultsFor("Content.Redactor.Tests.Fixtures.FixtureBaseEffect");
        Assert.True(map == null || map.Count == 0);
    }

    [Fact]
    public void Integrates_With_FieldExtractor_Default()
    {
        // End-to-end: build a FieldExtractor wired to the scanner and verify
        // that a field's Default ends up populated on the FieldMetadata.
        var testDll = typeof(CtorDefaultsScannerTests).Assembly.Location;
        var dir = Path.GetDirectoryName(testDll)!;
        var dlls = Directory.GetFiles(dir, "*.dll");
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var allPaths = dlls.Concat(Directory.GetFiles(runtimeDir, "*.dll"))
            .GroupBy(Path.GetFileName).Select(g => g.First()).ToList();
        var resolver = new PathAssemblyResolver(allPaths);
        using var mlc = new MetadataLoadContext(resolver, "System.Runtime");
        var asm = mlc.LoadFromAssemblyPath(testDll);

        var scanner = new CtorDefaultsScanner();
        scanner.ScanAssembly(testDll);
        var extractor = new FieldExtractor(new XmlDocReader(),
            new System.Collections.Generic.Dictionary<string, DataDefinitionMetadata>(),
            scanner);

        var t = asm.GetTypes().First(t => t.Name == "FixtureDefaultsDef");
        var fields = extractor.ExtractDataFields(t);

        var hello = fields.Single(f => f.Name == "StrHello");
        Assert.Equal("hello", hello.Default);

        // BoolFalseDefault / MoodHappy (value 0) are not in the IL because
        // Roslyn omits stfld for default(T) initializers. For booleans
        // FieldExtractor falls back to the C# language default (false) so
        // the editor doesn't show `#` for every unset bool.
        var falseField = fields.Single(f => f.Name == "BoolFalseDefault");
        Assert.Equal(false, falseField.Default);
    }

    // ---------- Regression tests for opaque-op / null-default handling ----------

    [Fact]
    public void Skips_OpaqueCall_But_Recovers_Following_Literal()
    {
        // Mirrors ActionComponent.CheckCanInteract = true: a preceding
        // field is initialised via a static factory call (opcode `call`),
        // which used to make the scanner bail before reaching later
        // literal initialisers.
        var s = LoadScanner();
        var map = s.GetDefaultsFor("Content.Redactor.Tests.Fixtures.FixtureOpaqueCtorDef")!;
        Assert.False(map.ContainsKey("OpaqueByCall"));
        Assert.Equal(1, map["TrueAfterCall"]);
    }

    [Fact]
    public void MultiArg_NewObj_Does_Not_Poison_Field()
    {
        // Mirrors SpriteSpecifier.Rsi(ResPath, "tester_0"): the inner
        // ldstr "second" must NOT be recorded as Wrapped's default. The
        // following plain literal must still be recovered.
        var s = LoadScanner();
        var map = s.GetDefaultsFor("Content.Redactor.Tests.Fixtures.FixtureMultiArgNewObjDef")!;
        Assert.False(map.ContainsKey("Wrapped"));
        Assert.Equal(9, map["RecoveredAfter"]);
    }

    [Fact]
    public void Skips_NonEnum_Static_LdsFld()
    {
        // TimeSpan.Zero — cross-assembly MemberRef to a non-enum static
        // field. Previously the scanner returned "Zero" as if it were an
        // enum member name.
        var s = LoadScanner();
        var map = s.GetDefaultsFor("Content.Redactor.Tests.Fixtures.FixtureStaticLdsfldDef")!;
        Assert.False(map.ContainsKey("SpanZero"));
        Assert.Equal(11, map["AfterStatic"]);
    }

    [Fact]
    public void FieldExtractor_Hides_Runtime_Only_Types()
    {
        // EntityUid / NetEntity / Nullable<EntityUid> / List<EntityUid>
        // must all be filtered out of the metadata. The plain string field
        // on the same type must survive.
        var testDll = typeof(CtorDefaultsScannerTests).Assembly.Location;
        var dir = Path.GetDirectoryName(testDll)!;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var allPaths = Directory.GetFiles(dir, "*.dll")
            .Concat(Directory.GetFiles(runtimeDir, "*.dll"))
            .GroupBy(Path.GetFileName).Select(g => g.First()).ToList();
        var resolver = new PathAssemblyResolver(allPaths);
        using var mlc = new MetadataLoadContext(resolver, "System.Runtime");
        var asm = mlc.LoadFromAssemblyPath(testDll);

        var extractor = new FieldExtractor(new XmlDocReader(),
            new System.Collections.Generic.Dictionary<string, DataDefinitionMetadata>());

        var t = asm.GetTypes().First(t => t.Name == "FixtureWithRuntimeHandlesDef");
        var fields = extractor.ExtractDataFields(t);
        var names = fields.Select(f => f.Name).ToHashSet();

        Assert.DoesNotContain("Owner", names);
        Assert.DoesNotContain("NetOwner", names);
        Assert.DoesNotContain("OptionalOwner", names);
        Assert.DoesNotContain("OwnerList", names);
        Assert.Contains("KeepMe", names);
    }
}
