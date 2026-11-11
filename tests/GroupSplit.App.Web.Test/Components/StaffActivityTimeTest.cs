using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class StaffActivityTimeTest : ComponentTest
{
    [Theory]
    [InlineData(-300)]
    [InlineData(330)]
    public void Activity_history_shows_the_viewers_local_time(int minutes)
    {
        ReaderAt(TimeSpan.FromMinutes(minutes));
        var staff = new Mock<IStaffClient>();
        var instant = new DateTimeOffset(2026, 11, 11, 1, 30, 0, TimeSpan.Zero);
        staff.Setup(s => s.ActivityAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<int>()))
            .ReturnsAsync(new DirectoryPage<StaffActivityResponse>(
                [new(Guid.NewGuid(), "Tickets", Guid.NewGuid(), "Updated", "Alex", instant, [])], false));
        staff.Setup(s => s.TeamAsync()).ReturnsAsync([]);
        Services.AddSingleton(staff.Object);

        var feed = Render<StaffActivityFeed>();
        Assert.Contains(instant.ToOffset(TimeSpan.FromMinutes(minutes)).ToString("g"), feed.Find("small").TextContent);
    }
}
