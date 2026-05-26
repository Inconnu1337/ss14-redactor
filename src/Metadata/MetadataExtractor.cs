using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Editor.Editor;

/// <summary>
/// Scans compiled assemblies via MetadataLoadContext to extract
/// all IPrototype types, IComponent types and their [DataField] metadata.
/// Outputs Editor/metadata.json consumed by the web editor.
/// </summary>
public static class MetadataExtractor
{
    public static void Extract(string solutionRoot, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var serverBinDir = Path.Combine(solutionRoot, "bin", "Content.Server");
        var clientBinDir = Path.Combine(solutionRoot, "bin", "Content.Client");

        // Collect all bin directories to scan (server + client)
        var binDirs = new List<string>();
        if (Directory.Exists(serverBinDir)) binDirs.Add(serverBinDir);
        if (Directory.Exists(clientBinDir)) binDirs.Add(clientBinDir);

        if (binDirs.Count == 0)
        {
            Logger.Error("No bin directories found.");
            Logger.Error("Build Content.Server and Content.Client first (dotnet build).");
            return;
        }

        var outputPath = Path.Combine(outputDir, "metadata.json");
        var cachePath = Path.Combine(outputDir, "metadata.cache.txt");
        var fingerprint = ComputeInputFingerprint(binDirs);

        if (File.Exists(outputPath) && File.Exists(cachePath))
        {
            try
            {
                var cached = File.ReadAllText(cachePath).Trim();
                if (cached == fingerprint)
                {
                    Logger.Info($"Metadata cache hit ({Path.GetFileName(outputPath)}, fingerprint {fingerprint[..12]}…) — skipping extraction.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read metadata cache: {ex.Message} — re-extracting.");
            }
        }

        Logger.Info($"Scanning {binDirs.Count} bin directories: {string.Join(", ", binDirs.Select(Path.GetFileName))}");
        Logger.Info("Extracting prototype metadata...");

        var runtimeDlls = FindRuntimeDlls();
        if (runtimeDlls.Length == 0)
            Logger.Warn("Could not locate BCL assemblies. Metadata extraction may fail.");

        // Collect DLLs from all bin directories, dedup by filename (server takes precedence for shared DLLs)
        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in runtimeDlls)
            pathMap[Path.GetFileName(p)] = p;
        foreach (var dir in binDirs)
            foreach (var p in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                pathMap.TryAdd(Path.GetFileName(p), p);

        var resolver = new PathAssemblyResolver(pathMap.Values);
        using var mlc = new MetadataLoadContext(resolver, "System.Runtime");

        // Load XML documentation from all bin directories (accumulates across calls)
        var xmlDocs = new XmlDocReader();
        foreach (var dir in binDirs)
            xmlDocs.LoadFromDirectory(dir);
        if (xmlDocs.Count > 0)
            Logger.Info($"Loaded {xmlDocs.Count} XML doc entries");
        else
            Logger.Info("No XML documentation files found (summaries will be empty).");

        var dataDefinitions = new Dictionary<string, DataDefinitionMetadata>();
        // Pre-scan every DLL once for literal field-initializer defaults. The
        // result is keyed by Type.FullName so it matches whatever
        // MetadataLoadContext later resolves while extracting fields.
        var defaultsScanner = new CtorDefaultsScanner();
        var fieldExtractor = new FieldExtractor(xmlDocs, dataDefinitions, defaultsScanner);

        var prototypes = new Dictionary<string, PrototypeMetadata>();
        var components = new Dictionary<string, ComponentMetadata>();
        // baseFullName -> [concreteFullName,...] for polymorphic !type: picking.
        var polymorphicTypes = new Dictionary<string, List<string>>();
        // Pass-1 captures Type handles so pass-2 can extract fields once
        // the entire DD/interface registry is fully populated. Without this
        // two-pass split, a field declared as a content interface (e.g.
        // IWireAction) that hasn't yet been seen as an implementor would be
        // mis-classified as plain text.
        var dataDefTypes = new Dictionary<string, Type>();
        var protoTypes  = new Dictionary<string, Type>();
        var compTypes   = new Dictionary<string, Type>();
        var skippedAssemblies = 0;
        var skippedTypes = 0;

        // === PASS 1: Discovery ============================================
        // Scan unique DLLs from all bin directories (avoid scanning the same DLL twice)
        var scannedDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in binDirs)
        {
            foreach (var dllPath in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(dllPath);
                if (!scannedDlls.Add(fileName)) continue; // already scanned from another dir

                try
                {
                    var assembly = mlc.LoadFromAssemblyPath(dllPath);
                    DiscoverAssembly(assembly, prototypes, components, dataDefinitions,
                        polymorphicTypes, dataDefTypes, protoTypes, compTypes, xmlDocs,
                        ref skippedTypes);
                }
                catch (Exception ex)
                {
                    skippedAssemblies++;
                    Logger.Warn($"Could not load assembly {fileName}: {ex.Message}");
                }

                // Side-channel: pull literal field-initializer defaults from
                // the SAME DLL via PEReader. Independent of the MLC load above
                // — even if MLC fails (e.g. mixed-mode native lib), the
                // PEReader pass is usually still cheap and may yield results.
                try { defaultsScanner.ScanAssembly(dllPath); }
                catch (Exception ex)
                { Logger.Warn($"Defaults scan failed for {fileName}: {ex.Message}"); }
            }
        }

        // === PASS 2: Field extraction =====================================
        // dataDefinitions/polymorphicTypes are now fully populated, so
        // FieldExtractor can correctly classify interface-typed fields
        // (e.g. `IWireAction Action`) as `kind: object` polymorphic refs.
        foreach (var (full, t) in dataDefTypes)
        {
            if (!dataDefinitions.TryGetValue(full, out var dd) || dd.Fields.Count > 0) continue;
            try { dd.Fields = fieldExtractor.ExtractDataFields(t); }
            catch (Exception ex) { Logger.Warn($"Field extraction failed for {full}: {ex.Message}"); }
        }
        foreach (var (yt, t) in protoTypes)
        {
            if (!prototypes.TryGetValue(yt, out var p) || p.Fields.Count > 0) continue;
            try { p.Fields = fieldExtractor.ExtractDataFields(t); }
            catch (Exception ex) { Logger.Warn($"Field extraction failed for prototype {yt}: {ex.Message}"); }
        }
        foreach (var (cn, t) in compTypes)
        {
            if (!components.TryGetValue(cn, out var c) || c.Fields.Count > 0) continue;
            try { c.Fields = fieldExtractor.ExtractDataFields(t); }
            catch (Exception ex) { Logger.Warn($"Field extraction failed for component {cn}: {ex.Message}"); }
        }

        var metadata = new MetadataRoot
        {
            Prototypes = prototypes,
            Components = components,
            DataDefinitions = dataDefinitions,
            PolymorphicTypes = polymorphicTypes,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var json = JsonSerializer.Serialize(metadata, options);
        File.WriteAllText(outputPath, json);
        try { File.WriteAllText(cachePath, fingerprint); }
        catch (Exception ex) { Logger.Warn($"Could not write metadata cache: {ex.Message}"); }

        Logger.Info($"Extracted {prototypes.Count} prototypes, {components.Count} components, {dataDefinitions.Count} data definitions");
        Logger.Info($"Recovered literal defaults for {defaultsScanner.TypesWithDefaults} types");
        if (skippedAssemblies > 0)
            Logger.Info($"Skipped {skippedAssemblies} unloadable assemblies (native libs, etc.)");
        if (skippedTypes > 0)
            Logger.Info($"Skipped {skippedTypes} problematic types");
        Logger.Info($"Metadata written to: {outputPath}");
    }

    /// <summary>
    /// Build a deterministic fingerprint over every DLL the extractor will
    /// scan plus the editor's own version, so a content rebuild OR an
    /// editor upgrade invalidates the cache automatically. Uses
    /// path+length+last-write-time-utc per file rather than a content
    /// hash — orders of magnitude faster for the typical SS14 fork
    /// (hundreds of DLLs, ~hundreds of MB) and still detects every
    /// rebuild, since MSBuild rewrites the output DLL on every change.
    /// </summary>
    private static string ComputeInputFingerprint(IReadOnlyList<string> binDirs)
    {
        var entries = new List<string>();
        foreach (var dir in binDirs)
        {
            string[] dlls;
            try { dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);
            foreach (var p in dlls)
            {
                try
                {
                    var fi = new FileInfo(p);
                    entries.Add($"{Path.GetFileName(p)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                }
                catch { /* skip unreadable */ }
            }
        }
        // Mix in the editor's own assembly file timestamp so that any
        // rebuild (which rewrites the DLL on disk) invalidates the cache,
        // even when the semantic version hasn't changed.
        var selfVer = typeof(MetadataExtractor).Assembly.GetName().Version?.ToString() ?? "0";
        var selfPath = typeof(MetadataExtractor).Assembly.Location;
        var selfTs = "0";
        try
        {
            var fi = new FileInfo(selfPath);
            selfTs = fi.LastWriteTimeUtc.Ticks.ToString();
        }
        catch { /* ignore — version alone is the fallback */ }
        entries.Add($"__editor|{selfVer}|{selfTs}");

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries)));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void DiscoverAssembly(
        Assembly assembly,
        Dictionary<string, PrototypeMetadata> prototypes,
        Dictionary<string, ComponentMetadata> components,
        Dictionary<string, DataDefinitionMetadata> dataDefinitions,
        Dictionary<string, List<string>> polymorphicTypes,
        Dictionary<string, Type> dataDefTypes,
        Dictionary<string, Type> protoTypes,
        Dictionary<string, Type> compTypes,
        XmlDocReader xmlDocs,
        ref int skippedTypes)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
            Logger.Warn($"Partial type load for {assembly.GetName().Name} ({types.Length} types loaded)");
        }

        foreach (var type in types)
        {
            try
            {
                DiscoverType(type, prototypes, components, dataDefinitions,
                    polymorphicTypes, dataDefTypes, protoTypes, compTypes, xmlDocs);
            }
            catch (Exception ex)
            {
                skippedTypes++;
                Logger.Warn($"Could not scan type {type.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// True for interfaces declared in non-system assemblies. Such
    /// interfaces are eligible to act as <c>!type:</c> polymorphic bases
    /// even when they carry no <c>[ImplicitDataDefinitionForInheritors]</c>
    /// attribute (e.g. <c>IWireAction</c>), as long as at least one
    /// concrete DataDefinition implementor is discovered.
    /// </summary>
    private static bool IsContentAssembly(Assembly asm)
    {
        var n = asm.GetName().Name ?? "";
        if (n.Length == 0) return false;
        if (n == "mscorlib" || n == "netstandard" || n == "System") return false;
        if (n.StartsWith("System.") || n.StartsWith("Microsoft.")) return false;
        return true;
    }

    private static void DiscoverType(
        Type type,
        Dictionary<string, PrototypeMetadata> prototypes,
        Dictionary<string, ComponentMetadata> components,
        Dictionary<string, DataDefinitionMetadata> dataDefinitions,
        Dictionary<string, List<string>> polymorphicTypes,
        Dictionary<string, Type> dataDefTypes,
        Dictionary<string, Type> protoTypes,
        Dictionary<string, Type> compTypes,
        XmlDocReader xmlDocs)
    {
        // Scan DataDefinition types (BOTH abstract bases and concrete
        // implementors – abstract bases are needed so the editor can resolve
        // `List<TBase>` element types, and to pick !type: subtypes).
        //
        // A type counts as a DataDefinition if it has [DataDefinition] /
        // [ImplicitDataDefinitionForInheritors] on itself OR if any ancestor
        // has [ImplicitDataDefinitionForInheritors] (because that attribute
        // implicitly opts every subclass in – the concrete subclasses of
        // e.g. CEEntityEffect do not redeclare the attribute themselves).
        static bool HasDirectDataDefAttr(Type t) => t.CustomAttributes
            .Any(a => a.AttributeType.Name is "DataDefinitionAttribute"
                or "ImplicitDataDefinitionForInheritorsAttribute");

        static bool HasImplicitDataDefAncestor(Type t)
        {
            var b = t.BaseType;
            while (b != null && b.FullName != "System.Object")
            {
                if (b.CustomAttributes.Any(a => a.AttributeType.Name == "ImplicitDataDefinitionForInheritorsAttribute"))
                    return true;
                b = b.BaseType;
            }
            foreach (var iface in t.GetInterfaces())
            {
                if (iface.CustomAttributes.Any(a => a.AttributeType.Name == "ImplicitDataDefinitionForInheritorsAttribute"))
                    return true;
            }
            return false;
        }

        var hasDataDef = HasDirectDataDefAttr(type) || HasImplicitDataDefAncestor(type);

        if (hasDataDef)
        {
            var fullName = type.FullName ?? type.Name;
            if (!dataDefinitions.ContainsKey(fullName))
            {
                // Stub entry; pass 2 fills Fields once the registry is complete.
                dataDefinitions[fullName] = new DataDefinitionMetadata
                {
                    ClassName = fullName,
                    ShortName = type.Name,
                    Summary = xmlDocs.GetTypeSummary(type),
                    Fields = new List<FieldMetadata>(),
                };
                dataDefTypes[fullName] = type;
            }

            // Walk base chain – if any ancestor is also a DataDefinition,
            // register this concrete type as an implementor of that base.
            // Both abstract and concrete types contribute (a concrete
            // intermediate may itself be a !type: target with further
            // subclasses).
            if (!type.IsAbstract)
            {
                var baseT = type.BaseType;
                while (baseT != null && baseT.FullName != "System.Object")
                {
                    // An ancestor counts as a polymorphic !type: target if
                    // it has its own DataDefinition attribute OR if it
                    // inherits the DataDef status from a higher
                    // ImplicitDataDefinitionForInheritors ancestor. The
                    // second case is critical for chains like
                    //   ToggleIntrinsicUIEvent : InstantActionEvent
                    //   : InputComboEvent : HandledEntityEventArgs
                    // where only the topmost ancestor carries the
                    // attribute, yet the editor still needs to know that
                    // ToggleIntrinsicUIEvent is a valid !type: pick for a
                    // field of declared type InstantActionEvent.
                    var baseHasDD = HasDirectDataDefAttr(baseT) || HasImplicitDataDefAncestor(baseT);
                    if (baseHasDD)
                    {
                        var baseFull = baseT.FullName ?? baseT.Name;
                        if (!polymorphicTypes.TryGetValue(baseFull, out var impls))
                            polymorphicTypes[baseFull] = impls = new List<string>();
                        if (!impls.Contains(fullName))
                            impls.Add(fullName);
                    }
                    baseT = baseT.BaseType;
                }

                // Walk ALL implemented interfaces from content/engine assemblies.
                // We deliberately do NOT require [ImplicitDataDefinitionForInheritors]
                // on the interface itself – many polymorphic contract interfaces in
                // SS14 (e.g. IWireAction) carry no attribute but are intended as
                // !type: bases via their concrete [DataDefinition] implementors.
                // System/Microsoft interfaces are filtered out by IsContentAssembly
                // so junk markers like IComparable/IDisposable don't pollute the
                // polymorphic registry.
                foreach (var iface in type.GetInterfaces())
                {
                    if (!IsContentAssembly(iface.Assembly)) continue;
                    var ifaceFull = iface.FullName ?? iface.Name;
                    if (!polymorphicTypes.TryGetValue(ifaceFull, out var impls))
                        polymorphicTypes[ifaceFull] = impls = new List<string>();
                    if (!impls.Contains(fullName))
                        impls.Add(fullName);
                    if (!dataDefinitions.ContainsKey(ifaceFull))
                    {
                        dataDefinitions[ifaceFull] = new DataDefinitionMetadata
                        {
                            ClassName = ifaceFull,
                            ShortName = iface.Name,
                            Summary = xmlDocs.GetTypeSummary(iface),
                            Fields = new List<FieldMetadata>(),
                        };
                        // Interfaces declare no fields themselves; no Type capture needed.
                    }
                }
            }
        }

        // Scan Prototype types
        var protoAttr = type.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name is "PrototypeAttribute" or "PrototypeRecordAttribute");

        if (protoAttr != null)
        {
            var yamlType = InferPrototypeYamlType(protoAttr, type);
            var inheriting = type.GetInterfaces().Any(i => i.Name == "IInheritingPrototype");

            if (prototypes.TryAdd(yamlType, new PrototypeMetadata
            {
                ClassName = type.FullName ?? type.Name,
                YamlType = yamlType,
                Inheriting = inheriting,
                Summary = xmlDocs.GetTypeSummary(type),
                Fields = new List<FieldMetadata>(),
            }))
            {
                protoTypes[yamlType] = type;
            }
        }

        // Scan Component types
        var compAttr = type.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "RegisterComponentAttribute");

        if (compAttr != null)
        {
            var compName = InferComponentName(type);

            if (components.TryAdd(compName, new ComponentMetadata
            {
                ClassName = type.FullName ?? type.Name,
                Name = compName,
                Summary = xmlDocs.GetTypeSummary(type),
                Fields = new List<FieldMetadata>(),
            }))
            {
                compTypes[compName] = type;
            }
        }
    }

    /// <summary>
    /// Returns paths to BCL assemblies for use in PathAssemblyResolver.
    /// Works in both regular and self-contained single-file mode.
    /// In single-file mode, RuntimeEnvironment.GetRuntimeDirectory() may return an empty
    /// or non-existent path, so we fall back to TRUSTED_PLATFORM_ASSEMBLIES.
    /// </summary>
    private static string[] FindRuntimeDlls()
    {
        // Preferred: TRUSTED_PLATFORM_ASSEMBLIES — always populated by the .NET host,
        // even in self-contained single-file mode (runtime extracts to a temp dir).
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            var paths = tpa.Split(Path.PathSeparator)
                           .Where(File.Exists)
                           .ToArray();
            if (paths.Length > 0) return paths;
        }

        // Fallback: scan the runtime directory (works for regular (non-single-file) installs)
        try
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            if (Directory.Exists(runtimeDir))
                return Directory.GetFiles(runtimeDir, "*.dll");
        }
        catch { /* ignore */ }

        return Array.Empty<string>();
    }

    private static string InferPrototypeYamlType(CustomAttributeData attr, Type type)
    {
        if (attr.ConstructorArguments.Count > 0 &&
            attr.ConstructorArguments[0].Value is string name &&
            !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var typeName = type.Name;
        if (typeName.EndsWith("Prototype"))
            typeName = typeName[..^"Prototype".Length];

        return char.ToLowerInvariant(typeName[0]) + typeName[1..];
    }

    private static string InferComponentName(Type type)
    {
        var name = type.Name;
        if (name.EndsWith("Component"))
            name = name[..^"Component".Length];
        return name;
    }
}
