using EndpointAgents.Platform.Windows.Actions.SystemSide;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class KmsActivationTests
{
    private static Dictionary<string, object?> Row(string description, uint status,
        string? machine = null, uint port = 0, uint reason = 0) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ID"] = "a1b2", ["Name"] = "Office 21, Office21ProPlus2021VL_KMS_Client_AE edition",
        ["Description"] = description, ["LicenseStatus"] = status,
        ["LicenseStatusReason"] = reason, ["KeyManagementServiceMachine"] = machine,
        ["KeyManagementServicePort"] = port
    };

    [Fact]
    public void VolumeClientThatPointsAtTheOrganisationServerIsRecognised()
    {
        var state = KmsLicensing.FromRow(Row("Office 21, VOLUME_KMSCLIENT channel", 1,
            "172.16.0.2", 1688), "172.16.0.2", 1688);
        Assert.True(state.VolumeClient);
        Assert.True(state.Licensed);
        Assert.True(state.UsesOrganisationKms);
        Assert.Null(state.ErrorCode);
    }

    [Fact]
    public void DefaultPortZeroMatchesTheDefaultKmsPort()
    {
        Assert.True(KmsLicensing.FromRow(Row("VOLUME_KMSCLIENT", 1, "KMS.corp.local"),
            "kms.corp.local", 1688).UsesOrganisationKms);
        Assert.False(KmsLicensing.FromRow(Row("VOLUME_KMSCLIENT", 1, "kms.corp.local"),
            "kms.corp.local", 1700).UsesOrganisationKms);
    }

    [Fact]
    public void RetailOrUnlicensedProductsAreReportedWithTheirErrorCode()
    {
        var retail = KmsLicensing.FromRow(Row("RETAIL channel", 1), "172.16.0.2", 1688);
        Assert.False(retail.VolumeClient);
        Assert.False(retail.UsesOrganisationKms);
        var notActivated = KmsLicensing.FromRow(Row("VOLUME_KMSCLIENT", 5, "other.host",
            reason: 0xC004F074), "172.16.0.2", 1688);
        Assert.False(notActivated.Licensed);
        Assert.False(notActivated.UsesOrganisationKms);
        Assert.Equal("0xC004F074", notActivated.ErrorCode);
    }
}
