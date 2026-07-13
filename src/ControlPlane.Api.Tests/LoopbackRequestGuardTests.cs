using ControlPlane.Api.System;
using System.Net;

namespace ControlPlane.Api.Tests;

public sealed class LoopbackRequestGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.10", false)]
    public void RecognizesOnlyLoopbackAddresses(string address, bool expected) =>
        Assert.Equal(expected, LoopbackRequestGuard.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void RejectsMissingRemoteAddress() =>
        Assert.False(LoopbackRequestGuard.IsLoopback(null));
}
