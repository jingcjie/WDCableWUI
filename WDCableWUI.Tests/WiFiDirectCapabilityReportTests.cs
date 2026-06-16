using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class WiFiDirectCapabilityReportTests
{
    [TestMethod]
    public void MissingP2PDeviceDiscoveryMarksAdapterUnsupported()
    {
        const string output = """
            Wireless Device Capabilities
            ----------------------------

            Interface name: Wi-Fi

                Station                                     : Supported
                Wi-Fi Direct Device                         : Supported
                Wi-Fi Direct GO                             : Supported
                Wi-Fi Direct Client                         : Supported
                P2P Device Discovery                        : Not Supported
                P2P Service Name Discovery                  : Not Supported
            """;

        var report = WiFiDirectCapabilityReport.Parse(output);
        var missing = report.GetMissingRequiredCapabilities();

        Assert.AreEqual("Wi-Fi", report.InterfaceName);
        CollectionAssert.Contains(missing.ToList(), WiFiDirectCapabilityReport.P2PDeviceDiscovery);
    }

    [TestMethod]
    public void RequiredSupportedCapabilitiesPass()
    {
        const string output = """
            Interface name: Wi-Fi

                Wi-Fi Direct Device                         : Supported
                Wi-Fi Direct GO                             : Supported
                Wi-Fi Direct Client                         : Supported
                P2P Device Discovery                        : Supported
            """;

        var report = WiFiDirectCapabilityReport.Parse(output);

        Assert.AreEqual(0, report.GetMissingRequiredCapabilities().Count);
    }

    [TestMethod]
    public void UnreportedCapabilitiesDoNotCreateFalseNegative()
    {
        const string output = """
            Interface name: Wi-Fi

                Station                                     : Supported
                Soft AP                                     : Supported
            """;

        var report = WiFiDirectCapabilityReport.Parse(output);

        Assert.AreEqual(0, report.GetMissingRequiredCapabilities().Count);
        Assert.IsNull(report.IsCapabilitySupported(WiFiDirectCapabilityReport.P2PDeviceDiscovery));
    }
}
