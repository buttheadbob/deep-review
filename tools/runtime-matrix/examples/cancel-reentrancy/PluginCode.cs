// Plugin code under test, pasted read-only from SensEssentials at commit 0b51e8aa912ee98a80e218fbbb0960682931df55:
//   Shared/Config/AutoCommand.cs  (RunNow, Cancel, IsRunning and the fields they use; the class is renamed
//                                  AutoCommandOriginal so the fixed copy can sit next to it)
//   Shared/Core/CommandSequence.cs (Run, verbatim)
//   Shared/Core/GameThread.cs      (InvokeSafe, InvokeAsync, verbatim)
//   Shared/Core/WorldSession.cs    (verbatim)
// Game types are stubbed in Stubs.cs. The method bodies below are unchanged.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace SensEssentials;

public class AutoCommandOriginal
{
    private DateTime _nextRun = DateTime.MinValue;
    private Trigger _trigger = Trigger.Disabled;
    private int _currentStep;
    private string _name = "";
    private TimeSpan _interval = TimeSpan.Zero;

    // Guards the schedule and run state, which is used by the timer tick, commands and background runs.
    private readonly object _sync = new();
    private readonly List<CancellationTokenSource> _runs = [];
    private const int MaxRuns = 8;

    public List<AutoCommandStep> Steps { get; } = [];

    public AutoCommandOriginal(string name) => _name = name;

    private DateTime NextScheduledRun()
    {
        var now = DateTime.Now;
        var next = now.Date + _interval;
        return (next <= now ? next + TimeSpan.FromDays(1) : next).ToUniversalTime();
    }

    /// <summary>
    /// Runs all steps in the background. Each run has its own cancellation token, so several runs of the same
    /// command can exist (for example a manual !admin runauto) without interfering with each other.
    /// </summary>
    internal async void RunNow()
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            // Commands can start each other, and a loop of them must not grow without end.
            if (_runs.Count >= MaxRuns)
            {
                Log.Warn($"Auto command '{_name}' already has {MaxRuns} runs going, so another was not started.");
                cts.Dispose();
                return;
            }

            _runs.Add(cts);
        }

        try
        {
            var steps = Steps.Select(step => (step.Command, step.DelaySpan)).ToList();
            await CommandSequence.Run(steps, $"Auto command '{_name}'", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Error(e, $"Auto command '{_name}' failed");
        }
        finally
        {
            lock (_sync)
                _runs.Remove(cts);
            cts.Dispose();
        }
    }

    internal void Cancel()
    {
        Log.Info($"Cancelling autocommand {_name}");
        lock (_sync)
        {
            foreach (var run in _runs)
                run.Cancel();

            _currentStep = 0;
            _nextRun = _trigger == Trigger.Scheduled
                ? NextScheduledRun()
                : DateTime.UtcNow + _interval;
        }
    }

    internal bool IsRunning()
    {
        lock (_sync)
            return _currentStep > 0 || _runs.Count > 0;
    }
}

/// <summary>
/// Runs commands one after another on the game thread, for auto commands and presets.
/// </summary>
internal static class CommandSequence
{
    internal static async Task Run(IReadOnlyList<(string Command, TimeSpan Delay)> steps, string source, CancellationToken token)
    {
        var world = WorldSession.Current;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, WorldSession.Ending);
        try
        {
            foreach (var (command, delay) in steps)
            {
                await GameThread.InvokeAsync(() =>
                {
                    if (!stop.IsCancellationRequested && WorldSession.Current == world && EssentialsCore.Host.IsServerRunning
                        && !string.IsNullOrEmpty(command))
                        EssentialsCore.RunCommand(command, source);
                }, stop.Token).ConfigureAwait(false);

                await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The world changed: nothing more of this run belongs in the next one.
        }
    }
}

public static class GameThread
{
    public static void InvokeSafe(Action action, [CallerMemberName] string caller = "")
    {
        MySandboxGame.Static.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error in game thread action from {caller}");
            }
        }, caller);
    }

    public static Task InvokeAsync(Action action, CancellationToken token = default, [CallerMemberName] string caller = "")
    {
        // Continuations run off the game thread, so a caller awaiting this never delays the game.
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = token.Register(() => done.TrySetCanceled());
        InvokeSafe(() =>
        {
            try
            {
                action();
            }
            finally
            {
                cancel.Dispose();
                done.TrySetResult(true);
            }
        }, caller);
        return done.Task;
    }
}

public static class WorldSession
{
    private static int _current;
    private static CancellationTokenSource _ending = new();

    public static int Current => Volatile.Read(ref _current);

    public static CancellationToken Ending => Volatile.Read(ref _ending).Token;

    internal static void Next()
    {
        Interlocked.Increment(ref _current);
        Interlocked.Exchange(ref _ending, new CancellationTokenSource()).Cancel();
    }
}
