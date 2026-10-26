using VibeShift.Core.Switching;

namespace VibeShift.Core.Tests;

public class ServicesTests
{
    [Fact]
    public void Helper_args()
    {
        var (verb, names) = Services.ParseHelperArgs(new[] { "--services", "stop", "SysMain,WSearch" })!.Value;
        Assert.Equal("stop", verb);
        Assert.Equal(new[] { "SysMain", "WSearch" }, names);
        Assert.Null(Services.ParseHelperArgs(new[] { "--services", "delete", "x" }));
        Assert.Null(Services.ParseHelperArgs(new[] { "--tray" }));
    }

    [Fact]
    public void Missing_service_is_not_running() => Assert.False(Services.IsRunning("NoSuchService_VibeShift"));
}
