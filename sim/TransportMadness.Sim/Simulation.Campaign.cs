namespace TransportMadness.Sim;

/// <summary>What the turn of a month did to the budget and the standing, for the Company window.</summary>
/// <param name="Month">Index of the month that was closed (0 is the first January).</param>
/// <param name="ExportValue">Value delivered to the gateways in it.</param>
/// <param name="ExportTerm">Points the delivery moved the standing by against what the phase expects.</param>
/// <param name="DeficitTerm">Points lost for ending the month with the money below zero (negative or zero).</param>
/// <param name="FineTerm">Points lost for the fines of the month (negative or zero).</param>
/// <param name="Allocation">Money granted for the next month, 0 once the campaign is over.</param>
public readonly record struct MonthReport(long Month, double ExportValue, float ExportTerm, float DeficitTerm, float FineTerm, float StandingBefore, float StandingAfter, long Allocation);

/// <summary>The campaign (C4): the monthly allocation, the government's standing, dismissal and the final grade.</summary>
public sealed partial class Simulation
{
    readonly CampaignTuning _campaign;
    readonly List<float> _standingHistory = new();
    double _monthExportValue;
    long _monthFines;

    public CampaignTuning CampaignTuning => _campaign;
    /// <summary>The standing at the end of every month so far, oldest first.</summary>
    public IReadOnlyList<float> StandingHistory => _standingHistory;
    /// <summary>What the last turn of a month did, null before the first.</summary>
    public MonthReport? LastMonth { get; private set; }

    /// <summary>The phase of the war now.</summary>
    public PhaseTuning Phase => _campaign.PhaseOf(MonthIndex);

    /// <summary>The allocation the next turn of a month would grant at the standing now (the phase of the month it will start).</summary>
    public long NextAllocation => (long)MathF.Round(_campaign.PhaseOf(MonthIndex + 1).Allocation * _campaign.AllocationFactor(Company.Standing));

    /// <summary>Value delivered to the gateways so far this month.</summary>
    public double ExportValueThisMonth => _monthExportValue;

    /// <summary>Months left before the dismissal if the standing stays under the line, 0 while it is above it.</summary>
    public int MonthsToDismissal => Company.LowStandingMonths == 0 ? 0 : Math.Max(0, _campaign.DismissMonths - Company.LowStandingMonths);

    void NoteExport(int cargo, int units)
    {
        double value = units * (double)_cargo[cargo].PayPerUnitKm;
        _monthExportValue += value;
        Company.DeliveredValue += value;
    }

    /// <summary>The average satisfaction of the towns, 0 to 100, 0 on a map without towns.</summary>
    public float AverageSatisfaction => _towns.Count == 0 ? 0f : _towns.Average(t => t.Satisfaction);

    /// <summary>
    /// The turn of a month: the standing moves by the deliveries, the deficit and the fines; the committee may be dismissed; the last
    /// month brings the grade; otherwise the allocation for the next month is paid.
    /// </summary>
    void CloseMonthForCampaign()
    {
        if (Company.CampaignOver) return;
        ChargePower();
        long month = MonthIndex; // the month that is ending (Day has not moved on yet, see CloseDay)
        var phase = _campaign.PhaseOf(month);
        float before = Company.Standing;
        float exportTerm = Math.Clamp((float)(_monthExportValue - phase.ExpectedValue) / _campaign.ExportScale, -_campaign.MaxExportSwing, _campaign.MaxExportSwing);
        float deficitTerm = Company.Money < 0 ? -_campaign.DeficitPenalty : 0f;
        float fineTerm = -Math.Min(_campaign.MaxFinePenalty, _monthFines / _campaign.FinePenaltyUnit);
        Company.Standing = Math.Clamp(before + exportTerm + deficitTerm + fineTerm, 0f, 100f);
        _standingHistory.Add(Company.Standing);

        Company.LowStandingMonths = Company.Standing < _campaign.DismissBelow ? Company.LowStandingMonths + 1 : 0;
        long allocation = 0;
        if (Company.LowStandingMonths >= _campaign.DismissMonths)
        {
            Company.Dismissed = true;
            AddEvent(SimEventKind.Standing, EventSeverity.Serious, 0f, 0f, -1, $"The committee has been dismissed: standing {Company.Standing:0} for {_campaign.DismissMonths} months");
        }
        else if (month + 1 >= _campaign.Months)
        {
            Finish();
        }
        else
        {
            allocation = (long)MathF.Round(_campaign.PhaseOf(month + 1).Allocation * _campaign.AllocationFactor(Company.Standing));
            Earn(allocation, LedgerEntry.Allocation);
            WarnAboutStanding();
        }
        LastMonth = new MonthReport(month, _monthExportValue, exportTerm, deficitTerm, fineTerm, before, Company.Standing, allocation);
        _monthExportValue = 0;
        _monthFines = 0;
    }

    void WarnAboutStanding()
    {
        if (Company.LowStandingMonths > 0)
            AddEvent(SimEventKind.Standing, EventSeverity.Serious, 0f, 0f, -1, $"Standing {Company.Standing:0}: {MonthsToDismissal} more months under {_campaign.DismissBelow:0} and the committee is dismissed");
        else if (Company.Standing < _campaign.DismissBelow + 10f)
            AddEvent(SimEventKind.Standing, EventSeverity.Warning, 0f, 0f, -1, $"Standing {Company.Standing:0}: the ministry is losing patience");
    }

    /// <summary>The last month is over: the grade comes from the standing, what was delivered and how the towns feel.</summary>
    void Finish()
    {
        var g = _campaign.Grade;
        float production = (float)Math.Min(100.0, 100.0 * Company.DeliveredValue / g.ProductionTarget);
        Company.Score = g.StandingWeight * Company.Standing + g.ProductionWeight * production + g.SatisfactionWeight * AverageSatisfaction;
        int grade = 0;
        while (grade < g.Thresholds.Count && Company.Score < g.Thresholds[grade]) grade++;
        Company.Grade = grade;
        Company.Finished = true;
        AddEvent(SimEventKind.Standing, EventSeverity.Info, 0f, 0f, -1, $"The campaign is over: {g.Names[grade]} (score {Company.Score:0})");
    }

    ulong CampaignHash()
    {
        ulong h = BitConverter.SingleToUInt32Bits(Company.Standing);
        h = h * 31 + (ulong)Company.LowStandingMonths;
        h = h * 31 + (Company.Dismissed ? 1UL : 0UL) + (Company.Finished ? 2UL : 0UL) + (ulong)(Company.Grade + 1) * 4;
        h = h * 31 + BitConverter.DoubleToUInt64Bits(_monthExportValue);
        h = h * 31 + BitConverter.DoubleToUInt64Bits(Company.DeliveredValue);
        h = h * 31 + (ulong)_monthFines;
        return h;
    }
}
