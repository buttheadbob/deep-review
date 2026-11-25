using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DeepRules.Tests
{
    public enum Tfm
    {
        Net10,
        Net48
    }

    /// <summary>Which stub assemblies a snippet references.</summary>
    [Flags]
    public enum Host
    {
        /// <summary>No game, Harmony or Torch reference: ordinary .NET code.</summary>
        None = 0,
        /// <summary>The game stubs, compiled as an assembly named Sandbox.Game.</summary>
        Game = 1,
        /// <summary>The Harmony stubs, compiled as an assembly named 0Harmony.</summary>
        Harmony = 2,
        /// <summary>The Torch stubs, compiled as an assembly named Torch.API.</summary>
        Torch = 4,
        /// <summary>Game and Harmony: the default, a client or server plugin.</summary>
        All = Game | Harmony
    }

    /// <summary>
    /// Compiles a snippet against .NET 10 or .NET Framework 4.8 reference assemblies plus the chosen stub assemblies, with no
    /// preprocessor symbols (like the hub loaders), runs every DeepRules analyzer and returns its diagnostics.
    /// </summary>
    public static class Harness
    {
        private static readonly Lazy<ImmutableArray<MetadataReference>> Net10Refs = new(() =>
        {
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
            return tpa.Split(Path.PathSeparator)
                .Where(p => p.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToImmutableArray();
        });

        private static readonly Lazy<ImmutableArray<MetadataReference>> Net48Refs = new(() =>
        {
            var dir = typeof(Harness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "Net48RefDir").Value ?? "";
            var names = new[] { "mscorlib", "System", "System.Core", "System.Xml", "System.Xml.Linq", "System.Runtime.Serialization", "System.Numerics", "Microsoft.CSharp", "System.Net.Http" };
            return names.Select(n => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(dir, n + ".dll"))).ToImmutableArray();
        });

        private static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers = typeof(ConcurrencyAnalyzer).Assembly.GetTypes()
            .Where(t => typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();

        private static readonly ConcurrentDictionary<(string, Tfm, Host, NullableContextOptions, OutputKind), ImmutableArray<Diagnostic>> Cache = new();
        private static readonly ConcurrentDictionary<(Tfm, string, string), MetadataReference> StubAssemblies = new();

        private static ImmutableArray<MetadataReference> BaseRefs(Tfm tfm) => tfm == Tfm.Net10 ? Net10Refs.Value : Net48Refs.Value;

        /// <summary>A stub assembly with the given name and source, compiled once per reference set.</summary>
        private static MetadataReference StubAssembly(Tfm tfm, string name, string source) => StubAssemblies.GetOrAdd((tfm, name, source), _ =>
        {
            var comp = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: name + ".cs") },
                BaseRefs(tfm), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
            using var ms = new MemoryStream();
            var result = comp.Emit(ms);
            if (!result.Success)
                throw new InvalidOperationException("Stub assembly " + name + " does not compile on " + tfm + ":\n" +
                                                    string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
            return MetadataReference.CreateFromImage(ms.ToArray());
        });

        public static ImmutableArray<Diagnostic> Analyze(string source, Tfm tfm = Tfm.Net10, Host host = Host.All,
            NullableContextOptions nullable = NullableContextOptions.Enable, OutputKind output = OutputKind.DynamicallyLinkedLibrary) =>
            Cache.GetOrAdd((source, tfm, host, nullable, output), key => Run(key.Item1, key.Item2, StubRefs(key.Item2, key.Item3), key.Item4, key.Item5));

        /// <summary>The snippet on .NET 10 with only empty assemblies of the given names referenced (to test which names make a game or plugin context).</summary>
        public static ImmutableArray<Diagnostic> AnalyzeReferencing(string source, params string[] assemblyNames) =>
            Run(source, Tfm.Net10, assemblyNames.Select(n => StubAssembly(Tfm.Net10, n, "namespace Empty { }")).ToList(),
                NullableContextOptions.Enable, OutputKind.DynamicallyLinkedLibrary);

        private static List<MetadataReference> StubRefs(Tfm tfm, Host host)
        {
            var refs = new List<MetadataReference>();
            if (host.HasFlag(Host.Game)) refs.Add(StubAssembly(tfm, "Sandbox.Game", Stubs.Game));
            if (host.HasFlag(Host.Harmony)) refs.Add(StubAssembly(tfm, "0Harmony", Stubs.Harmony));
            if (host.HasFlag(Host.Torch)) refs.Add(StubAssembly(tfm, "Torch.API", Stubs.Torch));
            return refs;
        }

        private static ImmutableArray<Diagnostic> Run(string source, Tfm tfm, List<MetadataReference> extra, NullableContextOptions nullable, OutputKind output)
        {
            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, parse, path: "Test.cs") };
            var compilation = CSharpCompilation.Create("Snippet", trees, BaseRefs(tfm).AddRange(extra),
                new CSharpCompilationOptions(output, nullableContextOptions: nullable, allowUnsafe: true));

            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("Snippet does not compile on " + tfm + ":\n" + string.Join("\n", errors.Take(10)));

            var exceptions = new List<string>();
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                (ex, analyzer, diag) => { lock (exceptions) exceptions.Add(analyzer.GetType().Name + ": " + ex); },
                concurrentAnalysis: false, logAnalyzerExecutionTime: false);
            var diags = compilation.WithAnalyzers(Analyzers, options).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
            if (exceptions.Count > 0) throw new InvalidOperationException("Analyzer threw:\n" + string.Join("\n", exceptions));
            var ad = diags.Where(d => d.Id == "AD0001").ToList();
            if (ad.Count > 0) throw new InvalidOperationException("Analyzer failure:\n" + string.Join("\n", ad));
            return diags.Where(d => d.Location.SourceTree?.FilePath == "Test.cs").OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
        }

        public static List<Diagnostic> Get(string id, string source, Tfm tfm = Tfm.Net10, Host host = Host.All,
            NullableContextOptions nullable = NullableContextOptions.Enable, OutputKind output = OutputKind.DynamicallyLinkedLibrary) =>
            Analyze(source, tfm, host, nullable, output).Where(d => d.Id == id).ToList();

        public static int LineOf(Diagnostic d) => d.Location.GetLineSpan().StartLinePosition.Line + 1;

        public static string LineText(Diagnostic d) =>
            d.Location.SourceTree!.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString();

        /// <summary>
        /// Asserts the rule fires (exactly <paramref name="count"/> times when given) and, when <paramref name="onLine"/> is given,
        /// that some diagnostic starts on a line containing that text. Runs on net10 and, unless disabled, net48 with the same result.
        /// </summary>
        public static List<Diagnostic> Fires(string id, string source, int? count = null, string? onLine = null, bool net48 = true, Host host = Host.All,
            NullableContextOptions nullable = NullableContextOptions.Enable, OutputKind output = OutputKind.DynamicallyLinkedLibrary)
        {
            var found = Get(id, source, Tfm.Net10, host, nullable, output);
            Assert.True(found.Count > 0, id + " did not fire. Diagnostics: " + Describe(Analyze(source, Tfm.Net10, host, nullable, output)));
            if (count.HasValue) Assert.True(found.Count == count.Value, id + " fired " + found.Count + " times, expected " + count + ": " + Describe(found));
            if (onLine != null) Assert.True(found.Any(d => LineText(d).Contains(onLine)), id + " not on a line containing '" + onLine + "': " + Describe(found));
            foreach (var d in found)
            {
                var msg = d.GetMessage();
                Assert.False(string.IsNullOrWhiteSpace(msg));
                Assert.False(msg.StartsWith("lead:", StringComparison.Ordinal), "message still has the lead: prefix: " + msg);
                Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
                Assert.Equal("DeepReview", d.Descriptor.Category);
            }
            if (net48)
            {
                var old = Get(id, source, Tfm.Net48, host, nullable, output);
                Assert.True(old.Count == found.Count, id + " fired " + found.Count + " times on net10 but " + old.Count + " on net48: " + Describe(old));
            }
            return found;
        }

        public static void Silent(string id, string source, bool net48 = true, Host host = Host.All,
            NullableContextOptions nullable = NullableContextOptions.Enable, OutputKind output = OutputKind.DynamicallyLinkedLibrary)
        {
            var found = Get(id, source, Tfm.Net10, host, nullable, output);
            Assert.True(found.Count == 0, id + " fired unexpectedly: " + Describe(found));
            if (net48)
            {
                var old = Get(id, source, Tfm.Net48, host, nullable, output);
                Assert.True(old.Count == 0, id + " fired unexpectedly on net48: " + Describe(old));
            }
        }

        public static string Describe(IEnumerable<Diagnostic> diags) =>
            "\n" + string.Join("\n", diags.Select(d => d.Id + " line " + LineOf(d) + ": " + d.GetMessage()));
    }
}
