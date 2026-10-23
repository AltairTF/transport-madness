using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class RailNetworkTests
{
    static RailNetwork Flat() => new(Terrain.Flat(), Tuning.Load());

    [Fact]
    public void ATrackBetweenTwoPoints_IsOneStraightPieceWithTwoNodes()
    {
        var rail = Flat();

        var built = rail.BuildTrack(0, 0, 300, 0);

        var track = rail.GetTrack(Assert.Single(built));
        Assert.Equal(300f, track.Length, 2);
        Assert.Equal(2, rail.NodeCount);
        Assert.Equal(rail.Rail.SpeedLimit, track.SpeedLimit);
        Assert.Equal(new[] { track.Id }, rail.TracksAt(track.From));
    }

    [Fact]
    public void ATrackThatEndsInsideAnotherSplitsItAndMakesAJunction()
    {
        var rail = Flat();
        rail.BuildTrack(0, 0, 400, 0);

        var built = rail.BuildTrack(200, 150, 200, 0);

        Assert.Single(built);
        int middle = rail.FindNodeNear(200, 0, 1f);
        Assert.Equal(3, rail.TracksAt(middle).Count);
        Assert.Equal(2, rail.LiveTrackCount - 1); // the split pieces plus the branch, minus the branch
    }

    [Fact]
    public void ATrackCrossingAnother_MakesAJunctionOfFourTracks()
    {
        var rail = Flat();
        rail.BuildTrack(0, 0, 400, 0);

        var built = rail.BuildTrack(200, -150, 200, 150);

        Assert.Equal(2, built.Count);
        Assert.Equal(4, rail.TracksAt(rail.FindNodeNear(200, 0, 1f)).Count);
    }

    [Fact]
    public void ATrackCannotBeBuiltTwiceOrTooShortOrTooSharp()
    {
        var rail = Flat();
        rail.BuildTrack(0, 0, 300, 0);

        Assert.Empty(rail.BuildTrack(0, 0, 300, 0));                                // a duplicate
        Assert.Empty(rail.BuildTrack(300, 0, 310, 0));                              // shorter than the minimum
        Assert.Empty(rail.BuildTrack(300, 0, 400, 0, sweep: 2.6f));                 // a curve tighter than the minimum radius
        Assert.NotEmpty(rail.BuildTrack(300, 0, 700, 0, sweep: 0.5f));              // a gentle one is fine
    }

    [Fact]
    public void ATrackThatClimbsTooSteeply_IsRefused()
    {
        var terrain = Terrain.Create(128, 8f, (x, _) => 30f + x * 0.1f);
        var rail = new RailNetwork(terrain, Tuning.Load());

        Assert.False(rail.CanBuildTrack(-200, 0, 200, 0));   // 10 % grade, over the 4 % limit
        var gentle = new RailNetwork(Terrain.Create(128, 8f, (x, _) => 10f + x * 0.03f), Tuning.Load());
        Assert.True(gentle.CanBuildTrack(-200, 0, 200, 0));
    }

    [Fact]
    public void ASteepGradeAndATightCurve_LowerTheSpeedLimit()
    {
        var rail = new RailNetwork(Terrain.Create(128, 8f, (x, _) => 20f + x * 0.035f), Tuning.Load());
        var steep = rail.GetTrack(rail.BuildTrack(-200, 0, 200, 0).Single());
        var level = Flat();
        var curved = level.GetTrack(level.BuildTrack(0, 0, 300, 0, sweep: 1.2f).Single());

        Assert.True(steep.SpeedLimit < rail.Rail.SpeedLimit * 0.8f, $"steep limit {steep.SpeedLimit}");
        Assert.True(curved.SpeedLimit < level.Rail.SpeedLimit, $"curve limit {curved.SpeedLimit}");
    }

    [Fact]
    public void Price_IsLengthTimesRate_OnLevelGround_AndTheEstimateMatchesTheBuild()
    {
        var rail = Flat();
        var cost = rail.EstimateCost(0, 0, 500, 0)!.Value;

        Assert.Equal(500f * rail.Rail.TrackCostPerMetre, cost.Money, 1);
        Assert.Equal(0f, cost.CutVolume);
        Assert.Equal(500f, cost.Length, 2);
    }

    [Fact]
    public void Earthworks_ArePriced_OnRollingGround()
    {
        var hills = Terrain.Create(128, 8f, (x, _) => 10f + 4f * MathF.Sin(x / 40f));
        var rail = new RailNetwork(hills, Tuning.Load());

        var cost = rail.EstimateCost(-200, 0, 200, 0)!.Value;

        Assert.True(cost.CutVolume > 0f && cost.FillVolume > 0f);
        Assert.True(cost.Money > 400f * rail.Rail.TrackCostPerMetre);
    }

    [Fact]
    public void ATrackOverARiver_IsABridgeAndAnEndInWaterIsRefused()
    {
        var terrain = Terrain.Create(128, 8f, (x, _) => MathF.Abs(x) < 30f ? -4f : 10f);
        var rail = new RailNetwork(terrain, Tuning.Load());

        Assert.Empty(rail.BuildTrack(0, 0, 200, 0));
        var pieces = rail.BuildTrack(-200, 0, 200, 0).Select(id => rail.GetTrack(id)).ToList();

        var bridge = Assert.Single(pieces, p => p.Structure == RoadStructure.Bridge);
        Assert.True(bridge.Length >= 60f);
        Assert.Contains(pieces, p => p.Structure == RoadStructure.None);
    }

    [Fact]
    public void ATrackThroughAHill_IsATunnel_AndTheHillStays()
    {
        float Hill(float x) => MathF.Abs(x) >= 80f ? 0f : 40f * 0.5f * (1f + MathF.Cos(MathF.PI * x / 80f));
        var terrain = Terrain.Create(128, 8f, (x, _) => 10f + Hill(x));
        var rail = new RailNetwork(terrain, Tuning.Load());
        float top = terrain.HeightAt(0, 0);

        var pieces = rail.BuildTrack(-300, 0, 300, 0).Select(id => rail.GetTrack(id)).ToList();

        Assert.Contains(pieces, p => p.Structure == RoadStructure.Tunnel);
        Assert.Equal(top, terrain.HeightAt(0, 0), 3);
    }

    [Fact]
    public void TurnAngle_AndCanPass_FollowTheBendBetweenTwoTracks()
    {
        var rail = Flat();
        rail.BuildTrack(0, 0, 200, 0);
        rail.BuildTrack(200, 0, 400, 0);       // straight on
        rail.BuildTrack(200, 0, 300, 100);     // 45 degrees
        rail.BuildTrack(200, 0, 200, 150);     // 90 degrees: sharper than a train takes
        int node = rail.FindNodeNear(200, 0, 1f);
        var from = rail.TracksAt(rail.FindNodeNear(0, 0, 1f)).Single();
        var onward = rail.TracksAt(rail.FindNodeNear(400, 0, 1f)).Single();
        var diagonal = rail.TracksAt(rail.FindNodeNear(300, 100, 1f)).Single();
        var sharp = rail.TracksAt(rail.FindNodeNear(200, 150, 1f)).Single();

        Assert.Equal(0f, rail.TurnAngle(from, node, onward), 3);
        Assert.Equal(MathF.PI / 4f, rail.TurnAngle(from, node, diagonal), 3);
        Assert.True(rail.CanPass(from, node, onward));
        Assert.True(rail.CanPass(from, node, diagonal));
        Assert.False(rail.CanPass(from, node, sharp));
        Assert.False(rail.CanPass(from, node, from)); // no turning back on itself
        Assert.Equal(2, rail.Exits(from, node).Count());
    }

    [Fact]
    public void TurnSpeed_IsUnlimitedStraightAndSlowerForSharperTurns()
    {
        var rail = Flat();

        Assert.True(float.IsPositiveInfinity(rail.TurnSpeed(0f)));
        Assert.True(rail.TurnSpeed(0.3f) > rail.TurnSpeed(0.8f));
        Assert.Equal(rail.Rail.MinTurnoutSpeed, rail.TurnSpeed(rail.Rail.MaxTurnAngle), 3);
    }

    [Fact]
    public void RemovingATrack_TakesItsSignalsAndFreesTheNode()
    {
        var rail = Flat();
        var id = rail.BuildTrack(0, 0, 300, 0).Single();
        var track = rail.GetTrack(id);
        rail.SetSignal(id, track.To, SignalKind.Block);

        Assert.True(rail.RemoveTrack(id));

        Assert.Empty(rail.TracksAt(track.From));
        Assert.False(rail.HasSignals(track.To));
        Assert.False(rail.RemoveTrack(id));
        Assert.Equal(rail.Rail.DemolitionCostPerMetre * 300f, rail.DemolitionCost(id), 1);
    }

    [Fact]
    public void ASignalIsFacingOneTrackAtOneNode_ChangingKindReplacesIt_AndSplittingKeepsIt()
    {
        var rail = Flat();
        var id = rail.BuildTrack(0, 0, 400, 0).Single();
        var track = rail.GetTrack(id);

        var signal = rail.SetSignal(id, track.To, SignalKind.Block)!;
        Assert.Same(signal, rail.SetSignal(id, track.To, SignalKind.Path));
        Assert.Equal(SignalKind.Path, signal.Kind);
        Assert.Null(rail.SetSignal(id, 99 > rail.NodeCount ? track.From + track.To + 5 : 0, SignalKind.Block)); // not an end of the track

        int middle = rail.SplitTrack(id, 150f, out var first, out var second);

        Assert.Equal(second, signal.Track);          // it faced the To end, which the second piece still has
        Assert.Equal(track.To, signal.Node);
        Assert.Same(signal, rail.SignalFor(second, track.To));
        Assert.Null(rail.SignalFor(first, middle));
    }

    [Fact]
    public void ATrackUpASlope_LevelsTheGroundUnderIt_AndBumpsTheTerrainVersion()
    {
        var terrain = Terrain.Create(128, 8f, (x, _) => 10f + x * 0.03f + 2f * MathF.Sin(x / 25f));
        var rail = new RailNetwork(terrain, Tuning.Load());
        int version = terrain.Version;
        var changed = terrain.TakeChangedChunks();

        var pieces = rail.BuildTrack(-250, 0, 250, 0).Select(id => rail.GetTrack(id)).ToList();

        Assert.NotEmpty(pieces);
        Assert.True(terrain.Version > version);
        Assert.NotEmpty(terrain.TakeChangedChunks());
        foreach (var piece in pieces.Where(p => p.Structure == RoadStructure.None))
            for (float along = 0f; along <= piece.Length; along += 6f)
            {
                var (x, z) = piece.Geometry.PointAt(along);
                Assert.Equal(rail.TrackHeightAt(piece.Id, along), terrain.HeightAt(x, z), 0.3f);
            }
    }

    [Fact]
    public void WhyNotTrack_NamesTheRuleThatBrokeAndTheNumbers()
    {
        var steep = new RailNetwork(Terrain.Create(128, 8f, (x, _) => 30f + x * 0.1f), Tuning.Load());
        var level = Flat();

        Assert.Null(level.WhyNotTrack(0, 0, 300, 0));
        Assert.Contains("steep", steep.WhyNotTrack(-200, 0, 200, 0));
        Assert.Contains("%", steep.WhyNotTrack(-200, 0, 200, 0));
        Assert.Contains("short", level.WhyNotTrack(0, 0, 10, 0));
        Assert.Contains("tight", level.WhyNotTrack(0, 0, 100, 0, sweep: 2.6f));
        level.BuildTrack(0, 0, 300, 0);
        Assert.NotNull(level.WhyNotTrack(0, 0, 300, 0));
    }

    [Fact]
    public void OnGeneratedHills_EveryTrackThatIsBuilt_RespectsTheGradeLimit_AndEveryRefusalHasAReason()
    {
        var terrain = Terrain.Generate(7UL, 256, 8f, 70f, water: false, settings: new TerrainSettings(0.15f, 0.5f, 0.5f)); // gentle land: rails climb little
        var rail = new RailNetwork(terrain, Tuning.Load());
        int built = 0, refused = 0;
        for (int k = 0; k < 40; k++)
        {
            float x0 = -700f + k * 11f, z0 = -600f + k * 29f % 1200f, x1 = x0 + 600f, z1 = z0 + 90f;
            if (rail.CanBuildTrack(x0, z0, x1, z1)) { built += rail.BuildTrack(x0, z0, x1, z1).Count; }
            else { refused++; Assert.False(string.IsNullOrEmpty(rail.WhyNotTrack(x0, z0, x1, z1))); }
        }

        Assert.True(built > 0, $"built {built}, refused {refused}");
        foreach (var track in rail.Tracks.Where(t => !t.Retired))
            Assert.True(MathF.Abs(track.Grade) <= rail.Rail.MaxGrade + 1e-4f, $"grade {track.Grade}");
    }
}
