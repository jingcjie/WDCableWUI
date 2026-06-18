using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WDCableWUI.Services;

internal sealed record WiFiDirectAdapterRemovalLaunchResult(
    bool Started,
    bool ElevationCanceled,
    string? ErrorMessage);

internal static class WiFiDirectAdapterRemovalLauncher
{
    private const int ElevationCanceledErrorCode = 1223;

    public static WiFiDirectAdapterRemovalLaunchResult Launch(
        int parentProcessId,
        string executablePath,
        string? appUserModelId = null)
    {
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The application executable path is required.", nameof(executablePath));
        }

        try
        {
            var encodedCommand = EncodePowerShellCommand(
                BuildPowerShellCommand(parentProcessId, executablePath, appUserModelId));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            return process == null
                ? new WiFiDirectAdapterRemovalLaunchResult(
                    Started: false,
                    ElevationCanceled: false,
                    ErrorMessage: "Windows did not start the elevated cleanup process.")
                : new WiFiDirectAdapterRemovalLaunchResult(
                    Started: true,
                    ElevationCanceled: false,
                    ErrorMessage: null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCanceledErrorCode)
        {
            return new WiFiDirectAdapterRemovalLaunchResult(
                Started: false,
                ElevationCanceled: true,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            return new WiFiDirectAdapterRemovalLaunchResult(
                Started: false,
                ElevationCanceled: false,
                ErrorMessage: ex.Message);
        }
    }

    internal static string BuildPowerShellCommand(
        int parentProcessId,
        string executablePath,
        string? appUserModelId = null)
    {
        var escapedExecutablePath = EscapePowerShellSingleQuotedString(executablePath);
        var escapedAppUserModelId = EscapePowerShellSingleQuotedString(appUserModelId ?? string.Empty);
        var logPath = EscapePowerShellSingleQuotedString(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WDCable",
                "wifi-direct-adapter-cleanup.log"));

        return $$"""
            $ErrorActionPreference = 'Continue'
            $parentProcessId = {{parentProcessId.ToString(CultureInfo.InvariantCulture)}}
            $restartTarget = '{{escapedExecutablePath}}'
            $appUserModelId = '{{escapedAppUserModelId}}'
            $logPath = '{{logPath}}'
            $logDirectory = Split-Path -Parent $logPath
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

            function Write-CleanupLog([string] $message) {
                $line = ('{0:o} {1}' -f [DateTimeOffset]::Now, $message)
                Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
            }

            Write-CleanupLog 'Elevated stale Wi-Fi Direct adapter cleanup started.'
            try {
                Wait-Process -Id $parentProcessId -Timeout 30 -ErrorAction SilentlyContinue
            } catch {
                Write-CleanupLog ('Waiting for WDCable to exit failed: ' + $_.Exception.Message)
            }

            $devices = @(
                Get-PnpDevice -Class Net -PresentOnly:$false -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Present -eq $false -and
                        $_.FriendlyName -match '^Microsoft Wi-Fi Direct Virtual Adapter #\d+$' -and
                        $_.InstanceId -match '\\VWIFIMP_WFD\\'
                    } |
                    Select-Object -Property FriendlyName, InstanceId, Present |
                    Sort-Object -Property InstanceId -Unique
            )

            Write-CleanupLog ('Matched stale numbered Wi-Fi Direct adapters: ' + $devices.Count)
            foreach ($device in $devices) {
                Write-CleanupLog ('Removing ' + $device.FriendlyName + ' [' + $device.InstanceId + ']')
                & "$env:SystemRoot\System32\pnputil.exe" /remove-device "$($device.InstanceId)" /subtree
                if ($LASTEXITCODE -eq 0) {
                    Write-CleanupLog ('Removed [' + $device.InstanceId + ']')
                } else {
                    Write-CleanupLog ('Removal failed with exit code ' + $LASTEXITCODE + ' [' + $device.InstanceId + ']')
                }
            }

            Write-CleanupLog 'Stale adapter cleanup finished; restarting WDCable.'
            if (-not [string]::IsNullOrWhiteSpace($appUserModelId)) {
                Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList ('shell:AppsFolder\' + $appUserModelId)
            } else {
                Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList ('"' + $restartTarget + '"')
            }
            """;
    }

    internal static string EncodePowerShellCommand(string command)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
