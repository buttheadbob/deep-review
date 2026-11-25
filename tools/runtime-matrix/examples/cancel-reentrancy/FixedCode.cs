// The fix under test: Cancel takes a snapshot of the runs under the lock and cancels them after leaving it, so a run
// whose continuation runs inline (and removes itself from _runs under the same lock) cannot change the list being
// enumerated. Everything else is AutoCommandOriginal unchanged.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SensEssentials;

public class AutoCommandFixed
{
    private DateTime _nextRun = DateTime.MinValue;
    private Trigger _trigger = Trigger.Disabled;
    private int _currentStep;
    private string _name = "";
    private TimeSpan _interval = TimeSpan.Zero;
    private readonly object _sync = new();
    private readonly List<CancellationTokenSource> _runs = [];
    private const int MaxRuns = 8;

    public List<AutoCommandStep> Steps { get; } = [];

    public AutoCommandFixed(string name) => _name = name;

    private DateTime NextScheduledRun()
    {
        var now = DateTime.Now;
        var next = now.Date + _interval;
        return (next <= now ? next + TimeSpan.FromDays(1) : next).ToUniversalTime();
    }

    internal async void RunNow()
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
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
        List<CancellationTokenSource> runs;
        lock (_sync)
        {
            runs = _runs.ToList();
            _currentStep = 0;
            _nextRun = _trigger == Trigger.Scheduled
                ? NextScheduledRun()
                : DateTime.UtcNow + _interval;
        }

        // Outside the lock: a cancelled run may finish inline on this thread and take the lock to remove itself.
        foreach (var run in runs)
            run.Cancel();
    }

    internal bool IsRunning()
    {
        lock (_sync)
            return _currentStep > 0 || _runs.Count > 0;
    }
}
