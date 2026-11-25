// Worked example: SensEssentials AutoCommand.Cancel at 0b51e8a.
//
// Cancel() loops `foreach (var run in _runs) run.Cancel();` while holding `lock (_sync)`. A run is awaiting
// `Task.Delay(delay, token)` inside CommandSequence.Run. Cancelling its token completes the delay; if the await
// continuation runs inline on the cancelling thread, the run unwinds to RunNow's finally, which takes the same lock
// (re-entrant on that thread) and removes itself from _runs, and the foreach then throws
// "Collection was modified; enumeration operation may not execute".
//
// Each check starts a fresh command, waits until its first step ran on the game thread and the run is parked in
// Task.Delay, then calls Cancel(), from a plain thread (a command handler) and from the game thread (a timer tick).
using System;
using System.Threading;
using SensEssentials;

namespace Harness;

public static class Program
{
    private const int Runs = 25;

    public static int Main()
    {
        Matrix.Note("runtime " + Matrix.Runtime);
        using var game = new GameThread();
        Sandbox.MySandboxGame.Thread = game;

        Matrix.Count("original Cancel from a command thread", Runs, _ =>
        {
            var auto = new AutoCommandOriginal("restart-warning");
            auto.Steps.Add(new AutoCommandStep { Command = "!say restart soon", DelaySpan = TimeSpan.FromSeconds(30) });
            StartAndPark(game, auto.RunNow);
            auto.Cancel();
            Wait.Until(() => !auto.IsRunning(), 5000, "the run to end");
        });

        Matrix.Count("original Cancel from the game thread", Runs, _ =>
        {
            var auto = new AutoCommandOriginal("restart-warning");
            auto.Steps.Add(new AutoCommandStep { Command = "!say restart soon", DelaySpan = TimeSpan.FromSeconds(30) });
            StartAndPark(game, auto.RunNow);
            game.Run(auto.Cancel);
            Wait.Until(() => !auto.IsRunning(), 5000, "the run to end");
        });

        Matrix.Count("fixed Cancel from a command thread", Runs, _ =>
        {
            var auto = new AutoCommandFixed("restart-warning");
            auto.Steps.Add(new AutoCommandStep { Command = "!say restart soon", DelaySpan = TimeSpan.FromSeconds(30) });
            StartAndPark(game, auto.RunNow);
            auto.Cancel();
            Wait.Until(() => !auto.IsRunning(), 5000, "the run to end");
        });

        Matrix.Count("fixed Cancel from the game thread", Runs, _ =>
        {
            var auto = new AutoCommandFixed("restart-warning");
            auto.Steps.Add(new AutoCommandStep { Command = "!say restart soon", DelaySpan = TimeSpan.FromSeconds(30) });
            StartAndPark(game, auto.RunNow);
            game.Run(auto.Cancel);
            Wait.Until(() => !auto.IsRunning(), 5000, "the run to end");
        });

        return Matrix.Finish();
    }

    /// <summary>Starts a run and waits until its first command ran on the game thread and it is parked in Task.Delay.</summary>
    private static void StartAndPark(GameThread game, Action runNow)
    {
        int before = EssentialsCore.Ran.Count;
        runNow();
        Wait.Until(() => EssentialsCore.Ran.Count > before, 5000, "the first step to run");
        game.Drain();
        // The continuation after InvokeAsync runs on the thread pool (RunContinuationsAsynchronously) and then awaits
        // Task.Delay(30 s); give it a moment to get there.
        Thread.Sleep(50);
    }
}
