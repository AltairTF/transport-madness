namespace TransportMadness.Sim;

/// <summary>Where money came from or went to; the company keeps a running total for each.</summary>
public enum LedgerEntry { Construction, Stations, Depots, Vehicles, Income, Upkeep, Interest, Demolition }

/// <summary>The player's transport business: money, loan and how it has been spent.</summary>
public sealed class Company
{
    readonly long[] _ledger = new long[Enum.GetValues<LedgerEntry>().Length];

    internal Company(long money) => Money = money;

    public long Money { get; internal set; }
    public long Loan { get; internal set; }
    /// <summary>Money minus loan.</summary>
    public long NetWorth => Money - Loan;
    /// <summary>Consecutive days the company has been worth less than minus the debt limit.</summary>
    public int InsolventDays { get; internal set; }
    /// <summary>Insolvent for too long. Challenge mode would end the game here; the sandbox carries on.</summary>
    public bool Bankrupt { get; internal set; }

    /// <summary>Net money moved under this heading since the start: income positive, spending negative.</summary>
    public long Total(LedgerEntry entry) => _ledger[(int)entry];

    internal void Record(LedgerEntry entry, long amount) => _ledger[(int)entry] += amount;
    internal ReadOnlySpan<long> Ledger => _ledger;
}
