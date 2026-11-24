using AudiobookApp.Services;

namespace AudiobookApp.Tests;

public class EpubTtsSleepTimerTests
{
    [Fact]
    public void StartMinutes_ThenExpires()
    {
        var timer = new EpubTtsSleepTimer();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        timer.StartMinutes(5, start);

        Assert.True(timer.IsActive);
        Assert.False(timer.HasExpired(start.AddMinutes(4)));
        Assert.True(timer.HasExpired(start.AddMinutes(5)));
        Assert.Equal(TimeSpan.Zero, timer.GetRemaining(start.AddMinutes(6)));
    }

    [Fact]
    public void Cancel_ClearsActiveState()
    {
        var timer = new EpubTtsSleepTimer();
        timer.StartMinutes(10, DateTimeOffset.UtcNow);
        timer.Cancel();
        Assert.False(timer.IsActive);
        Assert.False(timer.HasExpired());
    }

    [Fact]
    public void FormatRemaining_ShowsMinutes()
    {
        var timer = new EpubTtsSleepTimer();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        timer.StartMinutes(5, start);
        var formatted = timer.FormatRemaining(start.AddMinutes(1));
        Assert.Equal("04:00", formatted);
    }
}
