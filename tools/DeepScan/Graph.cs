using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeepScan;

internal sealed class MemberInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Type { get; init; }
    public required string File { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public string? Parent { get; init; }
    public string? Display { get; init; }
    public bool Async { get; init; }
    public bool AsyncVoid { get; init; }
    public bool PerTick { get; set; }
    public List<string> Attributes { get; init; } = [];
}

internal sealed record CallEdge(string From, string To, string Kind, string File, int Line);

internal sealed record EntryPoint(string Member, string Reason, string Detail, string File, int Line);

internal sealed record TypeRef(string File, int Line, string Member, string Type, string Namespace, string Assembly);

/// <summary>
/// Symbol map, call graph and entry points over the compiled sources. Members are methods, constructors, operators,
/// properties, indexers, events, accessors with bodies, field initializers, and lambdas handed to threads, tasks, timers,
/// game-thread queues or event subscriptions (pseudo-members "L:..."). Every other lambda belongs to its member.
/// </summary>
internal sealed class GraphBuilder(Compilation compilation, Func<SyntaxTree, string> relPath)
{
    public List<MemberInfo> Members { get; } = [];
    public List<CallEdge> Calls { get; } = [];
    public List<EntryPoint> Entries { get; } = [];
    public List<TypeRef> TypeRefs { get; } = [];

    private readonly Dictionary<SyntaxNode, string> _owners = [];
    private readonly Dictionary<string, MemberInfo> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string, string)> _edgeSeen = [];
    private readonly HashSet<(string, string)> _entrySeen = [];
    private readonly HashSet<(string, string)> _typeSeen = [];

    private static readonly Regex Lifecycle = new(
        "^(Init|Initialize|Dispose|Draw\\w*|HandleInput|LoadData|UnloadData|BeforeStart|Simulate\\w*|Update\\w*|\\w*Tick\\w*|Load|Unload|Start|Stop|Shutdown|OnLoaded|OnUnloading|SaveData|GetObjectBuilder|Close|OnAddedToContainer|OnBeforeRemovedFromContainer|OnAddedToScene|OnRemovedFromScene|MarkForClose)$",
        RegexOptions.Compiled);
    private static readonly Regex PerTickName = new("^(Update\\w*|Draw\\w*|HandleInput|Simulate\\w*|\\w*Tick\\w*)$", RegexOptions.Compiled);
    private static readonly HashSet<string> HarmonyAttributes = new(StringComparer.Ordinal)
    {
        "HarmonyPatch", "HarmonyPrefix", "HarmonyPostfix", "HarmonyTranspiler", "HarmonyFinalizer", "HarmonyPrepare",
        "HarmonyTargetMethod", "HarmonyTargetMethods", "HarmonyCleanup", "HarmonyReversePatch",
    };
    private static readonly HashSet<string> HarmonyMethodNames = new(StringComparer.Ordinal)
    { "Prefix", "Postfix", "Transpiler", "Finalizer", "Prepare", "TargetMethod", "TargetMethods", "Cleanup" };

    public void Build()
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            string file = relPath(tree);
            DeclareMembers(root, model, file);
        }
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            string file = relPath(tree);
            Walk(tree.GetRoot(), model, file);
        }
    }

    private static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode n) => n.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static string Id(ISymbol s)
    {
        s = s.OriginalDefinition;
        if (s is IMethodSymbol { PartialImplementationPart: { } impl }) s = impl;
        return s.GetDocumentationCommentId() ?? s.ToDisplayString();
    }

    private static bool InSource(ISymbol? s) => s is not null && s.DeclaringSyntaxReferences.Length > 0 && s.Locations.Any(l => l.IsInSource);

    private void DeclareMembers(SyntaxNode root, SemanticModel model, string file)
    {
        foreach (var node in root.DescendantNodes())
        {
            ISymbol? symbol = null;
            string kind;
            switch (node)
            {
                case BaseMethodDeclarationSyntax m: symbol = model.GetDeclaredSymbol(m); kind = m is ConstructorDeclarationSyntax ? "constructor" : m is DestructorDeclarationSyntax ? "destructor" : m is MethodDeclarationSyntax ? "method" : "operator"; break;
                case PropertyDeclarationSyntax p: symbol = model.GetDeclaredSymbol(p); kind = "property"; break;
                case IndexerDeclarationSyntax ix: symbol = model.GetDeclaredSymbol(ix); kind = "indexer"; break;
                case EventDeclarationSyntax ev: symbol = model.GetDeclaredSymbol(ev); kind = "event"; break;
                case AccessorDeclarationSyntax acc when acc.Body is not null || acc.ExpressionBody is not null: symbol = model.GetDeclaredSymbol(acc); kind = "accessor"; break;
                case VariableDeclaratorSyntax v when v.Initializer is not null && v.Parent?.Parent is FieldDeclarationSyntax: symbol = model.GetDeclaredSymbol(v); kind = "field-initializer"; break;
                case VariableDeclaratorSyntax v when v.Initializer is not null && v.Parent?.Parent is EventFieldDeclarationSyntax: symbol = model.GetDeclaredSymbol(v); kind = "event-field"; break;
                default: continue;
            }
            if (symbol is null) continue;
            string id = Id(symbol);
            _owners[node] = id;
            string? parent = node is AccessorDeclarationSyntax && symbol is IMethodSymbol { AssociatedSymbol: { } assoc } ? Id(assoc) : null;
            var method = symbol as IMethodSymbol;
            var attributes = symbol.GetAttributes().Select(a => a.AttributeClass?.Name ?? "").Where(n => n.Length > 0)
                .Select(n => n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^9] : n).ToList();
            var info = new MemberInfo
            {
                Id = id,
                Name = symbol.Name,
                Kind = kind,
                Type = symbol.ContainingType?.ToDisplayString() ?? "",
                File = file,
                Start = Line(node),
                End = EndLine(node),
                Parent = parent,
                Display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                Async = method?.IsAsync ?? false,
                AsyncVoid = method is { IsAsync: true, ReturnsVoid: true },
                Attributes = attributes,
            };
            Members.Add(info);
            _byId.TryAdd(id, info);
            if (parent is not null)
                AddEdge(parent, id, "contains", file, info.Start);
            ClassifyEntry(symbol, info, node, model);
        }
    }

    private void ClassifyEntry(ISymbol symbol, MemberInfo info, SyntaxNode node, SemanticModel model)
    {
        if (symbol is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
            return;
        string name = symbol is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } acc
            ? acc.AssociatedSymbol?.Name ?? symbol.Name : symbol.Name;

        // Overrides and implementations of members declared outside the sources: the framework calls them.
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        while (overridden is not null && InSource(overridden))
        {
            AddEdge(Id(overridden), info.Id, "override", info.File, info.Start);
            overridden = overridden switch
            {
                IMethodSymbol m => m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };
        }
        if (overridden is not null && overridden.ContainingType?.SpecialType != SpecialType.System_Object)
            AddEntry(info, "framework-override", overridden.ContainingType?.ToDisplayString() + "." + overridden.Name);

        if (symbol.ContainingType is { } type)
        {
            foreach (var iface in type.AllInterfaces)
                foreach (var im in iface.GetMembers())
                {
                    var impl = type.FindImplementationForInterfaceMember(im);
                    if (impl is null || !SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, symbol.OriginalDefinition))
                        continue;
                    if (InSource(im))
                        AddEdge(Id(im), info.Id, "override", info.File, info.Start);
                    else
                        AddEntry(info, "interface-implementation", iface.ToDisplayString() + "." + im.Name);
                }
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } && Lifecycle.IsMatch(name))
            AddEntry(info, "lifecycle-name", name);
        if (symbol is IMethodSymbol && PerTickName.IsMatch(name))
            info.PerTick = true;

        foreach (string attr in info.Attributes)
        {
            if (attr.Contains("Command", StringComparison.Ordinal))
                AddEntry(info, "command", "[" + attr + "]");
            if (HarmonyAttributes.Contains(attr))
                AddEntry(info, "harmony", "[" + attr + "]");
        }
        if (symbol is IMethodSymbol && symbol.ContainingType is { } ct
            && ct.GetAttributes().Any(a => a.AttributeClass?.Name is "HarmonyPatch" or "HarmonyPatchAttribute")
            && HarmonyMethodNames.Contains(symbol.Name))
        {
            AddEntry(info, "harmony", ct.Name + "." + symbol.Name);
            // A patch on a per-tick method runs every tick too.
            string patchText = string.Join(" ", ct.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>()
                .SelectMany(t => t.AttributeLists).Select(a => a.ToString()));
            if (Regex.IsMatch(patchText, "\"(Update\\w*|Draw\\w*|HandleInput|Simulate\\w*|\\w*Tick\\w*)\"|nameof\\([\\w.]*\\.(Update\\w*|Draw\\w*|HandleInput|Simulate\\w*|\\w*Tick\\w*)\\)"))
                info.PerTick = true;
        }
    }

    private void AddEntry(MemberInfo m, string reason, string detail)
    {
        if (_entrySeen.Add((m.Id, reason)))
            Entries.Add(new EntryPoint(m.Id, reason, detail, m.File, m.Start));
    }

    private void AddEdge(string from, string to, string kind, string file, int line)
    {
        if (from == to && kind != "contains") return;
        if (_edgeSeen.Add((from, to, kind)))
            Calls.Add(new CallEdge(from, to, kind, file, line));
    }

    private string? OwnerOf(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (_owners.TryGetValue(n, out string? id))
                return id;
        return null;
    }

    private void Walk(SyntaxNode root, SemanticModel model, string file)
    {
        // Pseudo-members for lambdas that run on another thread, a timer, an event or the game thread queue.
        foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            string? reason = LambdaReason(lambda, model, out string detail);
            if (reason is null) continue;
            string? owner = OwnerOf(lambda);
            int line = Line(lambda);
            var col = lambda.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
            string id = $"L:{owner ?? file}@{line}:{col}";
            _owners[lambda] = id;
            var info = new MemberInfo
            {
                Id = id, Name = "lambda", Kind = "lambda", Type = "", File = file, Start = line, End = EndLine(lambda),
                Parent = owner, Display = $"{reason} lambda in {owner}",
                Async = lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            };
            Members.Add(info);
            _byId.TryAdd(id, info);
            if (owner is not null) AddEdge(owner, id, "creates", file, line);
            AddEntry(info, reason, detail);
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv:
                {
                    var target = model.GetSymbolInfo(inv);
                    var s = (target.Symbol ?? target.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                    if (s is null) break;
                    s = s.ReducedFrom ?? s;
                    if (InSource(s) && OwnerOf(inv) is { } owner) AddEdge(owner, Id(s), "call", file, Line(inv));
                    // nameof(Method) handed to Harmony or reflection: the method is called from outside.
                    break;
                }
                case BaseObjectCreationExpressionSyntax create:
                {
                    var s = model.GetSymbolInfo(create).Symbol as IMethodSymbol;
                    if (InSource(s) && OwnerOf(create) is { } owner) AddEdge(owner, Id(s!), "new", file, Line(create));
                    break;
                }
                case IdentifierNameSyntax id when id.Parent is not InvocationExpressionSyntax || ((InvocationExpressionSyntax)id.Parent).Expression != id:
                    MemberReference(id, model, file);
                    break;
                case ConstructorInitializerSyntax init:
                {
                    var s = model.GetSymbolInfo(init).Symbol as IMethodSymbol;
                    if (InSource(s) && OwnerOf(init) is { } owner) AddEdge(owner, Id(s!), "call", file, Line(init));
                    break;
                }
            }
            if (node is SimpleNameSyntax name && !(node.Parent is QualifiedNameSyntax q && q.Left == node))
                RecordTypeRef(name, model, file);
        }
    }

    private void MemberReference(IdentifierNameSyntax id, SemanticModel model, string file)
    {
        // Skip the name part of an invocation like a.B(): handled by the invocation case.
        if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id && ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma)
            return;
        var info = model.GetSymbolInfo(id);
        var s = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (s is null || !InSource(s)) return;
        string? owner = OwnerOf(id);
        SyntaxNode expr = id.Parent is MemberAccessExpressionSyntax m2 && m2.Name == id ? m2 : id;

        if (id.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax nameofInv } && nameofInv.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
            && s is IMethodSymbol nm)
        {
            if (_byId.TryGetValue(Id(nm), out var mem)) AddEntry(mem, "nameof-reference", "nameof used for reflection or Harmony at " + file + ":" + Line(id));
            return;
        }

        switch (s)
        {
            case IPropertySymbol p:
            {
                bool write = expr.Parent is AssignmentExpressionSyntax asg && asg.Left == expr;
                bool compound = write && !expr.Parent.IsKind(SyntaxKind.SimpleAssignmentExpression);
                bool incdec = expr.Parent is PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression };
                if (owner is null) break;
                if (!write || compound || incdec) AddEdge(owner, AccessorId(p, p.GetMethod), "get", file, Line(id));
                if (write || incdec) AddEdge(owner, AccessorId(p, p.SetMethod), "set", file, Line(id));
                break;
            }
            case IEventSymbol e when owner is not null:
                AddEdge(owner, Id(e), "event", file, Line(id));
                break;
            case IMethodSymbol m when owner is not null && m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction:
            {
                // Method group: a delegate is created. As an event handler or a thread/timer/task body it is an entry point.
                AddEdge(owner, Id(m), "delegate", file, Line(id));
                if (!_byId.TryGetValue(Id(m), out var target)) break;
                if (expr.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } add && add.Right == expr)
                    AddEntry(target, "event-handler", "subscribed at " + file + ":" + Line(id));
                else if (expr.Parent is ArgumentSyntax arg && ArgumentReason(arg, model, out string detail) is { } reason)
                    AddEntry(target, reason, detail + " at " + file + ":" + Line(id));
                break;
            }
        }
    }

    private static string AccessorId(IPropertySymbol p, IMethodSymbol? accessor)
    {
        if (accessor is not null && accessor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is AccessorDeclarationSyntax a && (a.Body is not null || a.ExpressionBody is not null)))
            return Id(accessor);
        return Id(p);
    }

    private string? LambdaReason(AnonymousFunctionExpressionSyntax lambda, SemanticModel model, out string detail)
    {
        detail = "";
        if (lambda.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } add && add.Right == lambda)
        {
            var ev = model.GetSymbolInfo(add.Left).Symbol as IEventSymbol;
            detail = ev is null ? add.Left.ToString() : ev.ContainingType?.Name + "." + ev.Name;
            return ev?.ContainingType?.ToDisplayString() is "System.Timers.Timer" ? "timer" : "event-handler";
        }
        return lambda.Parent is ArgumentSyntax arg ? ArgumentReason(arg, model, out detail) : null;
    }

    /// <summary>What kind of code runs a delegate passed as this argument: another thread, a timer, the game thread queue.</summary>
    private static string? ArgumentReason(ArgumentSyntax arg, SemanticModel model, out string detail)
    {
        detail = "";
        SyntaxNode? call = arg.Parent?.Parent;
        ISymbol? target = call switch
        {
            InvocationExpressionSyntax inv => model.GetSymbolInfo(inv).Symbol ?? model.GetSymbolInfo(inv).CandidateSymbols.FirstOrDefault(),
            BaseObjectCreationExpressionSyntax c => model.GetSymbolInfo(c).Symbol,
            _ => null,
        };
        if (target is not IMethodSymbol method) return null;
        string type = method.ContainingType?.ToDisplayString() ?? "";
        string name = method.MethodKind == MethodKind.Constructor ? "new" : method.Name;
        detail = type + "." + name;
        if (type is "System.Threading.Thread" && name == "new") return "background-thread";
        if (type is "System.Threading.Timer" or "System.Timers.Timer" && name == "new") return "timer";
        if (type.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) && name is "Run" or "ContinueWith" or "StartNew" or "Factory")
            return "background-task";
        if (type == "System.Threading.Tasks.TaskFactory" || type.StartsWith("System.Threading.Tasks.TaskFactory<", StringComparison.Ordinal))
            return "background-task";
        if (type == "System.Threading.Tasks.Task" && name == "new") return "background-task";
        if (type == "System.Threading.ThreadPool") return "background-threadpool";
        if (type == "System.Threading.Tasks.Parallel") return "background-parallel";
        if (type == "System.Threading.CancellationToken" && name is "Register" or "UnsafeRegister") return "cancellation-callback";
        if (name is "Invoke" or "InvokeAsync" or "BeginInvoke" or "Enqueue" or "Post" &&
            (type.Contains("MySandboxGame", StringComparison.Ordinal) || type.Contains("GameThread", StringComparison.OrdinalIgnoreCase)
             || type.Contains("MainThread", StringComparison.OrdinalIgnoreCase) || type.Contains("Dispatcher", StringComparison.Ordinal)
             || type.Contains("SynchronizationContext", StringComparison.Ordinal) || type.Contains("ITorchBase", StringComparison.Ordinal)
             || type.Contains("TorchBase", StringComparison.Ordinal)))
            return "game-thread-queue";
        return null;
    }

    private void RecordTypeRef(SimpleNameSyntax name, SemanticModel model, string file)
    {
        var s = model.GetSymbolInfo(name).Symbol;
        if (s is null) return;
        INamedTypeSymbol? type = s as INamedTypeSymbol ?? s.ContainingType;
        if (type is null || InSource(type)) return;
        string asm = type.ContainingAssembly?.Name ?? "";
        if (asm.Length == 0) return;
        string full = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
        if (!_typeSeen.Add((file, full))) return;
        string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        TypeRefs.Add(new TypeRef(file, Line(name), OwnerOf(name) ?? "", full, ns, asm));
    }
}
