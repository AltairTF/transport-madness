namespace TransportMadness.Sim;

/// <summary>
/// Staffing (C11): the big industries have jobs, and the people who fill them are the residents of the towns within reach, as trip groups: an industry is
/// a workplace like a building with jobs (see <see cref="PickPlace"/>), and its people come on foot, by car or by a line that stops where the industry
/// is covered. The staffing of an industry is the share of its jobs that groups filled by reaching it in a day, worked out when the day closes; it makes
/// its output times that staffing until the next day closes. There are no shifts.
/// </summary>
public sealed partial class Simulation
{
    /// <summary>
    /// How fully an industry is staffed, 0 to 1: the share of its jobs filled by the groups that reached it on the last day that closed (1 until the first
    /// closes). 1 for an industry without jobs and on a map without towns, where there is nobody to work.
    /// </summary>
    public float StaffingOf(Industry industry)
    {
        if (_industryTuning.Types[industry.Type].Jobs <= 0 || _towns.Count == 0) return 1f;
        return industry.Staffing;
    }

    /// <summary>The people who reached an industry to work on the last day that closed.</summary>
    public int StaffOf(Industry industry) => industry.WorkersYesterday;

    /// <summary>The jobs of an industry.</summary>
    public int JobsOf(Industry industry) => _industryTuning.Types[industry.Type].Jobs;

    /// <summary>At the turn of a day: the people who reached each industry with jobs today become its staffing.</summary>
    void CloseDayForStaffing()
    {
        foreach (var industry in _industries)
        {
            industry.WorkersYesterday = industry.WorkersToday;
            industry.WorkersToday = 0;
            int jobs = _industryTuning.Types[industry.Type].Jobs;
            if (jobs > 0) industry.Staffing = MathF.Min(1f, industry.WorkersYesterday / (float)jobs);
        }
    }

    /// <summary>
    /// A new industry with jobs (founded by the state after the groups were made) takes the nearest groups within reach that are at home, until its jobs are
    /// taken; they leave the building they worked in. Groups made from now on choose among workplaces with it already in the list.
    /// </summary>
    void StaffNewIndustry(Industry industry)
    {
        int jobs = _industryTuning.Types[industry.Type].Jobs;
        if (jobs <= 0 || _groups.Count == 0) return;
        float reach2 = _trips.WorkRadius * _trips.WorkRadius;
        int place = IndustryPlace + industry.Id;
        var near = new List<(float D, int Id)>();
        foreach (var g in _groups)
        {
            if (g.Retired || g.Travelling || g.Work >= IndustryPlace) continue; // a group that works in an industry already stays there
            var home = _buildings[g.Home];
            float d2 = Dist2(home.X, home.Z, industry.X, industry.Z);
            if (d2 <= reach2) near.Add((d2, g.Id));
        }
        near.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Id.CompareTo(b.Id));
        foreach (var (_, id) in near)
        {
            if (_jobsTaken.GetValueOrDefault(place) >= jobs) break;
            var g = _groups[id];
            if (g.NextLeg != TripLeg.WorkOut && g.NextLeg != TripLeg.LeisureOut) continue; // between the trips of a day, not at the workplace
            if (g.Work >= 0) _jobsTaken[g.Work] = Math.Max(0, _jobsTaken.GetValueOrDefault(g.Work) - g.People);
            g.Work = place;
            _jobsTaken[place] = _jobsTaken.GetValueOrDefault(place) + g.People;
            if (g.NextTick == long.MaxValue) { g.PlanDay = Day + 1; Advance(g, -1, Tick); } // it had no workplace and so nowhere to go
        }
    }
}
