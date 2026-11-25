using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR001Tests
    {
        /// <summary>The token reaches an awaited Task.Delay with ConfigureAwait(false) through a method parameter: proven.</summary>
        [Fact]
        public void CancelWhoseTokenReachesAnAwaitedDelay()
        {
            var d = Fires("DR001", """
using System;
using System.Threading;
using System.Threading.Tasks;
class C
{
    private readonly object _sync = new object();
    private CancellationTokenSource _cts = new CancellationTokenSource();
    public async Task Run() { await Wait(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false); lock (_sync) { } }
    private static async Task Wait(TimeSpan delay, CancellationToken token) { var t = token; await Task.Delay(delay, t).ConfigureAwait(false); }
    public void Stop() { lock (_sync) { _cts.Cancel(); } }
}
""", 1, "_cts.Cancel()");
            Assert.Contains("C.Wait (line 8) -> Task.Delay", d[0].GetMessage());
        }

        /// <summary>A local stored into a collection field, its token through a linked source and a Register callback: proven.</summary>
        [Fact]
        public void CancelFromCollectionWithLinkedSourceAndRegister()
        {
            Fires("DR001", """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
class C
{
    private readonly object _sync = new object();
    private readonly List<CancellationTokenSource> _runs = new List<CancellationTokenSource>();
    public async Task Start(CancellationToken stopping)
    {
        var cts = new CancellationTokenSource();
        lock (_sync) _runs.Add(cts);
        await Steps(cts.Token, stopping).ConfigureAwait(false);
    }
    private static async Task Steps(CancellationToken token, CancellationToken stopping)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, stopping);
        await Task.Delay(1000, stop.Token).ConfigureAwait(false);
    }
    public void Cancel() { lock (_sync) { foreach (var run in _runs) run.Cancel(); } }
}
""", 1, "run.Cancel()");
            Fires("DR001", """
using System.Threading;
class C
{
    private readonly object _sync = new object();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private int _state;
    public C() { _cts.Token.Register(() => { lock (_sync) _state = 0; }); }
    public void Stop() { lock (_sync) { _state = 1; _cts.Cancel(); } }
}
""", 1, "_cts.Cancel()");
        }

        /// <summary>No proven inline code: a polled token, a plain await (it may resume on a context), or no token use at all.</summary>
        [Fact]
        public void UnprovenCancelIsReviewOnly()
        {
            const string source = """
using System.Threading;
using System.Threading.Tasks;
class C
{
    private readonly object _sync = new object();
    private CancellationTokenSource _a = new CancellationTokenSource();
    private CancellationTokenSource _b = new CancellationTokenSource();
    private CancellationTokenSource _c = new CancellationTokenSource();
    public void Poll() { while (!_a.Token.IsCancellationRequested) { } }
    public async Task Plain() { await Task.Delay(1000, _b.Token); }
    public void Stop() { lock (_sync) { _a.Cancel(); _b.Cancel(); _c.Cancel(); } }
}
""";
            Silent("DR001", source);
            Fires("DR035", source, 3);
        }

        [Fact]
        public void TrySetResultInsideMonitorRegion() => Fires("DR035", """
using System.Threading;
using System.Threading.Tasks;
class C
{
    private readonly object _gate = new object();
    private TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>();
    public void Finish()
    {
        bool taken = false;
        try { Monitor.Enter(_gate, ref taken); _done.TrySetResult(true); }
        finally { if (taken) Monitor.Exit(_gate); }
    }
}
""", 1, "TrySetResult");

        [Fact]
        public void SemaphoreReleaseInsideLock() => Fires("DR035", """
using System.Threading;
class C
{
    private readonly object _sync = new object();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
    private int _queued;
    public void Push() { lock (_sync) { _queued++; _signal.Release(); } }
}
""", 1, "_signal.Release()");

        [Fact]
        public void CancelAfterLockReleasedIsSilent()
        {
            const string source = """
using System.Collections.Generic;
using System.Threading;
class C
{
    private readonly object _sync = new object();
    private readonly List<CancellationTokenSource> _runs = new List<CancellationTokenSource>();
    public void Stop()
    {
        CancellationTokenSource[] runs;
        lock (_sync) { runs = _runs.ToArray(); }
        foreach (var r in runs) r.Cancel();
    }
}
""";
            Silent("DR001", source);
            Silent("DR035", source);
        }

        [Fact]
        public void LocalNotYetHandedOutIsSilent()
        {
            const string source = """
using System.Collections.Generic;
using System.Threading;
class C
{
    private readonly object _sync = new object();
    private readonly List<CancellationTokenSource> _runs = new List<CancellationTokenSource>();
    public void Start()
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            if (_runs.Count >= 8) { cts.Dispose(); return; }
            _runs.Add(cts);
        }
    }
}
""";
            Silent("DR001", source);
            Silent("DR035", source);
        }

        [Fact]
        public void AsyncContinuationsTcsIsSilent()
        {
            const string source = """
using System.Threading.Tasks;
class C
{
    private readonly object _sync = new object();
    private readonly TaskCompletionSource<int> _tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Done() { lock (_sync) { _tcs.TrySetResult(1); } }
}
""";
            Silent("DR001", source);
            Silent("DR035", source);
        }

        [Fact]
        public void SemaphoreRegionExitIsSilent()
        {
            const string source = """
using System.Threading;
using System.Threading.Tasks;
class C
{
    private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
    private int _n;
    public async Task Run()
    {
        await _sem.WaitAsync();
        try { _n++; }
        finally { _sem.Release(); }
    }
}
""";
            Silent("DR001", source);
            Silent("DR035", source);
        }
    }

    public class DR002Tests
    {
        [Fact]
        public void BodyRemovesFromSameList() => Fires("DR002", """
using System.Collections.Generic;
class C
{
    private readonly List<int> _items = new List<int>();
    public void Prune() { foreach (var x in _items) if (x < 0) _items.Remove(x); }
}
""", 1, "foreach");

        [Fact]
        public void DelegateInLoopAndRemovalElsewhere()
        {
            var d = Fires("DR002", """
using System;
using System.Collections.Generic;
class C
{
    private readonly List<Action> _handlers = new List<Action>();
    public void Fire() { foreach (var h in _handlers) h(); }
    public void Unsubscribe(Action a) { _handlers.Remove(a); }
}
""", 1);
            Assert.Contains("Unsubscribe", d[0].GetMessage());
        }

        [Fact]
        public void OwnMethodMutatesTheList() => Fires("DR002", """
using System.Collections.Generic;
class C
{
    private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
    public void Refresh() { foreach (var kv in _names) Forget(kv.Key); }
    private void Forget(int id) { _names.Remove(id); }
}
""", 1);

        [Fact]
        public void SnapshotIsSilent() => Silent("DR002", """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    private readonly List<Action> _handlers = new List<Action>();
    public void Fire() { foreach (var h in _handlers.ToArray()) h(); }
    public void Unsubscribe(Action a) { _handlers.Remove(a); }
}
""");

        [Fact]
        public void RemoveThenBreakIsSilent() => Silent("DR002", """
using System.Collections.Generic;
class C
{
    private readonly List<int> _items = new List<int>();
    public void RemoveFirstNegative()
    {
        foreach (var x in _items)
        {
            if (x < 0) { _items.Remove(x); break; }
        }
    }
}
""");

        [Fact]
        public void ConcurrentCollectionIsSilent() => Silent("DR002", """
using System;
using System.Collections.Concurrent;
class C
{
    private readonly ConcurrentDictionary<int, Action> _jobs = new ConcurrentDictionary<int, Action>();
    public void Run() { foreach (var kv in _jobs) kv.Value(); }
    public void Drop(int id) { _jobs.TryRemove(id, out _); }
}
""");

        [Fact]
        public void CallbackWithoutOtherMutationIsSilent() => Silent("DR002", """
using System;
using System.Collections.Generic;
class C
{
    private readonly List<Action> _handlers = new List<Action> { () => { } };
    public void Fire() { foreach (var h in _handlers) h(); }
}
""");
    }

    public class DR003Tests
    {
        [Fact]
        public void DelegateUnderLock() => Fires("DR003", """
using System;
class C
{
    private readonly object _sync = new object();
    private Action _callback = () => { };
    private int _state;
    public void Tick() { lock (_sync) { _state++; _callback(); } }
}
""", 1, "_callback()");

        [Fact]
        public void RunMethodOfAnotherTypeUnderLock() => Fires("DR003", """
class Step { public void RunStep(string owner) { } }
class C
{
    private readonly object _sync = new object();
    private readonly Step _step = new Step();
    public void Update() { lock (_sync) { _step.RunStep("x"); } }
}
""", 1, "RunStep");

        [Fact]
        public void InterfaceMemberUnderLock() => Fires("DR003", """
interface IHandler { void Apply(int value); }
class C
{
    private readonly object _sync = new object();
    private IHandler? _handler;
    public void Set(int v) { lock (_sync) { _handler?.Apply(v); } }
}
""", 1);

        [Fact]
        public void OwnPrivateCleanHelperIsSilent() => Silent("DR003", """
class C
{
    private readonly object _sync = new object();
    private int _count;
    public void Tick() { lock (_sync) { RunBookkeeping(); } }
    private void RunBookkeeping() { _count++; }
}
""");

        [Fact]
        public void DelegateAfterLockIsSilent() => Silent("DR003", """
using System;
class C
{
    private readonly object _sync = new object();
    private Action _callback = () => { };
    private int _state;
    public void Tick()
    {
        lock (_sync) { _state++; }
        _callback();
    }
}
""");

        [Fact]
        public void CollectionCallsAreSilent() => Silent("DR003", """
using System.Collections.Generic;
class C
{
    private readonly object _sync = new object();
    private readonly List<int> _list = new List<int>();
    public void Add(int v) { lock (_sync) { _list.Add(v); _list.Sort(); } }
}
""");
    }

    public class DR004Tests
    {
        [Fact]
        public void AsyncVoidMethod() => Fires("DR004", """
using System.Threading.Tasks;
class C { internal async void RunNow() { await Task.Delay(1); } }
""", 1, "RunNow");

        [Fact]
        public void AsyncLambdaToAction() => Fires("DR004", """
using System;
using System.Threading.Tasks;
class C { public Action Make() { Action a = async () => await Task.Delay(1); return a; } }
""", 1);

        /// <summary>Wrapping the whole body in a catch-all is the fix the message offers, so it silences the rule.</summary>
        [Fact]
        public void WholeBodyCatchAllIsSilent() => Silent("DR004", """
using System;
using System.Threading.Tasks;
class C
{
    internal async void RunNow() { try { await Task.Delay(1); } catch (Exception e) { Console.WriteLine(e); } }
    public Action Make() => async () => { try { await Task.Delay(1); } catch { } };
}
""");

        [Fact]
        public void EventHandlerIsSilent() => Silent("DR004", """
using System;
using System.Threading.Tasks;
class C { private async void OnClick(object sender, EventArgs e) { await Task.Delay(1); } }
""");

        [Fact]
        public void AsyncTaskAndFuncAreSilent() => Silent("DR004", """
using System;
using System.Threading.Tasks;
class C
{
    public async Task RunAsync() { await Task.Delay(1); }
    public Func<Task> Make() => async () => await Task.Delay(1);
}
""");
    }

    public class DR005Tests
    {
        [Fact]
        public void ResultAndWait() => Fires("DR005", """
using System.Threading.Tasks;
class C
{
    public int Get(Task<int> t) => t.Result;
    public void Block(Task t) => t.Wait();
}
""", 2);

        [Fact]
        public void GetResultAndWaitAll() => Fires("DR005", """
using System.Threading.Tasks;
class C
{
    public int Get() => Load().GetAwaiter().GetResult();
    public void All(Task a, Task b) => Task.WaitAll(a, b);
    private Task<int> Load() => Task.FromResult(1);
}
""", 2);

        [Fact]
        public void ResultAfterAwaitIsSilent() => Silent("DR005", """
using System.Threading.Tasks;
class C
{
    public async Task<int> Sum(Task<int> a, Task<int> b)
    {
        await Task.WhenAll(a, b);
        return a.Result + b.Result;
    }
}
""");

        [Fact]
        public void CompletedChecksAreSilent() => Silent("DR005", """
using System.Threading.Tasks;
class C
{
    public int Peek(Task<int> t) { if (t.IsCompleted) return t.Result; return 0; }
    public Task<int> Next(Task<int> t) => t.ContinueWith(x => x.Result + 1);
}
""");
    }

    public class DR006Tests
    {
        [Fact]
        public void FieldDisposedWhileTokenStillRead() => Fires("DR006", """
using System.Threading;
class C
{
    private CancellationTokenSource _cts = new CancellationTokenSource();
    public void Restart() { _cts.Cancel(); _cts.Dispose(); _cts = new CancellationTokenSource(); }
    public CancellationToken Token => _cts.Token;
}
""", 1, "_cts.Dispose()");

        [Fact]
        public void DictionaryEntryDisposedWhileCancelAllRuns() => Fires("DR006", """
using System.Collections.Concurrent;
using System.Threading;
class C
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _jobs = new ConcurrentDictionary<int, CancellationTokenSource>();
    public void Start(int id) { var cts = new CancellationTokenSource(); _jobs[id] = cts; }
    public void Finish(int id) { if (_jobs.TryRemove(id, out var cts)) cts.Dispose(); }
    public void CancelAll() { foreach (var c in _jobs.Values) c.Cancel(); }
}
""", 1, "cts.Dispose()");

        [Fact]
        public void CancelSideCatchesObjectDisposedIsSilent() => Silent("DR006", """
using System;
using System.Threading;
class C
{
    private CancellationTokenSource _cts = new CancellationTokenSource();
    public void Stop() { _cts.Dispose(); }
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
""");

        [Fact]
        public void LocalNeverStoredIsSilent() => Silent("DR006", """
using System.Threading;
using System.Threading.Tasks;
class C
{
    private CancellationTokenSource _shutdown = new CancellationTokenSource();
    public async Task Once()
    {
        var cts = new CancellationTokenSource();
        try { await Task.Delay(10, cts.Token); }
        finally { cts.Dispose(); }
    }
    public void Stop() => _shutdown.Cancel();
}
""");
    }

    public class DR007Tests
    {
        [Fact]
        public void ThisAndTypeof() => Fires("DR007", """
class C
{
    public void A() { lock (this) { } }
    public void B() { lock (typeof(C)) { } }
}
""", 2);

        [Fact]
        public void StringAndPublicField() => Fires("DR007", """
class C
{
    public readonly object Gate = new object();
    public void A() { lock ("name") { } }
    public void B() { lock (Gate) { } }
}
""", 2);

        [Fact]
        public void PrivateObjectIsSilent() => Silent("DR007", """
class C
{
    private readonly object _sync = new object();
    private static readonly object s_sync = new object();
    public void A() { lock (_sync) { } }
    public static void B() { lock (s_sync) { } }
}
""");

        [Fact]
        public void PrivateCollectionIsSilent() => Silent("DR007", """
using System.Collections.Generic;
class C
{
    private readonly List<int> _items = new List<int>();
    public void Add(int v) { lock (_items) { _items.Add(v); } }
}
""");
    }

    public class DR008Tests
    {
        [Fact]
        public void IfNullAssignInThreadedType() => Fires("DR008", """
using System.Threading.Tasks;
class Cache
{
    private static Cache? _instance;
    public static Cache Instance { get { if (_instance == null) _instance = new Cache(); return _instance; } }
    public void Refresh() => Task.Run(() => { });
}
""", 1);

        [Fact]
        public void CoalesceAssignInAsyncType() => Fires("DR008", """
using System.Collections.Generic;
using System.Threading.Tasks;
class C
{
    private static Dictionary<int, string>? s_names;
    public async Task<string> Name(int id)
    {
        await Task.Yield();
        s_names ??= new Dictionary<int, string>();
        return s_names.TryGetValue(id, out var n) ? n : "";
    }
}
""", 1);

        [Fact]
        public void InsideLockIsSilent() => Silent("DR008", """
using System.Threading.Tasks;
class Cache
{
    private static readonly object s_sync = new object();
    private static Cache? _instance;
    public static Cache Instance { get { lock (s_sync) { if (_instance == null) _instance = new Cache(); return _instance; } } }
    public void Refresh() => Task.Run(() => { });
}
""");

        [Fact]
        public void SingleThreadedTypeIsSilent() => Silent("DR008", """
class Cache
{
    private static Cache? _instance;
    public static Cache Instance => _instance ??= new Cache();
}
""");
    }

    public class DR009Tests
    {
        [Fact]
        public void TimerDiscarded() => Fires("DR009", """
using System.Threading;
class C { public void Start() { new Timer(_ => { }, null, 0, 1000); } }
""", 1, "new Timer");

        [Fact]
        public void TimerFieldNeverDisposedAndLocalOnly() => Fires("DR009", """
using System.Threading;
class C
{
    private Timer? _timer;
    public void Start() { _timer = new Timer(_ => { }, null, 0, 1000); }
    public void Other() { var t = new System.Timers.Timer(1000); t.Start(); }
}
""", 2);

        /// <summary>A timer other code can reach or was handed may be disposed there.</summary>
        [Fact]
        public void ReachableOrHandedOffTimersAreSilent() => Silent("DR009", """
using System.Collections.Generic;
using System.Threading;
class C
{
    public Timer? Poll;
    private Timer? _owned;
    private readonly List<System.IDisposable> _disposables = new List<System.IDisposable>();
    public void Start() { Poll = new Timer(_ => { }, null, 0, 1000); _owned = new Timer(_ => { }, null, 0, 1000); _disposables.Add(_owned); }
}
""");

        [Fact]
        public void DisposedFieldIsSilent() => Silent("DR009", """
using System;
using System.Threading;
class C : IDisposable
{
    private Timer? _timer;
    public void Start() { _timer = new Timer(_ => { }, null, 0, 1000); }
    public void Dispose() { _timer?.Dispose(); }
}
""");

        [Fact]
        public void StoppedTimersTimerAndUsingLocalAreSilent() => Silent("DR009", """
using System.Threading;
class C
{
    private readonly System.Timers.Timer _poll = new System.Timers.Timer(1000);
    public void Stop() { _poll.Stop(); }
    public void Once() { using var t = new Timer(_ => { }, null, 0, Timeout.Infinite); }
}
""");
    }
}
