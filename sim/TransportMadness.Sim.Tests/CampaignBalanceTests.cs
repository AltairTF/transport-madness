using TransportMadness.Sim;
using Xunit.Abstractions;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// The balance of the campaign (M6c), measured by the bots of <see cref="CampaignBots"/> playing whole campaigns on generated maps of five towns. Targets
/// agreed with the developer: an idle committee is dismissed between months 20 and 30, a company that runs only buses with no maintenance in the twenties
/// as well, and a competent player ends with the second-best grade (the best is for strong human play: exports and towns that are supplied). A whole
/// campaign takes about a minute each, so these are in the <c>Balance</c> category and run only with BALANCE_BOTS set:
/// <c>BALANCE_BOTS=1 dotnet test -c Release --filter Category=Balance</c> (or the manual "campaign balance" workflow). The short probe at the end runs always.
/// </summary>
public class CampaignBalanceTests(ITestOutputHelper output)
{
    static bool On => Environment.GetEnvironmentVariable("BALANCE_BOTS") != null;

    CampaignResult Play(CampaignBots.Bot bot, ulong seed)
    {
        var r = CampaignBots.Play(bot, seed);
        output.WriteLine(r.ToString());
        return r;
    }

    [Fact, Trait("Category", "Balance")]
    public void AnIdleCommittee_IsDismissedBetweenMonths20And30()
    {
        if (!On) return;
        foreach (ulong seed in new ulong[] { 1, 2 })
            Assert.InRange(Play(CampaignBots.Bot.Idle, seed).DismissedAt, 20, 30);
    }

    [Fact, Trait("Category", "Balance")]
    public void ACompanyOfBusesWithoutMaintenance_IsDismissedInItsSecondYear()
    {
        if (!On) return;
        foreach (ulong seed in new ulong[] { 1, 2 })
            Assert.InRange(Play(CampaignBots.Bot.BusesNoMaintenance, seed).DismissedAt, 15, 30);
    }

    [Theory, Trait("Category", "Balance")]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    [InlineData(6UL)]
    public void ACompetentPlayer_EndsWithTheSecondBestGrade(ulong seed)
    {
        if (!On) return;
        var r = Play(CampaignBots.Bot.Competent, seed);
        Assert.Equal(-1, r.DismissedAt);
        Assert.Equal(1, r.Grade);
    }

    /// <summary>
    /// Always run (about five seconds): a year of an idle committee loses what the phase expects over the export scale each month, so the
    /// numbers that set the pace of a dismissal cannot drift without a test failing.
    /// </summary>
    [Fact]
    public void AYearOfDoingNothing_CostsTheStandingThePhaseExpects()
    {
        var r = CampaignBots.Play(CampaignBots.Bot.Idle, 1, months: 12);
        var c = Tuning.Load().Campaign;
        float perMonth = MathF.Min(c.MaxExportSwing, c.PhaseOf(0).ExpectedValue / c.ExportScale);
        Assert.Equal(c.StartStanding - 12 * perMonth, r.Standing, 0.01f);
        Assert.InRange(perMonth, 1.5f, 2.5f); // the pace that puts an idle committee out between months 20 and 30
    }
}
