using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Cecil = Mono.Cecil;

namespace DeepScan;

internal sealed record ResolvedReference(string Name, string Path, string Source);

internal sealed record SourceFile(string FullPath, string RelativePath);

/// <summary>
/// Collects sources and references the way the hub loaders do.
/// Magnetar (Legacy/References.cs) and Pulsar (Pulsar/Legacy/References.cs) hand the out-of-process compiler a list of
/// root assembly names: the game globs in the game folder plus a fixed list. RoslynReferences then resolves each name in
/// the probe directories with Mono.Cecil's SearchDirectory (".exe" before ".dll", directories in order) and follows every
/// assembly reference recursively. Names it cannot find are skipped silently. Custom references (NuGet compile files and
/// asset DLLs, via TryAddDependency) replace a closure entry of the same assembly name.
/// </summary>
internal static class References
{
    // magnetar Legacy/References.cs
    private static readonly string[] MagnetarCommon =
        ["Microsoft.CSharp", "0Harmony", "Newtonsoft.Json", "Mono.Cecil", "NLog", "PluginSdk", "System.Text.Json"];
    private static readonly string[] MagnetarGame =
        ["SpaceEngineers*.dll", "VRage*.dll", "Sandbox*.dll", "ProtoBuf*.dll", "protobuf*.dll"];

    // magnetar Pulsar/Legacy/References.cs (SE1 client)
    private static readonly string[] PulsarCommon =
        ["Microsoft.CSharp", "0Harmony", "DiscordRPC", "Newtonsoft.Json", "Mono.Cecil", "NLog"];
    private static readonly string[] PulsarWinForms = ["System.Windows.Forms", "System.Windows.Forms.DataVisualization"];
    private static readonly string[] PulsarWpf =
        ["System.Xaml", "System.Windows.Controls.Ribbon", "PresentationCore", "PresentationFramework", "WindowsBase"];
    private static readonly string[] PulsarGame = ["SpaceEngineers*.dll", "VRage*.dll", "Sandbox*.dll", "ProtoBuf*.dll"];

    private static readonly string[] ExcludeGlobs = ["VRage.Native.dll"];

    public static List<SourceFile> CollectSources(Options o, List<string> gaps, out string root)
    {
        var files = new List<SourceFile>();
        if (o.Manifest is not null)
        {
            root = Path.GetFullPath(o.Repo!);
            var doc = XDocument.Load(o.Manifest);
            var dirs = doc.Descendants().Where(e => e.Name.LocalName == "SourceDirectories")
                .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Directory"))
                .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
            // GitHubPlugin.AllowedZipPath: every *.cs in the archive whose repo-relative path starts with a listed
            // directory (ordinal, no separator check), or every *.cs when none are listed.
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsExcluded(rel, o.Exclude))
                    continue;
                if (dirs.Count == 0 || dirs.Any(d => rel.StartsWith(d, StringComparison.Ordinal)))
                    files.Add(new SourceFile(file, rel));
            }
            if (dirs.Count == 0)
                gaps.Add("manifest lists no SourceDirectories: the loader compiles every .cs file in the repository");
        }
        else
        {
            var full = o.Sources.Select(Path.GetFullPath).ToList();
            root = o.SourceRoot is not null ? Path.GetFullPath(o.SourceRoot)
                : o.Repo is not null ? Path.GetFullPath(o.Repo)
                : CommonRoot(full.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)!));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in full)
            {
                IEnumerable<string> found = Directory.Exists(p)
                    ? Directory.EnumerateFiles(p, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !IsExcluded(Path.GetRelativePath(p, f).Replace('\\', '/'), o.Exclude))
                    : File.Exists(p) ? [p] : [];
                if (!Directory.Exists(p) && !File.Exists(p))
                    gaps.Add($"source path not found: {p}");
                foreach (string f in found)
                    if (seen.Add(f))
                        files.Add(new SourceFile(f, Path.GetRelativePath(root, f).Replace('\\', '/')));
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private static bool IsExcluded(string rel, List<string> exclude) =>
        rel.Split('/').SkipLast(1).Any(part => exclude.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static string CommonRoot(IEnumerable<string> dirs)
    {
        string? common = null;
        foreach (string d in dirs)
        {
            string full = Path.GetFullPath(d).TrimEnd('\\', '/');
            if (common is null) { common = full; continue; }
            while (!full.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && !full.Equals(common, StringComparison.OrdinalIgnoreCase))
                common = Path.GetDirectoryName(common) ?? common;
        }
        return common ?? Directory.GetCurrentDirectory();
    }

    public static List<string> DefaultDefines(Options o)
    {
        // CompilerFactory.Create: TRACE, then the global flags, then DEBUG for debug builds.
        // Tools.GetCompilationSymbols(trusted): NETCOREAPP (net10 host), LINUX (non-Windows host), NETFRAMEWORK (net48
        // host), then PULSAR; Magnetar's CompilationSymbols replaces PULSAR with MAGNETAR.
        // LINUX is not added here: pass --define to model a Linux Interim host.
        var list = new List<string> { "TRACE" };
        if (o.Runtime == "net10") list.Add("NETCOREAPP");
        if (o.Runtime == "net48") list.Add("NETFRAMEWORK");
        if (o.Profile == "magnetar") list.Add("MAGNETAR");
        else if (o.Profile == "pulsar") list.Add("PULSAR");
        if (o.Debug) list.Add("DEBUG");
        return list;
    }

    public static List<ResolvedReference> Resolve(Options o, List<string> gaps, List<string> skipped)
    {
        var result = new Dictionary<string, ResolvedReference>(StringComparer.OrdinalIgnoreCase);
        if (o.Profile == "plain")
        {
            foreach (string dir in o.Refs.Concat(o.Probe))
            {
                if (!Directory.Exists(dir)) { gaps.Add($"reference folder not found: {dir}"); continue; }
                foreach (string file in Directory.EnumerateFiles(dir, "*.dll").Concat(Directory.EnumerateFiles(dir, "*.exe")))
                {
                    string? name = ManagedName(file);
                    if (name is not null && !result.ContainsKey(name))
                        result[name] = new ResolvedReference(name, file, Path.GetFileName(dir));
                }
            }
        }
        else
        {
            var probe = o.Probe.Concat(o.Refs).Where(d => { bool ok = Directory.Exists(d); if (!ok) gaps.Add($"probe folder not found: {d}"); return ok; }).ToList();
            var roots = new List<string>();
            string[] game = o.Profile == "magnetar" ? MagnetarGame : PulsarGame;
            if (o.GameDir is not null && Directory.Exists(o.GameDir))
            {
                var included = game.SelectMany(g => Directory.EnumerateFiles(o.GameDir, g));
                var excluded = ExcludeGlobs.SelectMany(g => Directory.EnumerateFiles(o.GameDir, g));
                roots.AddRange(included.Except(excluded, StringComparer.OrdinalIgnoreCase).Select(Path.GetFileNameWithoutExtension)!);
            }
            else
                gaps.Add("no --game-dir: the loader's game assembly roots are missing");
            if (o.Profile == "magnetar")
                roots.AddRange(MagnetarCommon);
            else
            {
                roots.AddRange(PulsarCommon);
                // Pulsar adds WinForms and WPF only on Windows; DataVisualization only on .NET Framework.
                roots.AddRange(o.Runtime == "net48" ? PulsarWinForms : PulsarWinForms.Take(1));
                roots.AddRange(PulsarWpf);
            }
            roots.AddRange(o.RootNames);

            var stack = new Stack<string>(roots.AsEnumerable().Reverse());
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                string name = stack.Pop();
                if (result.ContainsKey(name) || missing.Contains(name))
                    continue;
                string? file = Search(name, probe);
                if (file is null) { missing.Add(name); continue; }
                result[name] = new ResolvedReference(name, file, "closure:" + Path.GetFileName(Path.GetDirectoryName(file)));
                foreach (string dep in AssemblyReferences(file))
                    stack.Push(dep);
            }
            skipped.AddRange(missing);
            foreach (string root in new[] { "PluginSdk", "0Harmony", "Newtonsoft.Json" })
                if (o.Profile == "magnetar" && missing.Contains(root))
                    gaps.Add($"loader root '{root}' not found in the probe folders");
        }

        foreach (string file in o.RefFiles)
        {
            if (!File.Exists(file)) { gaps.Add($"reference file not found: {file}"); continue; }
            string? name = ManagedName(file);
            if (name is null) { gaps.Add($"not a managed assembly: {file}"); continue; }
            result[name] = new ResolvedReference(name, file, "custom");
        }
        return result.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Mono.Cecil BaseAssemblyResolver.SearchDirectory: each directory in order, ".exe" then ".dll".
    private static string? Search(string name, List<string> dirs)
    {
        foreach (string dir in dirs)
            foreach (string ext in new[] { ".exe", ".dll" })
            {
                string file = Path.Combine(dir, name + ext);
                if (File.Exists(file) && ManagedName(file) is not null)
                    return file;
            }
        return null;
    }

    public static string? ManagedName(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            return md.GetString(md.GetAssemblyDefinition().Name);
        }
        catch (Exception) { return null; }
    }

    private static IEnumerable<string> AssemblyReferences(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>PublicizedAssemblies.InspectSource: [IgnoresAccessChecksTo("Name")] string literals.</summary>
    public static HashSet<string> IgnoresAccessChecksTo(IEnumerable<SyntaxTree> trees)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees)
            foreach (var attr in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>())
            {
                if (!attr.Name.ToString().EndsWith("IgnoresAccessChecksTo", StringComparison.Ordinal))
                    continue;
                if (attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax lit
                    && lit.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
                    names.Add(lit.Token.ValueText);
            }
        return names;
    }

    /// <summary>Publicizer.PublicizeReference: every non-public type, field, method and property made public.</summary>
    public static MetadataReference Publicize(string path, IEnumerable<string> searchDirs)
    {
        var resolver = new Cecil.DefaultAssemblyResolver();
        foreach (string d in searchDirs) resolver.AddSearchDirectory(d);
        using var asm = Cecil.AssemblyDefinition.ReadAssembly(path, new Cecil.ReaderParameters { AssemblyResolver = resolver });
        static bool Generated(Cecil.ICustomAttributeProvider m) =>
            m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        static void Method(Cecil.MethodDefinition m, bool force)
        {
            if (!force && (Generated(m) || m.IsVirtual)) return;
            if (m.IsPrivate || m.IsAssembly || m.IsFamily || m.IsFamilyOrAssembly || m.IsFamilyAndAssembly) m.IsPublic = true;
        }
        foreach (var module in asm.Modules)
            foreach (var type in module.GetTypes())
            {
                // Same rules as magnetar Pulsar/Compiler/Publicizer.cs.
                if (!Generated(type) && !type.IsPublic)
                {
                    if (type.IsNested || type.IsNestedAssembly || type.IsNestedFamilyOrAssembly || type.IsNestedFamilyAndAssembly)
                        type.IsNestedPublic = true;
                    else
                        type.IsPublic = true;
                }
                foreach (var f in type.Fields)
                    if (!Generated(f) && (f.IsPrivate || f.IsAssembly || f.IsFamily || f.IsFamilyOrAssembly || f.IsFamilyAndAssembly))
                        f.IsPublic = true;
                foreach (var m in type.Methods)
                    Method(m, force: false);
                foreach (var p in type.Properties)
                {
                    if (Generated(p)) continue;
                    if (p.GetMethod is not null) Method(p.GetMethod, force: true);
                    if (p.SetMethod is not null) Method(p.SetMethod, force: true);
                }
            }
        asm.MainModule.Attributes |= Cecil.ModuleAttributes.ILOnly;
        var ms = new MemoryStream();
        asm.Write(ms);
        ms.Position = 0;
        return MetadataReference.CreateFromStream(ms, filePath: path);
    }
}
