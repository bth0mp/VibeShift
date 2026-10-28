using VibeShift.Core.State;
using VibeShift.Core.Switching;

namespace VibeShift.Core.Tests;

public class PowerPlanTests
{
    // Real output from a Lenovo Legion: OEM plans only, one name with a trailing space.
    private const string Legion = """

        Existing Power Schemes (* Active)
        -----------------------------------
        Power Scheme GUID: 16edbccd-dee9-4ec4-ace5-2f0b5f2a8975  (Legion Quiet Mode)
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
        Power Scheme GUID: 52521609-efc9-4268-b9ba-67dea73f18b2  (Legion Performance Mode)
        Power Scheme GUID: 85d583c5-cf2e-4197-80fd-3789a227a72c  (Legion Balance Mode ) *

        """;

    [Fact]
    public void Parses_plans_and_the_active_one()
    {
        var plans = PowerPlans.Parse(Legion.ReplaceLineEndings("\r\n"));
        Assert.Equal(["Legion Quiet Mode", "Balanced", "Legion Performance Mode", "Legion Balance Mode"], plans.Select(p => p.Name));
        Assert.Equal("85d583c5-cf2e-4197-80fd-3789a227a72c", Assert.Single(plans, p => p.Active).Guid);
    }

    [Fact] // the label is translated, the GUID and (name) aren't; names may contain brackets
    public void Parses_other_languages_and_bracketed_names()
    {
        var plans = PowerPlans.Parse("GUID du mode de gestion de l'alimentation : 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (Performances élevées (copie)) *\r\n");
        var p = Assert.Single(plans);
        Assert.Equal(("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "Performances élevées (copie)", true), (p.Guid, p.Name, p.Active));
    }

    [Fact]
    public void Session_remembers_the_plan_to_restore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vibeshift-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonStore(dir);
            store.SaveSession(new SessionState { PowerPlanBefore = "381b4222-f694-41f0-9685-ff5bb260df2e" });
            Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", store.LoadSession()!.PowerPlanBefore);
            store.SaveSettings(new Settings { GamingPowerPlan = "52521609-efc9-4268-b9ba-67dea73f18b2" });
            Assert.Equal("52521609-efc9-4268-b9ba-67dea73f18b2", store.LoadSettings().GamingPowerPlan);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
