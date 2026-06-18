using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class WiFiDirectAdapterRemovalLauncherTests
{
    [TestMethod]
    public void CleanupCommandTargetsOnlyNonPresentNumberedWiFiDirectAdapters()
    {
        var command = WiFiDirectAdapterRemovalLauncher.BuildPowerShellCommand(
            1234,
            @"C:\Apps\WDCableWUI.exe");

        StringAssert.Contains(command, "$_.Present -eq $false");
        StringAssert.Contains(
            command,
            "$_.FriendlyName -match '^Microsoft Wi-Fi Direct Virtual Adapter #\\d+$'");
        StringAssert.Contains(command, "$_.InstanceId -match '\\\\VWIFIMP_WFD\\\\'");
        Assert.IsFalse(
            command.Contains("Get-NetAdapter", StringComparison.OrdinalIgnoreCase),
            "Operational network adapters must never be included in stale cleanup.");
        Assert.IsFalse(
            command.Contains("-like 'Microsoft Wi-Fi Direct Virtual Adapter*'", StringComparison.Ordinal),
            "The unnumbered operational adapter must never match stale cleanup.");
        StringAssert.Contains(command, "pnputil.exe\" /remove-device");
        StringAssert.Contains(command, "/subtree");
        Assert.IsFalse(
            command.Contains("pnputil.exe\" /remove-device /class Net", StringComparison.OrdinalIgnoreCase),
            "The cleanup command must never remove the entire network adapter class.");
        Assert.IsFalse(
            command.Contains("/delete-driver", StringComparison.OrdinalIgnoreCase),
            "The cleanup command must not delete Wi-Fi driver packages.");
    }

    [TestMethod]
    public void CleanupCommandWaitsForParentAndRestartsTheSameExecutable()
    {
        var command = WiFiDirectAdapterRemovalLauncher.BuildPowerShellCommand(
            4321,
            @"C:\Apps\WDCableWUI.exe");

        StringAssert.Contains(command, "$parentProcessId = 4321");
        StringAssert.Contains(command, "Wait-Process -Id $parentProcessId");
        StringAssert.Contains(command, "$restartTarget = 'C:\\Apps\\WDCableWUI.exe'");
        StringAssert.Contains(command, "Start-Process -FilePath \"$env:SystemRoot\\explorer.exe\"");
    }

    [TestMethod]
    public void CleanupCommandUsesAppUserModelIdForPackagedRestart()
    {
        var command = WiFiDirectAdapterRemovalLauncher.BuildPowerShellCommand(
            4321,
            @"C:\Program Files\WindowsApps\WDCableWUI.exe",
            "JINGCJIE.4084573DC88A9_abc!App");

        StringAssert.Contains(
            command,
            "$appUserModelId = 'JINGCJIE.4084573DC88A9_abc!App'");
        StringAssert.Contains(command, "'shell:AppsFolder\\' + $appUserModelId");
    }

    [TestMethod]
    public void CleanupCommandEscapesExecutablePathForPowerShell()
    {
        var command = WiFiDirectAdapterRemovalLauncher.BuildPowerShellCommand(
            12,
            @"C:\User's Apps\WDCableWUI.exe");

        StringAssert.Contains(command, "$restartTarget = 'C:\\User''s Apps\\WDCableWUI.exe'");
    }

    [TestMethod]
    public void EncodedCommandRoundTripsAsPowerShellUnicode()
    {
        const string command = "Write-Output 'Wi-Fi Direct'";

        var encoded = WiFiDirectAdapterRemovalLauncher.EncodePowerShellCommand(command);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.AreEqual(command, decoded);
    }
}
