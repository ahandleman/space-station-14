using System.Diagnostics;
using Content.StationGeneration;

internal static class RegressionChecks
{
    public static int Run(int count, string? reportPath, int parallel = 1)
    {
        if (count is < 1 or > 10000) throw new ArgumentException("Batch count must be 1–10000.");
        if (parallel is < 1 or > 16) throw new ArgumentException("Parallel workers must be between 1 and 16.");
        var watch = Stopwatch.StartNew();
        var failures = new List<object>();
        var hashes = new HashSet<string>();
        var records = new List<object>();
        var styles = new[] { "hub", "ring", "spine" };
        var results = new GenerationResult[count];
        var checkedCount = 0;
        var failedCount = 0;
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
        {
            var config = new StationGenerationConfig { Seed = i, Archetype = styles[i % 3] };
            var project = StationSpatialPlanner.Generate(new StationProject { Config = config });
            var result = project.Result!;
            results[i] = result;
            if (!result.Success) Interlocked.Increment(ref failedCount);
            var completed = Interlocked.Increment(ref checkedCount);
            if (completed % 10 == 0) Console.WriteLine($"Checked {completed}/{count}; failures={Volatile.Read(ref failedCount)}");
        });
        // Keep reporting in seed order, regardless of worker completion order.
        for (var i = 0; i < count; i++)
        {
            var result = results[i];
            var archetype = styles[i % 3];
            if (!result.Success)
            {
                failures.Add(new { seed = i, archetype, result.Diagnostics });
                Console.WriteLine($"FAIL seed={i} style={archetype}: {string.Join("; ", result.Diagnostics.Select(d => d.Stage + "/" + d.Code + ": " + d.Message))}");
            }
            else
            {
                hashes.Add(result.Metrics.Hash);
                records.Add(new { seed = i, archetype, result.Attempts, result.Metrics });
            }
        }
        var summary = new { attempted = count, successful = count - failures.Count, failed = failures.Count,
            uniqueHashes = hashes.Count, parallelWorkers = parallel, elapsedSeconds = watch.Elapsed.TotalSeconds, failures, seeds = records };
        if (reportPath != null) File.WriteAllText(reportPath, ProjectJson.Serialize(summary));
        Console.WriteLine($"Attempted {count}; accepted {count - failures.Count}; failed {failures.Count}; unique layouts {hashes.Count}; {watch.Elapsed.TotalSeconds:F2}s");
        return failures.Count == 0 && (count == 1 || hashes.Count > 1) ? 0 : 1;
    }
}
