using EndpointAgents.Platform.Windows.Windows;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class WindowsVersionInfoTests
{
    [Theory]
    [InlineData("Windows 10 Pro", "21999", "Windows 10 Pro")]
    [InlineData("Windows 10 Pro", "22000", "Windows 11 Pro")]
    [InlineData("Windows 10 Enterprise", "28000", "Windows 11 Enterprise")]
    [InlineData("Windows Server 2025 Standard", "26100", "Windows Server 2025 Standard")]
    public void NormalizesWindowsProductName(string productName, string build,
        string expected) => Assert.Equal(expected,
            WindowsVersionInfo.NormalizeProductName(productName, build));
}
