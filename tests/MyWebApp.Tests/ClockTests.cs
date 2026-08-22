using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class ClockTests
{
    [Fact]
    public void Initially_Tracks_Real_Utc()
    {
        var clock = new PechkaTestClock();
        Assert.Equal(TimeSpan.Zero, clock.Offset);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Advance_Appends_To_The_Offset()
    {
        var clock = new PechkaTestClock();
        clock.Advance(TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(90), clock.Offset);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(89), TimeSpan.FromMinutes(91));
    }

    [Fact]
    public void Backward_Time_Travel_Is_Refused()
    {
        var clock = new PechkaTestClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
        Assert.Equal(TimeSpan.Zero, clock.Offset);
    }
}
