namespace Vibec;

/// While a model call runs, keep one line updated with what's happening and for how long.
/// Says nothing when output is redirected; clears itself when the call returns.
public static class Indicator
{
    static readonly Dictionary<string, string[]> Phrases = new()
    {
        ["backend"] =
        [
            "coding", "coding (not vibe coding)", "writing it out, step by step", "following your rules exactly",
            "translating, not interpreting", "adding nothing", "still coding. rules take time", "checking it does what you said",
        ],
        ["lowering"] =
        [
            "specifying", "writing down what you meant", "being precise", "leaving nothing to interpretation",
            "turning rules into requirements", "still specifying. precision takes time",
        ],
        ["resolution"] =
        [
            "resolving", "working out what you meant", "taking you literally, loosely", "consulting the vibes, strictly",
            "deciding what \"a lot\" means",
        ],
        ["toolchain"] =
        [
            "asking how to build this", "reading the manual so you don't have to", "finding your compiler",
        ],
    };

    static readonly string[] Generic = ["working", "in order, as written", "still working"];

    public static async Task<T> Run<T>(string stage, Func<Task<T>> work)
    {
        if (Console.IsOutputRedirected) return await work();

        var phrases = Phrases.GetValueOrDefault(stage) ?? Generic;
        var started = DateTime.UtcNow;
        using var stop = new CancellationTokenSource();
        int width = 0;

        var ticker = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var elapsed = DateTime.UtcNow - started;
                    var phrase = phrases[(int)(elapsed.TotalSeconds / 8) % phrases.Length];
                    var line = $"  {stage,-12} {phrase}… {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
                    lock (Console.Out) { Console.Write("\r" + line.PadRight(width)); }
                    width = Math.Max(width, line.Length);
                    await Task.Delay(1000, stop.Token);
                }
            }
            catch (TaskCanceledException) { }
        });

        try { return await work(); }
        finally
        {
            stop.Cancel();
            await ticker;
            lock (Console.Out) { Console.Write("\r" + new string(' ', width) + "\r"); }
        }
    }
}
