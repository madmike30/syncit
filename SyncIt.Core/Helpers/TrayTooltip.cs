using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>Tray tooltip text (Windows caps it at 63 chars). Lists the accounts with their badge letters.</summary>
public static class TrayTooltip
{
    public static string Build(SyncStatus status, string stateText)
    {
        var accounts = status.PairStatuses.Select(p => p.AccountEmail)
            .Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (accounts.Count == 0 && status.AccountEmail is not null) accounts.Add(status.AccountEmail);
        var who = accounts.Count switch
        {
            0 => "",
            1 => accounts[0]!,
            _ => string.Join(" ", accounts.Select(a => $"[{AccountColors.Initial(a)}]")) + $" {accounts.Count} accounts",
        };
        var tip = who.Length == 0 ? $"SyncIt — {stateText}" : $"SyncIt — {who}\n{stateText}";
        return tip.Length <= 63 ? tip : tip[..63];
    }
}
