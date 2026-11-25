using System.Linq;
using Microsoft.CodeAnalysis;

namespace DeepRules
{
    /// <summary>
    /// Every DeepRules diagnostic. All are warnings in the DeepReview category. A rule without the ReviewOnly tag reports
    /// only real defects that must be fixed; a ReviewOnly rule reports code a reviewer must judge, and the gate does not
    /// count it.
    /// </summary>
    internal static class Rules
    {
        private const string Category = "DeepReview";

        /// <summary>Custom tag of rules whose hits need a reviewer's judgement: reported for the stage-2 review, not counted by the gate.</summary>
        public const string ReviewOnly = "ReviewOnly";

        private static DiagnosticDescriptor Rule(string id, string title, string help, string description, params string[] tags) =>
            new DiagnosticDescriptor(id, title, "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
                description: description, helpLinkUri: "checks/core/" + help, customTags: tags);

        private static DiagnosticDescriptor Review(string id, string title, string help, string description, params string[] tags) =>
            new DiagnosticDescriptor(id, title, "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
                description: description, helpLinkUri: "checks/core/" + help, customTags: new[] { ReviewOnly }.Concat(tags).ToArray());

        // Concurrency
        public static readonly DiagnosticDescriptor DR001 = Rule("DR001", "Synchronous-continuation trigger inside a lock",
            "concurrency.md#locks-and-re-entrancy",
            "CancellationTokenSource.Cancel() inside a lock, where the source's token provably reaches code Cancel() runs inline: a Register callback, a linked source, or an awaited Task.Delay with ConfigureAwait(false).",
            WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor DR002 = Review("DR002", "Collection can change during enumeration",
            "concurrency.md#locks-and-re-entrancy",
            "A foreach over a field collection whose body changes it, or runs code that can re-enter and change it from elsewhere in the type.");

        public static readonly DiagnosticDescriptor DR003 = Review("DR003", "Unknown code runs while a lock is held",
            "concurrency.md#locks-and-re-entrancy",
            "A delegate, event, virtual or interface member, or a Run/Execute/Invoke/Handle-style method runs inside a lock and can re-enter or deadlock.");

        public static readonly DiagnosticDescriptor DR004 = Rule("DR004", "async void outside an event handler",
            "concurrency.md#async",
            "An async void method or lambda: exceptions escape to the thread pool or game thread and callers cannot await it.");

        public static readonly DiagnosticDescriptor DR005 = Review("DR005", "Sync-over-async",
            "concurrency.md#async",
            "Blocking on a Task with .Result, .Wait(), .GetAwaiter().GetResult(), Task.WaitAll or Task.WaitAny.");

        public static readonly DiagnosticDescriptor DR006 = Review("DR006", "CancellationTokenSource disposed while still reachable",
            "concurrency.md#cancellationtokensource-lifecycle",
            "A CancellationTokenSource kept in a field or collection is disposed while another method can still cancel it or read its Token.");

        public static readonly DiagnosticDescriptor DR007 = Rule("DR007", "Lock on a public or shared object",
            "concurrency.md#locks-and-re-entrancy",
            "lock(this), lock(typeof(...)), a string, a public member or a boxed value type.");

        public static readonly DiagnosticDescriptor DR008 = Review("DR008", "Unsynchronized lazy init of a static",
            "concurrency.md#shared-state-without-locks",
            "A static field initialised on first use without a lock, Lazy or Interlocked, in a type that also runs work on other threads.");

        public static readonly DiagnosticDescriptor DR009 = Rule("DR009", "Timer not kept or not disposed",
            "concurrency.md#timers-and-threads",
            "A timer that is not stored (it can be collected and stop) or is stored in a field the type never disposes or stops.");

        // Lifecycle
        public static readonly DiagnosticDescriptor DR010 = Rule("DR010", "Static mutable state",
            "lifecycle.md#checks",
            "Outside game and plugin code: a static field or settable static property assigned from instance code or from a method that is handed an instance, so every instance in the process shares and overwrites one value.");

        public static readonly DiagnosticDescriptor DR011 = Review("DR011", "Event subscription without unsubscription",
            "lifecycle.md#checks",
            "A += on another object's or a static event with no matching -= of the same handler in the type, or a lambda subscribed directly.");

        public static readonly DiagnosticDescriptor DR012 = Rule("DR012", "Process-wide hook",
            "lifecycle.md#checks",
            "AppDomain, TaskScheduler, Console or static-constructor event hooks with no matching -=.");

        public static readonly DiagnosticDescriptor DR013 = Rule("DR013", "Harmony clobbering",
            "lifecycle.md#checks",
            "Harmony.UnpatchAll, Harmony ids that are not namespaced, and PatchAll with no unpatch in a Dispose-like method.");

        // Errors
        public static readonly DiagnosticDescriptor DR014 = Rule("DR014", "Swallowed exception",
            "errors.md#checks",
            "A catch-all with no throw, no logging and no use of the exception.");

        public static readonly DiagnosticDescriptor DR015 = Rule("DR015", "Exception escapes a callback",
            "errors.md#checks",
            "A throw or rethrow that nothing catches in a game or Torch callback override, a Harmony patch, Dispose, a finalizer, an event handler, a thread, thread-pool or timer callback, or an async void method.");

        // Persistence
        public static readonly DiagnosticDescriptor DR016 = Review("DR016", "Non-atomic overwrite",
            "persistence.md#writes",
            "A file is overwritten in place instead of written to a temporary file and moved over the original.");

        public static readonly DiagnosticDescriptor DR017 = Review("DR017", "Enum persisted by implicit number",
            "persistence.md#formats-and-migration",
            "An enum whose members have implicit values is stored in a serialized type or read from numbers, or an external number is cast to an enum unchecked.");

        public static readonly DiagnosticDescriptor DR018 = Review("DR018", "Culture-sensitive text in data",
            "persistence.md#writes",
            "Numbers and dates formatted or parsed with the server's current culture.");

        public static readonly DiagnosticDescriptor DR019 = Review("DR019", "TimeSpan from text",
            "persistence.md#configuration-semantics",
            "TimeSpan.Parse without a format, and config TimeSpans used as periods or intervals without a check that they are above zero.");

        public static readonly DiagnosticDescriptor DR020 = Review("DR020", "Wall clock for durations",
            "persistence.md#dr020",
            "DateTime.Now used in arithmetic, comparisons or stored state.");

        // Inputs
        public static readonly DiagnosticDescriptor DR021 = Review("DR021", "Path from input",
            "inputs.md#paths",
            "A file path built from a string parameter with no GetFullPath + StartsWith check and no Path.GetFileName.");

        public static readonly DiagnosticDescriptor DR022 = Review("DR022", "Arithmetic on money/counts without bounds",
            "inputs.md#values",
            "Balances, credits, amounts and counts changed by a parameter with no checked, no clamp and no comparison.");

        // Performance
        public static readonly DiagnosticDescriptor DR023 = Rule("DR023", "Work in a per-frame/per-tick method",
            "performance.md#hot-path-checks-per-tick-per-frame-per-entity",
            "LINQ, allocations, string building, closures, reflection, file IO or logging on the unconditional path of an override of a member the game calls every frame, of a Harmony patch on one, or of a method those reach through unconditional direct calls.",
            WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor DR024 = Review("DR024", "Logging inside an unbounded loop or per-tick method without rate limiting",
            "performance.md#hot-path-checks-per-tick-per-frame-per-entity",
            "A logging call in a while/do/for(;;) loop, or in a method that runs every frame (as DR023), with no time, counter or last-value guard.",
            WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor DR025 = Review("DR025", "Unbounded growth",
            "performance.md#dr025",
            "A private field collection the type adds to but never removes from, clears or caps.");

        // Correctness
        public static readonly DiagnosticDescriptor DR026 = Rule("DR026", "Non-transitive or overflowing comparer",
            "correctness.md#ordering",
            "A comparer that uses an epsilon test or returns a - b.");

        public static readonly DiagnosticDescriptor DR027 = Review("DR027", "break that should probably be continue",
            "correctness.md#logic",
            "A loop break whose condition only tests the current element.");

        public static readonly DiagnosticDescriptor DR028 = Review("DR028", "Remove-before-add replacement",
            "correctness.md#ordering",
            "An old item is removed before its replacement is added, with no try/finally to restore it.");

        public static readonly DiagnosticDescriptor DR029 = Review("DR029", "Debug-only behaviour",
            "correctness.md#dr029",
            "Behaviour inside #if DEBUG / LOCAL_BUILD / TRACE regions, or only in their #else branch.");

        public static readonly DiagnosticDescriptor DR030 = Rule("DR030", "Nullable annotations in a compile with nullable disabled",
            "correctness.md#dr030",
            "Nullable reference annotations or null-forgiving operators outside a #nullable enable region, in a compilation whose nullable annotation context is off.");

        public static readonly DiagnosticDescriptor DR031 = Review("DR031", "Reflection by name without a null guard",
            "correctness.md#dr031",
            "A member looked up by a string name and used without a null check, or BindingFlags that cannot find it.");

        public static readonly DiagnosticDescriptor DR032 = Rule("DR032", "Off-the-game-thread entity lookup",
            "correctness.md#dr032",
            "Entity lookups by id or name in Task.Run, thread or timer callbacks, or after an await with ConfigureAwait(false).");

        // Review-only parts split from DR015 and DR010
        public static readonly DiagnosticDescriptor DR033 = Review("DR033", "Game callback with no exception guard",
            "errors.md#checks",
            "A game or Torch callback override or Harmony patch with no try/catch that calls plugin or game code.");

        public static readonly DiagnosticDescriptor DR035 = Review("DR035", "Continuation trigger inside a lock (no proven waiter)",
            "concurrency.md#locks-and-re-entrancy",
            "Cancelling a token, completing a TaskCompletionSource or releasing a SemaphoreSlim inside a lock, where no code that the call runs inline is proven: it can run waiting continuations on the calling thread while the lock is held.",
            WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor DR034 = Review("DR034", "Static state not reset on unload",
            "lifecycle.md#checks",
            "In game and plugin code: static mutable fields, static collections and static events that the type never resets in Dispose/Unload/Reset/Clear.");

        public static readonly DiagnosticDescriptor DR037 = Rule("DR037", "HTTP to a host not shown in the code or not allowed",
            "security.md#code",
            "A request whose URI does not show its host in the code, a host outside the allowlist (deep_review.http_allowed_hosts, Discord by default), or an HttpClient created anywhere but a static readonly field.");

        public static readonly DiagnosticDescriptor DR038 = Rule("DR038", "Process.Start of anything but a fixed local web page",
            "security.md#code",
            "Process.Start(string) or Process.Start(ProcessStartInfo) whose target is not a URL with a loopback host (127.0.0.1, localhost or [::1]) shown as constant text in the code, or that sets Arguments or a verb other than \"open\"; Process.Start used as a method group; and the Process.Start overloads that BannedSymbols.txt does not list.");

        public static readonly DiagnosticDescriptor DR039 = Rule("DR039", "SHA-1 outside the WebSocket handshake answer",
            "security.md#code",
            "SHA1 (or a type derived from it) used for anything but the RFC 6455 handshake answer: Convert.ToBase64String of the SHA-1 of the request's Sec-WebSocket-Key header value plus the constant GUID 258EAFA5-E914-47DA-95CA-C5AB0DC85B11, used only as the Sec-WebSocket-Accept value; and a SHA-1 that looks like the handshake but hashes other bytes or sends its result elsewhere.");

        public static readonly DiagnosticDescriptor DR036 = Review("DR036", "Work on a guarded or indirect path of a per-frame or timer method",
            "performance.md#hot-path-checks-per-tick-per-frame-per-entity",
            "LINQ, allocations, capturing closures, reflection or file IO in code that a per-frame override or a timer callback reaches, where DR023 cannot prove it runs every time: behind a guard, branch or loop, inside a lambda, through a method group, or through a virtual call to an override. A reviewer judges how often it really runs.",
            WellKnownDiagnosticTags.CompilationEnd);
    }
}
