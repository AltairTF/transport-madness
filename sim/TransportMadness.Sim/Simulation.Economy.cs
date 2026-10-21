namespace TransportMadness.Sim;

/// <summary>The company's money: what commands cost, the daily accounts, and the loan (the ministry's advance in the campaign).</summary>
public sealed partial class Simulation
{
    readonly EconomyTuning _economy;
    readonly long _ticksPerDay;

    public Company Company { get; }

    /// <summary>Whole days of simulated time that have passed.</summary>
    public long Day => Tick / _ticksPerDay;
    public float DayLengthSeconds => _economy.DayLength;

    /// <summary>Position in the calendar, derived from <see cref="Day"/>: the year counts from the tuning's start year, the month and the day of the month from 1.</summary>
    public readonly record struct Date(int Year, int Month, int DayOfMonth);

    public Date Today => DateOfDay(Day);

    public Date DateOfDay(long day)
    {
        long month = day / _economy.DaysPerMonth;
        return new Date(_economy.StartYear + (int)(month / _economy.MonthsPerYear), (int)(month % _economy.MonthsPerYear) + 1, (int)(day % _economy.DaysPerMonth) + 1);
    }

    /// <summary>Whole months that have passed; changes on the first day of each month.</summary>
    public long MonthIndex => Day / _economy.DaysPerMonth;

    /// <summary>How far through the current day it is, 0 at midnight up to (not including) 1.</summary>
    public float TimeOfDay => (float)(Tick % _ticksPerDay) / _ticksPerDay;

    /// <summary>Whether the company has at least this much money (the cost of a command is rounded up to whole units).</summary>
    public bool CanAfford(float cost) => Company.Money >= Price(cost);

    static long Price(float cost) => (long)MathF.Ceiling(cost);

    /// <summary>Pays for something when the money is there. Nothing happens, and false is returned, when it is not.</summary>
    bool TrySpend(float cost, LedgerEntry entry)
    {
        long price = Price(cost);
        if (Company.Money < price) return false;
        Charge(price, entry);
        return true;
    }

    /// <summary>Takes money that is owed whether or not it is there (upkeep, interest): the balance can go negative.</summary>
    void Charge(long amount, LedgerEntry entry)
    {
        if (entry == LedgerEntry.Fines) _monthFines += amount;
        Company.Money -= amount;
        Company.Record(entry, -amount);
    }

    void Earn(long amount, LedgerEntry entry)
    {
        Company.Money += amount;
        Company.Record(entry, amount);
    }

    /// <summary>Borrows up to the loan limit. Returns the amount taken, 0 when refused.</summary>
    int TakeLoan(long amount)
    {
        if (amount <= 0 || Company.Loan + amount > _economy.MaxLoan) return 0;
        Company.Loan += amount;
        Company.Money += amount;
        return 1;
    }

    int RepayLoan(long amount)
    {
        if (amount <= 0 || amount > Company.Loan || amount > Company.Money) return 0;
        Company.Loan -= amount;
        Company.Money -= amount;
        return 1;
    }

    /// <summary>The accounts at the turn of a day: upkeep of stations and vehicles, interest on the loan (the ministry's advance); at the turn of a month the campaign's books.</summary>
    void CloseDay()
    {
        foreach (var station in _stations)
            if (station.Active)
                Charge(Price(station.Mode == StationMode.Rail ? _railTuning.StationUpkeepPerDay : _stationTuning.UpkeepPerDay + (station.HasBays ? _stationTuning.BayUpkeepPerDay : 0f)), LedgerEntry.Upkeep);
        ChargeVehicleUpkeep();
        ChargeTrainUpkeep();
        ChargeGarageUpkeep();
        foreach (var light in _lights)
            if (light.Active) Charge(Price(_net.Tuning.Roads.TrafficLight.UpkeepPerDay), LedgerEntry.Upkeep);
        if (Company.Loan > 0) Charge(Price(Company.Loan * _economy.InterestPerDay), LedgerEntry.Interest);
        CheckSignals();
        CheckGarage();
        CloseDayForStaffing();
        CloseDayForTowns();
        TakeCalledUp();
        CloseDayForTrips();
        if ((Day + 1) % _economy.DaysPerMonth == 0) CloseMonthForCampaign();
    }
}
