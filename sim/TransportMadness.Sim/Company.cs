namespace TransportMadness.Sim;

/// <summary>Where money came from or went to; the company keeps a running total for each.</summary>
public enum LedgerEntry { Construction, Stations, Depots, Vehicles, Income, Upkeep, Interest, Demolition, Maintenance, Compensation, Fines, Imports, Allocation, Orders }

/// <summary>The player's transport business: money, loan and how it has been spent.</summary>
public sealed class Company
{
    readonly long[] _ledger = new long[Enum.GetValues<LedgerEntry>().Length];

    internal Company(long money) => Money = money;

    public long Money { get; internal set; }
    public long Loan { get; internal set; }
    /// <summary>Money minus loan.</summary>
    public long NetWorth => Money - Loan;
    /// <summary>How the government sees the committee, 0 to 100; see <see cref="CampaignTuning"/>.</summary>
    public float Standing { get; internal set; }
    /// <summary>Months in a row the standing has been under the dismissal line.</summary>
    public int LowStandingMonths { get; internal set; }
    /// <summary>The committee has been dismissed: the campaign is over with no grade.</summary>
    public bool Dismissed { get; internal set; }
    /// <summary>The campaign ran to its last month; <see cref="Grade"/> is set.</summary>
    public bool Finished { get; internal set; }
    /// <summary>The campaign is over, one way or the other.</summary>
    public bool CampaignOver => Dismissed || Finished;
    /// <summary>The final grade, 0 for the best up to the number of names less one; -1 until the campaign has finished.</summary>
    public int Grade { get; internal set; } = -1;
    /// <summary>The score the grade was given from, 0 to 100.</summary>
    public float Score { get; internal set; }
    /// <summary>The value of everything delivered to the gateways so far (units times the cargo's pay per unit and km).</summary>
    public double DeliveredValue { get; internal set; }
    /// <summary>Orders of the government met in full, paid late in proportion, and failed so far.</summary>
    public int OrdersMet { get; internal set; }
    public int OrdersLate { get; internal set; }
    public int OrdersFailed { get; internal set; }
    /// <summary>People hurt in crashes of the company's vehicles so far (each was paid compensation).</summary>
    public long Injured { get; internal set; }

    /// <summary>Net money moved under this heading since the start: income positive, spending negative.</summary>
    public long Total(LedgerEntry entry) => _ledger[(int)entry];

    internal void Record(LedgerEntry entry, long amount) => _ledger[(int)entry] += amount;
    internal ReadOnlySpan<long> Ledger => _ledger;
}
