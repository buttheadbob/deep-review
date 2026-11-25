// Runtime matrix helpers: a tiny assertion/report helper, a game-thread stand-in and repeat runners.
// Shared by every harness; copy it unchanged.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Harness;

/// <summary>
/// Results are printed one per line so run_matrix.py can compare runtimes:
///   RESULT &lt;name&gt; PASS|FAIL [&lt;exception type&gt;: &lt;message&gt;]
///   SUMMARY runtime=&lt;framework&gt; pass=&lt;n&gt; fail=&lt;n&gt;
/// The exit code is the number of failed checks (0 = all passed).
/// </summary>
public static class Matrix
{
    private static int _pass;
    private static int _fail;

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>Runs one named check. Any exception, or a false Expect inside it, fails the check.</summary>
    public static void Check(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine($"RESULT {name} PASS");
        }
        catch (Exception e)
        {
            _fail++;
            var inner = Unwrap(e);
            Console.WriteLine($"RESULT {name} FAIL {inner.GetType().FullName}: {OneLine(inner.Message)}");
            Console.WriteLine("STACK " + OneLine(inner.StackTrace ?? ""));
        }
    }

    /// <summary>Runs the body n times (for races and re-entrancy that do not show every time); fails on the first exception.</summary>
    public static void Repeat(string name, int times, Action<int> body)
    {
        Check(name, () =>
        {
            for (int i = 0; i < times; i++)
                body(i);
        });
    }

    /// <summary>Runs the body n times and reports how many runs threw, by exception type, without stopping.</summary>
    public static void Count(string name, int times, Action<int> body)
    {
        var thrown = new Dictionary<string, int>();
        var first = new Dictionary<string, string>();
        for (int i = 0; i < times; i++)
        {
            try { body(i); }
            catch (Exception e)
            {
                var inner = Unwrap(e);
                string key = inner.GetType().FullName ?? "?";
                thrown[key] = thrown.TryGetValue(key, out int n) ? n + 1 : 1;
                if (!first.ContainsKey(key))
                    first[key] = OneLine(inner.Message) + " @ " + OneLine((inner.StackTrace ?? "").Trim()).Split('|')[0].Trim();
            }
        }
        if (thrown.Count == 0)
        {
            _pass++;
            Console.WriteLine($"RESULT {name} PASS 0/{times} threw");
        }
        else
        {
            _fail++;
            var parts = new List<string>();
            foreach (var kv in thrown) parts.Add($"{kv.Key} x{kv.Value} (\"{first[kv.Key]}\")");
            Console.WriteLine($"RESULT {name} FAIL {string.Join(", ", parts)} of {times}");
        }
    }

    public static void Expect(bool condition, string what)
    {
        if (!condition)
            throw new ExpectationFailedException(what);
    }

    public static void Note(string text) => Console.WriteLine("NOTE " + OneLine(text));

    public static int Finish()
    {
        Console.WriteLine($"SUMMARY runtime={Runtime} pass={_pass} fail={_fail}");
        return Math.Min(_fail, 100);
    }

    private static Exception Unwrap(Exception e)
    {
        while ((e is AggregateException || e is System.Reflection.TargetInvocationException) && e.InnerException is not null)
            e = e.InnerException;
        return e;
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " | ");
}

public sealed class ExpectationFailedException(string message) : Exception(message);

/// <summary>
/// Stand-in for the game thread (MySandboxGame.Static.Invoke / Torch InvokeOnGameThread / Magnetar's queue): one
/// dedicated thread with NO SynchronizationContext, draining a queue. Like the game, it runs each queued action to the
/// end before the next, and continuations that complete on it run inline unless they were queued.
/// </summary>
public sealed class GameThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public GameThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "GameThread (stand-in)" };
        _thread.Start();
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Queues an action, like MySandboxGame.Static.Invoke.</summary>
    public void Invoke(Action action) => _queue.Add(action);

    /// <summary>Runs an action on the game thread and waits for it (rethrowing its exception). Never call from the game thread.</summary>
    public void Run(Action action, int timeoutMs = 10000)
    {
        if (IsCurrent) throw new InvalidOperationException("GameThread.Run called on the game thread (deadlock).");
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _queue.Add(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs)) throw new TimeoutException("The game thread did not run the action in time.");
        if (error is not null) throw new AggregateException(error);
    }

    /// <summary>Waits until everything queued so far has run.</summary>
    public void Drain(int timeoutMs = 10000) => Run(() => { }, timeoutMs);

    private void Loop()
    {
        // The game thread has no SynchronizationContext: awaits on it resume on whatever thread completes the task.
        SynchronizationContext.SetSynchronizationContext(null);
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception e) { Console.WriteLine($"GAMETHREAD-UNHANDLED {e.GetType().FullName}: {e.Message}"); }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>Waits for a condition with a time limit (polls; harness code only).</summary>
public static class Wait
{
    public static void Until(Func<bool> condition, int timeoutMs = 5000, string what = "condition")
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }
}
