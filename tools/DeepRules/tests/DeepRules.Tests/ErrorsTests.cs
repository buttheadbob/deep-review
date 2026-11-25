using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR014Tests
    {
        [Fact]
        public void EmptyCatchAndCatchExceptionReturningFalse() => Fires("DR014", """
using System;
class C
{
    public void A() { try { Work(); } catch { } }
    public bool B() { try { Work(); return true; } catch (Exception) { return false; } }
    private void Work() { }
}
""", 2);

        [Fact]
        public void UnusedExceptionVariable() => Fires("DR014", """
using System;
class C
{
    private int _failures;
    public void A() { try { Work(); } catch (Exception e) { _failures++; } }
    private void Work() { }
}
""", 1, "catch (Exception e)");

        [Fact]
        public void LoggedOrRethrownIsSilent() => Silent("DR014", """
using System;
using Game.Stubs;
class C
{
    public void A() { try { Work(); } catch (Exception e) { Log.Error(e, "work failed"); } }
    public void B() { try { Work(); } catch { Cleanup(); throw; } }
    public void D() { try { Work(); } catch (Exception) { Console.WriteLine("work failed"); } }
    private void Work() { }
    private void Cleanup() { }
}
""");

        [Fact]
        public void SpecificTypesAndUsedVariableAreSilent() => Silent("DR014", """
using System;
using System.IO;
class C
{
    private Exception? _last;
    public void A() { try { Work(); } catch (IOException) { } }
    public void B() { try { Work(); } catch (OperationCanceledException) { } }
    public void D() { try { Work(); } catch (Exception e) { _last = e; } }
    private void Work() { }
}
""");
    }

    public class DR015Tests
    {
        [Fact]
        public void RethrowInGameCallbackOverride()
        {
            var d = Fires("DR015", """
using System;
using Game.Stubs;
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    public override void UpdateBeforeSimulation()
    {
        try { Step(); }
        catch (Exception e) { Log.Error(e, "step"); throw; }
    }
    private void Step() { }
}
""", 1, "throw;", host: Host.Game);
            Assert.Contains("rethrows", d[0].GetMessage());
            Assert.Contains("MySessionComponentBase.UpdateBeforeSimulation", d[0].GetMessage());
        }

        [Fact]
        public void ThrowInTorchPluginUpdate() => Fires("DR015", """
using System;
using Torch;
public class Plugin : TorchPluginBase
{
    public override void Update() { throw new InvalidOperationException("tick"); }
}
""", 1, "throw new", host: Host.Torch);

        /// <summary>Outside game code: Dispose, finalizers, event handlers, thread and thread-pool callbacks and async void.</summary>
        [Fact]
        public void ThrowsThatEscapeAnywhere()
        {
            var d = Fires("DR015", """
using System;
using System.Threading;
using System.Threading.Tasks;
public class Worker : IDisposable
{
    private bool _open = true;
    public void Dispose() { if (!_open) throw new ObjectDisposedException(nameof(Worker)); _open = false; }
    ~Worker() { throw new InvalidOperationException("finalized"); }
    public void OnChanged(object? sender, EventArgs e) { throw new InvalidOperationException("changed"); }
    public void Start()
    {
        ThreadPool.QueueUserWorkItem(_ => { throw new InvalidOperationException("pool"); });
        new Thread(Loop).Start();
    }
    private void Loop() { throw new InvalidOperationException("loop"); }
    public async void Fire() { await Task.Yield(); throw new InvalidOperationException("fire"); }
}
""", 6, host: Host.None);
            Assert.Contains(d, x => x.GetMessage().Contains("finalizer thread"));
            Assert.Contains(d, x => x.GetMessage().Contains("used here as a thread callback"));
            Assert.Contains(d, x => x.GetMessage().Contains("code disposing this object"));
        }

        [Fact]
        public void CaughtThrowsAndNameOnlyMethodsAreSilent() => Silent("DR015", """
using System;
using System.Threading;
public class Worker : IDisposable
{
    public void Dispose() { try { throw new InvalidOperationException(); } catch (InvalidOperationException) { } }
    protected virtual void Dispose(bool disposing) { Close(); }
    public void OnChanged(object? sender, EventArgs e) { try { Close(); } catch (Exception) { Close(); } }
    public void Start() { new Thread(Loop).Start(); }
    private void Loop() { try { throw new InvalidOperationException(); } catch (Exception) { } }
    public void Update() { throw new InvalidOperationException("not a callback"); }
    public void Init(object game) { throw new InvalidOperationException("not a callback"); }
    private void Close() { }
}
""");

        [Fact]
        public void GuardedCallbackIsSilent() => Silent("DR015", """
using System;
using Game.Stubs;
using VRage.Plugins;
public class Manager { public void Tick() { } }
public class Plugin : IPlugin
{
    private readonly Manager _manager = new Manager();
    public void Update()
    {
        try { _manager.Tick(); }
        catch (Exception e) { Log.Error(e, "tick"); }
    }
    public void Init(object game)
    {
        try { if (game == null) throw new ArgumentNullException(nameof(game)); }
        catch (Exception e) { Log.Error(e, "init"); }
    }
    public void Dispose() { }
}
""", host: Host.Game);

        [Fact]
        public void PrivateHelpersAndBclOnlyDisposeAreSilent() => Silent("DR015", """
using System;
using System.Threading;
public class Manager { public void Tick() { } }
public class Plugin : IDisposable
{
    private readonly Manager _manager = new Manager();
    private Timer? _timer;
    private void Update(int n) { _manager.Tick(); }
    public void Dispose() { _timer?.Dispose(); }
}
""");
    }

    public class DR033Tests
    {
        [Fact]
        public void NoGuardInPluginUpdateAndHarmonyPostfix()
        {
            var d = Fires("DR033", """
using HarmonyLib;
using VRage.Plugins;
public class Manager { public void Tick() { } }
public class Plugin : IPlugin
{
    private readonly Manager _manager = new Manager();
    public void Init(object game) { }
    public void Update() { _manager.Tick(); }
    public void Dispose() { }
}
[HarmonyPatch(typeof(Manager), "Tick")]
public static class TickPatch
{
    public static void Postfix() { Helper.Run(); }
}
public static class Helper { public static void Run() { } }
""", 2);
            Assert.All(d, x => Assert.Contains("no try/catch", x.GetMessage()));
        }

        [Fact]
        public void SameNamesOutsideCallbacksAreSilent() => Silent("DR033", """
public class Manager { public void Tick() { } }
public class Scheduler
{
    private readonly Manager _manager = new Manager();
    public void Update() { _manager.Tick(); }
    public void Dispose() { _manager.Tick(); }
}
""");
    }
}
