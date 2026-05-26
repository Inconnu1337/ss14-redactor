using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Content.Editor.Tests;

using Content.Editor.Editor;

/// <summary>
/// Loads this very test assembly inside a <see cref="MetadataLoadContext"/> and
/// inspects the fixture types via <see cref="FieldExtractor"/> — mirrors how
/// the production code reads compiled SS14 DLLs.
/// </summary>
public class FieldExtractorTests : IDisposable
{
    private readonly MetadataLoadContext _mlc;
    private readonly Assembly _asm;
    private readonly FieldExtractor _extractor;

    public FieldExtractorTests()
    {
        var testDll = typeof(FieldExtractorTests).Assembly.Location;
        var dir = Path.GetDirectoryName(testDll)!;
        // Resolver covers the test DLL plus all DLLs alongside it (BCL + xunit).
        var dlls = Directory.GetFiles(dir, "*.dll");
        // Include runtime BCL.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var allPaths = dlls.Concat(Directory.GetFiles(runtimeDir, "*.dll"))
            .GroupBy(Path.GetFileName).Select(g => g.First()).ToList();
        var resolver = new PathAssemblyResolver(allPaths);
        _mlc = new MetadataLoadContext(resolver, "System.Runtime");
        _asm = _mlc.LoadFromAssemblyPath(testDll);
        _extractor = new FieldExtractor(new XmlDocReader(), new Dictionary<string, DataDefinitionMetadata>());
    }

    public void Dispose() => _mlc.Dispose();

    private Type Get(string name) =>
        _asm.GetTypes().First(t => t.Name == name);

    [Fact]
    public void ExtractDataFields_FindsAllAnnotatedMembers()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);

        // Spot check a few — Id, Parent, Abstract, Name, renamed, Enabled, Mood, Flags, Tags, Counters, Nested.
        Assert.Contains(fields, f => f.Name == "Id" && f.IsId && f.Tag == "id");
        Assert.Contains(fields, f => f.Name == "Parent" && f.IsParent && f.Tag == "parent");
        Assert.Contains(fields, f => f.Name == "Abstract" && f.IsAbstract && f.Tag == "abstract");
        Assert.Contains(fields, f => f.Name == "Count" && f.Tag == "renamed");
        Assert.Contains(fields, f => f.Name == "Enabled" && f.Required);
    }

    [Fact]
    public void ExtractDataFields_DefaultTagIsCamelCased()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);

        var name = Assert.Single(fields, f => f.Name == "Name");
        Assert.Equal("name", name.Tag);
    }

    [Fact]
    public void ExtractDataFields_BooleanKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var enabled = fields.Single(f => f.Name == "Enabled");
        Assert.Equal("boolean", enabled.FieldKind);
    }

    [Fact]
    public void ExtractDataFields_IntegerKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var count = fields.Single(f => f.Name == "Count");
        Assert.Equal("integer", count.FieldKind);
    }

    [Fact]
    public void ExtractDataFields_EnumKind_HasValues()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var mood = fields.Single(f => f.Name == "Mood");
        Assert.Equal("enum", mood.FieldKind);
        Assert.NotNull(mood.EnumValues);
        Assert.Contains("Happy", mood.EnumValues!);
        Assert.Contains("Sad", mood.EnumValues);
        Assert.Contains("Neutral", mood.EnumValues);
    }

    [Fact]
    public void ExtractDataFields_FlagsKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var flags = fields.Single(f => f.Name == "Flags");
        Assert.Equal("flags", flags.FieldKind);
    }

    [Fact]
    public void ExtractDataFields_ListKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var tags = fields.Single(f => f.Name == "Tags");
        Assert.Equal("list", tags.FieldKind);
        Assert.Equal("text", tags.Element?.Kind);
    }

    [Fact]
    public void ExtractDataFields_MapKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var counters = fields.Single(f => f.Name == "Counters");
        Assert.Equal("map", counters.FieldKind);
        Assert.Equal("text", counters.Key?.Kind);
        Assert.Equal("integer", counters.Value?.Kind);
    }

    [Fact]
    public void ExtractDataFields_DeeplyNestedCollection_BuildsRecursiveNode()
    {
        // List<Dictionary<string, Dictionary<string, string>>> – the editor
        // must see the full recursive shape so it can render a list-of-map-
        // of-map editor without bottoming out into a "TODO: Serialize" stub.
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var deep = fields.Single(f => f.Name == "DeepLayers");

        Assert.Equal("list", deep.FieldKind);
        Assert.Equal("map", deep.Element?.Kind);
        Assert.Equal("text", deep.Element?.Key?.Kind);
        Assert.Equal("map", deep.Element?.Value?.Kind);
        Assert.Equal("text", deep.Element?.Value?.Key?.Kind);
        Assert.Equal("text", deep.Element?.Value?.Value?.Kind);
    }

    [Fact]
    public void ExtractDataFields_DataDefinitionRefKind()
    {
        var t = Get("FixtureEntityPrototype");
        var fields = _extractor.ExtractDataFields(t);
        var nested = fields.Single(f => f.Name == "Nested");
        Assert.Equal("object", nested.FieldKind);
    }

    [Fact]
    public void ExtractDataFields_NonAnnotated_AreSkipped()
    {
        // FixtureSimplePrototype only has Id (which is annotated). No extra fields should appear.
        var t = Get("FixtureSimplePrototype");
        var fields = _extractor.ExtractDataFields(t);
        Assert.Single(fields);
        Assert.Equal("Id", fields[0].Name);
    }

    [Fact]
    public void ExtractDataFields_InheritsFromBase()
    {
        // FixtureConcreteEffect inherits FixtureBaseEffect (which has Probability).
        var t = Get("FixtureConcreteEffect");
        var fields = _extractor.ExtractDataFields(t);
        Assert.Contains(fields, f => f.Name == "Probability");
        Assert.Contains(fields, f => f.Name == "Message");
    }
}
