using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DeepScan;

internal sealed record LoadedAnalyzerAssembly(string Path, string Assembly, string Pack, int Analyzers, int Generators, int Rules);

internal sealed record AnalyzerLoadFailure(string Path, string Error);

/// <summary>Loads each analyzer folder into its own load context; Roslyn and the BCL come from the host.</summary>
internal sealed class FolderAnalyzerLoader : IAnalyzerAssemblyLoader
{
    private readonly ConcurrentDictionary<string, FolderContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath)
    {
        string dir = Path.GetDirectoryName(fullPath)!;
        return _contexts.GetOrAdd(dir, d => new FolderContext(d)).LoadFromAssemblyPath(fullPath);
    }

    private sealed class FolderContext(string dir) : AssemblyLoadContext("analyzers:" + dir, isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // The host's Roslyn, System.* and anything already in the default context win, so analyzers bind to
            // the compiler that runs them.
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception)
            {
            }
            string candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}

internal sealed class AnalyzerSet
{
    public List<DiagnosticAnalyzer> Analyzers { get; } = [];
    public List<ISourceGenerator> Generators { get; } = [];
    public List<LoadedAnalyzerAssembly> Loaded { get; } = [];
    public List<AnalyzerLoadFailure> Failed { get; } = [];
    public Dictionary<string, string> RuleTool { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DiagnosticDescriptor> Descriptors { get; } = new(StringComparer.Ordinal);

    private static readonly string[] SkipNames = ["CodeFix", "CodeFixes", "CodeRefactoring", "Workspaces", "Microsoft.CodeAnalysis."];

    public static AnalyzerSet Load(IEnumerable<string> analyzerDirs, IEnumerable<string> generatorPaths)
    {
        var set = new AnalyzerSet();
        var loader = new FolderAnalyzerLoader();
        var files = new List<(string file, bool generatorsOnly)>();
        foreach (string dir in analyzerDirs)
        {
            if (!Directory.Exists(dir)) { set.Failed.Add(new AnalyzerLoadFailure(dir, "analyzer folder not found")); continue; }
            foreach (string f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                // Satellite resource folders (cs/, de/ ...) hold no analyzers.
                string rel = Path.GetRelativePath(dir, f);
                if (rel.Contains(Path.DirectorySeparatorChar) && Path.GetFileName(f).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (SkipNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) && !name.Contains("Analyzers", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.Contains("CodeFix", StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add((f, false));
            }
        }
        foreach (string g in generatorPaths)
        {
            if (Directory.Exists(g))
                files.AddRange(Directory.EnumerateFiles(g, "*.dll").Select(f => (f, true)));
            else if (File.Exists(g))
                files.Add((g, true));
            else
                set.Failed.Add(new AnalyzerLoadFailure(g, "generator path not found"));
        }

        foreach (var (file, generatorsOnly) in files)
        {
            string? failure = null;
            var reference = new AnalyzerFileReference(Path.GetFullPath(file), loader);
            reference.AnalyzerLoadFailed += (_, e) => failure ??= $"{e.ErrorCode}: {e.Message}{(e.TypeName is null ? "" : " (" + e.TypeName + ")")}";
            try
            {
                var analyzers = generatorsOnly ? ImmutableArray<DiagnosticAnalyzer>.Empty : reference.GetAnalyzers(LanguageNames.CSharp);
                var generators = reference.GetGenerators(LanguageNames.CSharp);
                int rules = 0;
                string asmName = Path.GetFileNameWithoutExtension(file);
                string pack = Path.GetFileName(Path.GetDirectoryName(file)) ?? asmName;
                foreach (var a in analyzers)
                {
                    ImmutableArray<DiagnosticDescriptor> supported;
                    try { supported = a.SupportedDiagnostics; }
                    catch (Exception ex) { set.Failed.Add(new AnalyzerLoadFailure(file, $"{a.GetType().FullName}.SupportedDiagnostics threw {ex.GetType().Name}: {ex.Message}")); continue; }
                    set.Analyzers.Add(a);
                    foreach (var d in supported)
                    {
                        rules++;
                        set.RuleTool.TryAdd(d.Id, asmName);
                        set.Descriptors.TryAdd(d.Id, d);
                    }
                }
                set.Generators.AddRange(generators);
                if (failure is not null)
                    set.Failed.Add(new AnalyzerLoadFailure(file, failure));
                if (analyzers.Length > 0 || generators.Length > 0)
                    set.Loaded.Add(new LoadedAnalyzerAssembly(file, asmName, pack, analyzers.Length, generators.Length, rules));
            }
            catch (Exception ex)
            {
                set.Failed.Add(new AnalyzerLoadFailure(file, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return set;
    }
}

/// <summary>An is_global analyzer config: severities and options. Section headers are ignored (global only).</summary>
internal sealed class GlobalConfig
{
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static GlobalConfig Load(IEnumerable<string> paths, IEnumerable<string> extra)
    {
        var c = new GlobalConfig();
        foreach (string path in paths)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].Trim();
                c.Options[key] = value;
            }
        }
        foreach (string kv in extra)
        {
            int eq = kv.IndexOf('=');
            if (eq > 0) c.Options[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
        }
        return c;
    }

    private static ReportDiagnostic? Parse(string? v) => v?.ToLowerInvariant() switch
    {
        "error" => ReportDiagnostic.Error,
        "warning" => ReportDiagnostic.Warn,
        "suggestion" => ReportDiagnostic.Info,
        "silent" or "refactoring" => ReportDiagnostic.Hidden,
        "none" => ReportDiagnostic.Suppress,
        "default" => ReportDiagnostic.Default,
        _ => null,
    };

    /// <summary>
    /// The severity map the compiler would build from this config. Precedence: dotnet_diagnostic.ID.severity, then
    /// dotnet_analyzer_diagnostic.category-X.severity, then dotnet_analyzer_diagnostic.severity. The two bulk keys apply
    /// only to analyzer rules that are enabled by default and configurable (as documented for the compiler);
    /// disabled-by-default rules need an explicit dotnet_diagnostic entry. Compiler (CS) ids take only explicit entries.
    /// </summary>
    public ImmutableDictionary<string, ReportDiagnostic> Severities(IEnumerable<DiagnosticDescriptor> descriptors)
    {
        var map = ImmutableDictionary.CreateBuilder<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);
        Options.TryGetValue("dotnet_analyzer_diagnostic.severity", out string? bulk);
        foreach (var d in descriptors)
        {
            if (Options.TryGetValue($"dotnet_diagnostic.{d.Id}.severity", out string? s) && Parse(s) is { } explicitSeverity)
            {
                map[d.Id] = explicitSeverity;
                continue;
            }
            if (!d.IsEnabledByDefault || d.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable) || d.CustomTags.Contains(WellKnownDiagnosticTags.Compiler))
                continue;
            if (Options.TryGetValue($"dotnet_analyzer_diagnostic.category-{d.Category}.severity", out string? cat) && Parse(cat) is { } catSeverity)
                map[d.Id] = catSeverity;
            else if (Parse(bulk) is { } bulkSeverity)
                map[d.Id] = bulkSeverity;
        }
        foreach (var (key, value) in Options)
        {
            if (!key.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase) || !key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
                continue;
            string id = key["dotnet_diagnostic.".Length..^".severity".Length];
            if (!map.ContainsKey(id) && Parse(value) is { } sev)
                map[id] = sev;
        }
        return map.ToImmutable();
    }
}

internal sealed class DictionaryOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        if (values.TryGetValue(key, out string? v)) { value = v; return true; }
        value = "";
        return false;
    }

    public override IEnumerable<string> Keys => values.Keys;
}

internal sealed class OptionsProvider(Dictionary<string, string> global, Dictionary<string, Dictionary<string, string>> perFile)
    : AnalyzerConfigOptionsProvider
{
    private readonly DictionaryOptions _global = new(global);
    private readonly ConcurrentDictionary<string, DictionaryOptions> _files = new(StringComparer.OrdinalIgnoreCase);

    public override AnalyzerConfigOptions GlobalOptions => _global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => For(tree.FilePath);

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => For(textFile.Path);

    private AnalyzerConfigOptions For(string path)
    {
        if (!perFile.TryGetValue(path, out var extra))
            return _global;
        return _files.GetOrAdd(path, _ =>
        {
            var merged = new Dictionary<string, string>(global, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in extra) merged[k] = v;
            return new DictionaryOptions(merged);
        });
    }
}

internal sealed class FileText(string path) : AdditionalText
{
    private SourceText? _text;
    public override string Path { get; } = path;

    public override SourceText? GetText(CancellationToken cancellationToken = default)
    {
        if (_text is null)
        {
            using var stream = File.OpenRead(Path);
            _text = SourceText.From(stream, Encoding.UTF8);
        }
        return _text;
    }
}
