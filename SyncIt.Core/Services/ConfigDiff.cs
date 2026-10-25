using System.Text.Json;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>What a config reload must do: restart everything, or only start/stop the pairs that changed.</summary>
public sealed class ReloadPlan
{
    public bool RestartAll { get; init; }
    public HashSet<string> Stop { get; } = new();
    public HashSet<string> Start { get; } = new();
    /// <summary>Pairs whose effective pause state flipped (no restart needed).</summary>
    public Dictionary<string, bool> PauseChanges { get; } = new();
    public bool IsNoop => !RestartAll && Stop.Count == 0 && Start.Count == 0 && PauseChanges.Count == 0;
}

public static class ConfigDiff
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ReloadPlan Plan(SyncConfig oldC, SyncConfig newC)
    {
        if (Globals(oldC) != Globals(newC)) return new ReloadPlan { RestartAll = true };

        // The Explorer overlay is tied to the first enabled pair: if that changes, restart everything.
        var oldFirst = oldC.SyncPairs.FirstOrDefault(p => p.Enabled);
        var newFirst = newC.SyncPairs.FirstOrDefault(p => p.Enabled);
        if (oldFirst?.Id != newFirst?.Id || (oldFirst is not null && newFirst is not null && oldFirst.LocalPath != newFirst.LocalPath))
            return new ReloadPlan { RestartAll = true };

        var plan = new ReloadPlan();
        var oldPairs = oldC.SyncPairs.Where(p => p.Enabled).ToDictionary(p => p.Id);
        var newPairs = newC.SyncPairs.Where(p => p.Enabled).ToDictionary(p => p.Id);

        foreach (var id in oldPairs.Keys.Except(newPairs.Keys)) plan.Stop.Add(id);
        foreach (var id in newPairs.Keys.Except(oldPairs.Keys)) plan.Start.Add(id);
        foreach (var (id, np) in newPairs)
        {
            if (!oldPairs.TryGetValue(id, out var op)) continue;
            if (PairKey(op, oldC) != PairKey(np, newC)) { plan.Stop.Add(id); plan.Start.Add(id); continue; }
            var wasPaused = op.Paused || AccountRegistry.ForPair(oldC, op)?.Paused == true;
            var isPaused = np.Paused || AccountRegistry.ForPair(newC, np)?.Paused == true;
            if (wasPaused != isPaused) plan.PauseChanges[id] = isPaused;
        }
        return plan;
    }

    /// <summary>Copies settings engines read live (no restart needed) into the running config object.</summary>
    public static void ApplyHotSettings(SyncConfig running, SyncConfig newC)
    {
        running.ActiveHoursEnabled = newC.ActiveHoursEnabled;
        running.ActiveHoursStart = newC.ActiveHoursStart;
        running.ActiveHoursEnd = newC.ActiveHoursEnd;
        running.NotifyConflicts = newC.NotifyConflicts;
        running.NotifyDeletions = newC.NotifyDeletions;
        running.CheckForUpdates = newC.CheckForUpdates;
    }

    private static string Globals(SyncConfig c)
    {
        var copy = JsonSerializer.Deserialize<SyncConfig>(JsonSerializer.Serialize(c, Json), Json)!;
        copy.SyncPairs = new();
        copy.Accounts = new();
        ApplyHotSettings(copy, new SyncConfig()); // ignore hot settings
        return JsonSerializer.Serialize(copy, Json);
    }

    private static string PairKey(SyncPair p, SyncConfig c)
    {
        var copy = JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(p, Json), Json)!;
        copy.Paused = false;
        var acc = AccountRegistry.ForPair(c, p);
        return JsonSerializer.Serialize(copy, Json) + "|" + acc?.Id + "|" + acc?.TokenStorePath;
    }
}
