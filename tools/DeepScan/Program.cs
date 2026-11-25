using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeepScan;

internal sealed record DiagnosticRecord(
    string Id, string Severity, string Message, string? File, int Line, int Column, int EndLine, int EndColumn,
    string Tool, string? HelpUri, string Runtime, string Category, bool Suppressed, bool Generated, string? LineText,
    string? Member, string? DefaultSeverity, SuppressionRecord? Suppression, IReadOnlyList<string>? Tags = null);

internal sealed record SuppressionRecord(string Kind, string? Justification, string? File, int Line, string? Text);

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static int Main(string[] args)
    {
        // @file: one argument per line (long source lists exceed the Windows command-line limit).
        args = args.SelectMany(a => a.StartsWith('@') && File.Exists(a[1..])
            ? File.ReadAllLines(a[1..]).Where(l => l.Length > 0)
            : [a]).ToArray();
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Options.Usage);
            return args.Length == 0 ? 1 : 0;
        }
        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("DeepScan: " + e.Message);
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }
        try
        {
            return Run(o);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("DeepScan failed: " + e);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out!))!);
                File.WriteAllText(o.Out!, JsonSerializer.Serialize(new { schema = 1, tool = "DeepScan", error = e.ToString() }, Json));
            }
            catch (Exception)
            {
            }
            return 1;
        }
    }

    private static int Run(Options o)
    {
        var clock = Stopwatch.StartNew();
        var timing = new Dictionary<string, long>();
        var gaps = new List<string>();
        var skipped = new List<string>();

        // Sources and parse options, as RoslynCompiler.LoadSources.
        var sources = References.CollectSources(o, gaps, out string root);
        if (sources.Count == 0)
            gaps.Add("no C# sources found");
        bool loader = o.Profile != "plain";
        var langVersion = LanguageVersion.CSharp14;
        if (o.LangVersion is not null && !LanguageVersionFacts.TryParse(o.LangVersion, out langVersion))
            throw new ArgumentException($"Unknown --langversion '{o.LangVersion}'.");
        if (o.LangVersion is null && !loader)
            langVersion = LanguageVersion.Latest;
        var defines = o.Defines ?? References.DefaultDefines(o);
        var parse = CSharpParseOptions.Default.WithLanguageVersion(langVersion).WithPreprocessorSymbols(defines);

        var trees = new List<SyntaxTree>();
        var rel = new Dictionary<SyntaxTree, string>();
        foreach (var s in sources)
        {
            using var stream = File.OpenRead(s.FullPath);
            var text = SourceText.From(stream);
            var tree = CSharpSyntaxTree.ParseText(text, parse, s.FullPath);
            trees.Add(tree);
            rel[tree] = s.RelativePath;
        }
        if (o.GlobalUsings.Count > 0)
        {
            string code = string.Join("\n", o.GlobalUsings.Select(u => $"global using global::{u};"));
            var tree = CSharpSyntaxTree.ParseText(code, parse, Path.Combine(root, "obj", "DeepScan.GlobalUsings.g.cs"));
            trees.Add(tree);
            rel[tree] = "obj/DeepScan.GlobalUsings.g.cs";
        }
        timing["parse"] = clock.ElapsedMilliseconds;

        // References, as RoslynReferences plus TryAddDependency, and the publicizer for [IgnoresAccessChecksTo].
        var resolved = References.Resolve(o, gaps, skipped);
        var publicize = References.IgnoresAccessChecksTo(trees);
        var searchDirs = resolved.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var metadata = new List<MetadataReference>();
        var publicized = new List<string>();
        foreach (var r in resolved)
        {
            if (publicize.Contains(r.Name))
            {
                try
                {
                    metadata.Add(References.Publicize(r.Path, searchDirs));
                    publicized.Add(r.Name);
                    continue;
                }
                catch (Exception e)
                {
                    gaps.Add($"could not publicize {r.Name}: {e.Message} (the loader fails the build in this case)");
                }
            }
            metadata.Add(MetadataReference.CreateFromFile(r.Path));
        }
        foreach (string name in publicize.Where(n => resolved.All(r => !r.Name.Equals(n, StringComparison.Ordinal))))
            gaps.Add($"[IgnoresAccessChecksTo(\"{name}\")] names an assembly that is not referenced");
        timing["references"] = clock.ElapsedMilliseconds;

        var nullable = (o.Nullable ?? "disable") switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };
        var compileOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: o.Debug ? OptimizationLevel.Debug : OptimizationLevel.Release,
            allowUnsafe: o.Unsafe ?? loader,
            nullableContextOptions: nullable,
            warningLevel: o.WarningLevel ?? 4,
            concurrentBuild: true,
            reportSuppressedDiagnostics: true);

        // Analyzers and generators.
        var config = GlobalConfig.Load(o.GlobalConfigs, o.AnalyzerOptions);
        config.Options.TryAdd("build_property.TargetFramework", o.Runtime == "net48" ? "net48" : "net10.0");
        config.Options.TryAdd("build_property.TargetFrameworkIdentifier", o.Runtime == "net48" ? ".NETFramework" : ".NETCoreApp");
        config.Options.TryAdd("build_property.RootNamespace", o.AssemblyName);
        config.Options.TryAdd("build_property.ProjectDir", root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
        AnalyzerSet analyzers = o.NoAnalyzers ? new AnalyzerSet() : AnalyzerSet.Load(o.Analyzers, o.Generators);
        foreach (var f in analyzers.Failed)
            gaps.Add($"analyzer load failure: {f.Path}: {f.Error}");
        var severities = config.Severities(analyzers.Descriptors.Values);
        compileOptions = compileOptions.WithSpecificDiagnosticOptions(severities);

        var additional = new List<AdditionalText>();
        var perFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string projectDir = config.Options.TryGetValue("build_property.MSBuildProjectDirectory", out string? pd) ? pd : root;
        foreach (string f in o.AdditionalFiles)
        {
            if (!File.Exists(f)) { gaps.Add($"additional file not found: {f}"); continue; }
            string full = Path.GetFullPath(f);
            additional.Add(new FileText(full));
            if (full.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            {
                string target = Path.GetRelativePath(projectDir, full);
                perFile[full] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["build_metadata.AdditionalFiles.TargetPath"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(target)),
                };
                if (File.Exists(full + ".css"))
                    perFile[full]["build_metadata.AdditionalFiles.CssScope"] = "b-" + Hash(target);
            }
        }
        var provider = new OptionsProvider(new Dictionary<string, string>(config.Options, StringComparer.OrdinalIgnoreCase), perFile);

        Compilation compilation = CSharpCompilation.Create(o.AssemblyName, trees, metadata, compileOptions);
        var generatorDiagnostics = new List<Diagnostic>();
        if (analyzers.Generators.Count > 0)
        {
            var driver = CSharpGeneratorDriver.Create(analyzers.Generators, additional, parse, provider);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out var genDiags);
            generatorDiagnostics.AddRange(genDiags);
            foreach (var t in compilation.SyntaxTrees.Where(t => !rel.ContainsKey(t)))
                rel[t] = "generated/" + Path.GetFileName(t.FilePath);
        }
        timing["generators"] = clock.ElapsedMilliseconds;

        var records = new List<DiagnosticRecord>();
        var compileDiagnostics = compilation.GetDiagnostics();
        int compileErrors = compileDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        foreach (var d in compileDiagnostics.Concat(generatorDiagnostics))
            if (Keep(d, o)) records.Add(Record(d, "csc", rel, root, o.Runtime, null, compilation));
        timing["compile"] = clock.ElapsedMilliseconds;

        // Analyzer run.
        var analyzerTelemetry = new List<object>();
        var analyzerExceptions = new List<string>();
        if (analyzers.Analyzers.Count > 0)
        {
            var options = new CompilationWithAnalyzersOptions(
                new AnalyzerOptions(additional.ToImmutableArray(), provider),
                (ex, analyzer, diag) =>
                {
                    lock (analyzerExceptions)
                        if (analyzerExceptions.Count < 50)
                            analyzerExceptions.Add($"{analyzer.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
                },
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: true,
                reportSuppressedDiagnostics: true);
            var cwa = new CompilationWithAnalyzers(compilation, analyzers.Analyzers.ToImmutableArray(), options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(o.TimeoutSeconds));
            try
            {
                var result = cwa.GetAnalysisResultAsync(cts.Token).GetAwaiter().GetResult();
                foreach (var d in result.GetAllDiagnostics())
                {
                    if (!Keep(d, o)) continue;
                    string tool = analyzers.RuleTool.TryGetValue(d.Id, out string? t) ? t : "analyzer";
                    analyzers.Descriptors.TryGetValue(d.Id, out var desc);
                    records.Add(Record(d, tool, rel, root, o.Runtime, desc, compilation));
                }
                foreach (var (analyzer, info) in result.AnalyzerTelemetryInfo.OrderByDescending(kv => kv.Value.ExecutionTime).Take(15))
                    analyzerTelemetry.Add(new { analyzer = analyzer.GetType().FullName, seconds = Math.Round(info.ExecutionTime.TotalSeconds, 2) });
            }
            catch (OperationCanceledException)
            {
                gaps.Add($"analyzers timed out after {o.TimeoutSeconds} s; analyzer results are missing");
            }
            foreach (string e in analyzerExceptions)
                gaps.Add("analyzer threw: " + e);
        }
        timing["analyzers"] = clock.ElapsedMilliseconds;

        // Symbol map and call graph.
        var graph = new GraphBuilder(compilation, t => rel.TryGetValue(t, out string? r) ? r : t.FilePath);
        if (!o.NoGraph)
            graph.Build();
        timing["graph"] = clock.ElapsedMilliseconds;
        var byFile = graph.Members.GroupBy(m => m.File).ToDictionary(g => g.Key, g => g.OrderBy(m => m.End - m.Start).ToList());
        records = records.Select(r => r with { Member = r.File is not null && byFile.TryGetValue(r.File, out var ms)
            ? ms.FirstOrDefault(m => m.Start <= r.Line && r.Line <= m.End)?.Id : null }).ToList();

        // Emit for InferSharp and other IL tools.
        object? emitted = null;
        if (o.Emit is not null)
        {
            Directory.CreateDirectory(o.Emit);
            string dll = Path.Combine(o.Emit, o.AssemblyName + ".dll");
            string pdb = Path.Combine(o.Emit, o.AssemblyName + ".pdb");
            using (var dllStream = File.Create(dll))
            using (var pdbStream = File.Create(pdb))
            {
                var emit = compilation.Emit(dllStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdb));
                emitted = new { dll, pdb, success = emit.Success };
                if (!emit.Success) gaps.Add("emit failed (compile errors); no DLL for IL tools");
            }
        }

        var output = new
        {
            schema = 1,
            tool = "DeepScan",
            runtime = o.Runtime,
            profile = o.Profile,
            assemblyName = o.AssemblyName,
            sourceRoot = root,
            manifest = o.Manifest,
            langVersion = langVersion.ToDisplayString(),
            defines,
            nullable = nullable.ToString(),
            allowUnsafe = compileOptions.AllowUnsafe,
            optimization = compileOptions.OptimizationLevel.ToString(),
            warningLevel = compileOptions.WarningLevel,
            roslyn = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
            sources = sources.Select(s => s.RelativePath),
            references = resolved,
            skippedReferences = skipped,
            publicized,
            analyzers = new
            {
                loaded = analyzers.Loaded,
                failed = analyzers.Failed,
                generators = analyzers.Generators.Select(g => g.GetType().FullName),
                rules = analyzers.Descriptors.Count,
                enabledRules = severities.Count(kv => kv.Value is not ReportDiagnostic.Suppress and not ReportDiagnostic.Hidden),
                enabledRuleIds = severities.Where(kv => kv.Value is ReportDiagnostic.Warn or ReportDiagnostic.Error).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal),
                disabledRuleIds = severities.Where(kv => kv.Value is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal),
                // Rules whose descriptor carries the custom tag "ReviewOnly": input for the stage-2 review, not gate issues.
                reviewOnlyRuleIds = analyzers.Descriptors.Values.Where(d => d.CustomTags.Contains("ReviewOnly")).Select(d => d.Id).Distinct().OrderBy(k => k, StringComparer.Ordinal),
                telemetry = analyzerTelemetry,
            },
            compileErrors,
            diagnostics = records,
            members = graph.Members,
            calls = graph.Calls,
            entryPoints = graph.Entries,
            typeRefs = graph.TypeRefs,
            emitted,
            gaps,
            timing,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out!))!);
        using (var fs = File.Create(o.Out!))
            JsonSerializer.Serialize(fs, output, Json);
        Console.WriteLine($"DeepScan {o.Profile}/{o.Runtime}: {sources.Count} files, {resolved.Count} references, {analyzers.Analyzers.Count} analyzers, " +
                          $"{records.Count} diagnostics ({compileErrors} compile errors), {graph.Members.Count} members, {graph.Calls.Count} edges, {gaps.Count} gaps, {clock.Elapsed.TotalSeconds:F1}s");
        return gaps.Count > 0 ? 2 : 0;
    }

    private static string Hash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }

    private static bool Keep(Diagnostic d, Options o) =>
        d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error
        || (o.IncludeInfo && d.Severity == DiagnosticSeverity.Info && !d.Id.StartsWith("CS", StringComparison.Ordinal));

    private static DiagnosticRecord Record(Diagnostic d, string tool, Dictionary<SyntaxTree, string> rel, string root, string runtime, DiagnosticDescriptor? desc, Compilation compilation)
    {
        var loc = d.Location;
        string? file = null;
        int line = 0, col = 0, endLine = 0, endCol = 0;
        string? lineText = null;
        bool generated = false;
        if (loc.IsInSource && loc.SourceTree is { } tree)
        {
            var span = loc.GetLineSpan();
            file = rel.TryGetValue(tree, out string? r) ? r : Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/');
            generated = file.StartsWith("generated/", StringComparison.Ordinal) || file.StartsWith("obj/", StringComparison.Ordinal);
            line = span.StartLinePosition.Line + 1;
            col = span.StartLinePosition.Character + 1;
            endLine = span.EndLinePosition.Line + 1;
            endCol = span.EndLinePosition.Character + 1;
            var text = tree.GetText();
            if (span.StartLinePosition.Line < text.Lines.Count)
                lineText = text.Lines[span.StartLinePosition.Line].ToString().Trim();
        }
        string? help = d.Descriptor.HelpLinkUri;
        return new DiagnosticRecord(
            d.Id, d.Severity.ToString().ToLowerInvariant(), d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), file, line, col, endLine, endCol,
            tool, string.IsNullOrEmpty(help) ? null : help, runtime, d.Descriptor.Category, d.IsSuppressed, generated, lineText, null,
            desc?.DefaultSeverity.ToString().ToLowerInvariant(), d.IsSuppressed ? Suppression(d, compilation, rel, root) : null,
            d.Descriptor.CustomTags.Any() ? d.Descriptor.CustomTags.ToList() : null);
    }

    /// <summary>How a suppressed diagnostic was suppressed, and the reason written next to it (if any).</summary>
    private static SuppressionRecord Suppression(Diagnostic d, Compilation compilation, Dictionary<SyntaxTree, string> rel, string root)
    {
        try
        {
            var info = d.GetSuppressionInfo(compilation);
            if (info?.Attribute is { } attr)
            {
                string? justification = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Justification").Value.Value as string;
                var syntax = attr.ApplicationSyntaxReference;
                string? file = null;
                int line = 0;
                if (syntax is not null)
                {
                    var loc = syntax.GetSyntax().GetLocation();
                    file = rel.TryGetValue(syntax.SyntaxTree, out string? r) ? r : Path.GetRelativePath(root, syntax.SyntaxTree.FilePath).Replace('\\', '/');
                    line = loc.GetLineSpan().StartLinePosition.Line + 1;
                }
                return new SuppressionRecord("SuppressMessage", string.IsNullOrWhiteSpace(justification) ? null : justification.Trim(), file, line, syntax?.GetSyntax().ToString());
            }
        }
        catch (Exception)
        {
        }
        // #pragma warning disable: the nearest disable above the diagnostic that names its id (or no id) with no restore between.
        if (d.Location.SourceTree is { } tree)
        {
            var text = tree.GetText();
            int start = d.Location.GetLineSpan().StartLinePosition.Line;
            var pragma = new System.Text.RegularExpressions.Regex(@"^\s*#\s*pragma\s+warning\s+(disable|restore)\b([^/]*)(//(.*))?$");
            for (int i = start; i >= 0; i--)
            {
                string line = text.Lines[i].ToString();
                var m = pragma.Match(line);
                if (!m.Success) continue;
                var ids = m.Groups[2].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length > 0 && !ids.Contains(d.Id, StringComparer.OrdinalIgnoreCase)) continue;
                if (m.Groups[1].Value == "restore") break;
                string reason = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "";
                return new SuppressionRecord("pragma", reason.Length > 0 ? reason : null,
                    rel.TryGetValue(tree, out string? r) ? r : tree.FilePath, i + 1, line.Trim());
            }
        }
        return new SuppressionRecord("other", null, null, 0, null);
    }
}
