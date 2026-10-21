namespace TransportMadness.Sim;

/// <summary>The fleet by year (C8): a model can be bought from the year of its tuning on; the roster is data and the code names none of it.</summary>
public sealed partial class Simulation
{
    /// <summary>Whether a road vehicle model can be bought now (its year has come).</summary>
    public bool IsAvailable(VehicleModelTuning model) => Today.Year >= model.FromYear;

    /// <summary>Whether a locomotive can be bought now.</summary>
    public bool IsAvailable(LocomotiveTuning locomotive) => Today.Year >= locomotive.FromYear;

    /// <summary>The road vehicle models that can be bought now, in the order of the tuning.</summary>
    public IEnumerable<VehicleModelTuning> AvailableModels => _models.Where(IsAvailable);

    /// <summary>The locomotives that can be bought now.</summary>
    public IEnumerable<LocomotiveTuning> AvailableLocomotives => _railTuning.Locomotives.Where(IsAvailable);

    /// <summary>The names of the models (road vehicles and locomotives) whose year is <paramref name="year"/>, for the news.</summary>
    public IReadOnlyList<string> ModelsOfYear(int year) =>
        _models.Where(m => m.FromYear == year).Select(m => m.Name).Concat(_railTuning.Locomotives.Where(l => l.FromYear == year).Select(l => l.Name)).ToList();

    /// <summary>The factor on the price of a repair of a road vehicle model now: 1 until it is old, more after.</summary>
    public float RepairFactorOf(VehicleModelTuning model) => _maint.OldVehicle.Factor(model.FromYear, Today.Year);

    /// <summary>The factor on the price of a repair of a locomotive's train now.</summary>
    public float RepairFactorOf(LocomotiveTuning locomotive) => _maint.OldVehicle.Factor(locomotive.FromYear, Today.Year);
}
