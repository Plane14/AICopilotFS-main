using AICopilotFS.Domain.FlightPlanning;

namespace AICopilotFS.Domain.Tests.FlightPlanning;

public class RouteBuilderTests
{
    [Fact]
    public void BuildRoute_ReturnsFlightPlanWithAirports()
    {
        var builder = new RouteBuilder();
        var plan = builder.BuildRoute("KSEA", "KPDX", "16L");

        Assert.Equal("KSEA", plan.DepartureIcao);
        Assert.Equal("KPDX", plan.DestinationIcao);
        Assert.Equal("16L", plan.DepartureRunway);
    }
}
