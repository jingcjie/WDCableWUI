# Wi-Fi Direct Connection Improvement Plan - Windows WinUI

Updated: 2026-06-18

This file is written for code agents. Keep Windows and Android behavior separate. Do not copy Android lifecycle rules into this WinUI project.

## Official Sources Checked

- Microsoft `WiFiDirectAdvertisementPublisher`: publishes Wi-Fi Direct advertisements; Mobile Hotspot can make this class stop working because Mobile Hotspot takes precedence over Wi-Fi Direct.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.wifidirectadvertisementpublisher
- Microsoft `WiFiDirectAdvertisement.ListenStateDiscoverability`: controls listen state and discoverability; default is `None`.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.wifidirectadvertisement.listenstatediscoverability
- Microsoft `WiFiDirectAdvertisementListenStateDiscoverability`: `Normal` is highly discoverable while the app is foreground; `Intensive` is discoverable foreground/background.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.wifidirectadvertisementlistenstatediscoverability
- Microsoft `WiFiDirectConnectionListener`: listens for incoming Wi-Fi Direct connection requests through `ConnectionRequested`.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.wifidirectconnectionlistener
- Microsoft `WiFiDirectDevice`: use `FromIdAsync`, `ConnectionStatusChanged`, and `GetConnectionEndpointPairs`; only one app can connect to a Wi-Fi Direct device at a time; Proximity capability is required.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.wifidirectdevice
- Microsoft `DeviceWatcher`: `EnumerationCompleted` is not stopped; it continues to raise added/updated/removed events. Wait for `Stopped` before restarting a watcher.
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher
- Microsoft app capability declarations: `privateNetworkClientServer` allows local inbound/outbound networking; `proximity` is the device capability for close-proximity communication.
  https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations

## User Experience Goal

- Opening the Windows app should automatically make this PC available to nearby WDCable peers.
- The user should not need an "Accept Invitation" toggle.
- Incoming connection requests should always show a clear accept/reject dialog.
- The user can decide whether to scan for other devices. Scanning is user-driven, not automatic.
- Debug state should explain exactly which native API call or callback changed the flow.

## Current Repo Facts

- Core service: `Services/WiFiDirectService.cs`.
- UI page: `UI/Connection/ConnectionPage.xaml` and `.xaml.cs`.
- The current `Connection_AcceptInvitation` toggle is actually a discoverability toggle. It starts/stops `WiFiDirectAdvertisementPublisher`.
- `ConnectionPage.OnNavigatedFrom` stops scanning. That makes scanning page-owned.
- Incoming request prompts are currently subscribed by `ConnectionPage`; if discoverability becomes app-global, request prompting must move out of the page.
- `Package.appxmanifest` currently has `privateNetworkClientServer`, `internetClient`, `wiFiControl`, and `radios`, but should be checked for `DeviceCapability Name="proximity"` because Microsoft documents Proximity as required for Wi-Fi Direct communication.

## Target Windows Model

The singleton `WiFiDirectService` owns all native Wi-Fi Direct objects. Pages observe and command it; pages do not create, restart, or dispose native lifecycle objects except through service methods.

State names to use in diagnostics:

- `Unavailable`
- `Idle`
- `Advertising`
- `Scanning`
- `IncomingPrompt`
- `ConnectingOutbound`
- `ConnectingInbound`
- `Connected`
- `Disconnecting`
- `Error`

Keep these as service state, not page state.

## Startup Presence

Implement a service method like `EnsureDiscoverableAsync(reason)` and call it once after `ServiceManager.Initialize()` succeeds, preferably from `ServiceManager` or `MainWindow` startup.

Rules:

- Create one `WiFiDirectAdvertisementPublisher` and one `WiFiDirectConnectionListener` per service lifecycle.
- Set `Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal` first. Use `Intensive` only after a separate battery/background test proves it is needed.
- Keep the existing autonomous group owner setting unless a focused Windows/Android test proves it hurts compatibility.
- Subscribe to publisher `StatusChanged`.
- If publisher starts, state becomes `Advertising`.
- If publisher aborts, log the error and show a retry action. Do not enter an immediate restart loop.
- Do not stop advertising when scanning starts.
- Do not stop advertising when connecting.
- Stop advertising only on app shutdown, Wi-Fi Direct unavailable, or explicit service cleanup.

## Remove The Accept Invitation Toggle

Planned UI change:

- Remove `DiscoverableToggle` from `ConnectionPage.xaml`.
- Remove `OnDiscoverableToggled` and UI references to `DiscoverableToggle` in `ConnectionPage.xaml.cs`.
- Remove or stop using `Connection_AcceptInvitation.*` resource keys.
- Replace the toggle with read-only presence status, for example:
  - `Discoverable: Starting`
  - `Discoverable: Active`
  - `Discoverable: Failed - Mobile Hotspot may be enabled`
  - optional `Retry` button calling `EnsureDiscoverableAsync("user_retry")`

Important: incoming requests must still show accept/reject. Removing the toggle does not mean auto-accept.

## Incoming Request Flow

Move incoming request ownership out of `ConnectionPage`.

Preferred design:

- `WiFiDirectService` raises an app-level `ConnectionRequested` event with request metadata and a response task.
- `MainWindow` or a small `ConnectionPromptService` subscribes for the whole app lifetime.
- Show one accept/reject dialog at a time.
- If the user accepts, continue with `WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id)`.
- If the user rejects or timeout expires, dispose the request and return to `Advertising`.

Rules:

- Do not auto-accept incoming requests.
- If already connected or connecting, reject or queue exactly one prompt. Prefer reject with a clear status for v1.
- If scanning is active and user accepts an incoming request, stop scan before forming the connection. Keep advertising/listener resources alive unless Windows reports they must be recreated.

## User Scan Flow

Scanning is separate from being discoverable.

Rules:

- User clicks Scan to call service `StartScanAsync(reason)`.
- Use `WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint)`.
- Create a `DeviceWatcher` only when no watcher is active.
- Treat `EnumerationCompleted` as "initial enumeration complete", not as scan stopped.
- Keep the watcher alive after `EnumerationCompleted` so Added/Updated/Removed continue to update UI.
- User Stop Scan calls `StopScanAsync(reason)`.
- When stopping, wait for `DeviceWatcher.Stopped` before creating or starting another watcher.
- Do not clear the peer list on every transient stop unless the user explicitly stops scan or Wi-Fi Direct resets.

## Outbound Connect Flow

Rules:

- Reject connect if `Connected`, `ConnectingOutbound`, `ConnectingInbound`, or `IncomingPrompt`.
- If scan is running, stop watcher and wait for `Stopped` or a short timeout before `FromIdAsync`.
- Do not stop advertising/listener during outbound connect.
- Call `WiFiDirectDevice.FromIdAsync(device.Id)`.
- Subscribe to `ConnectionStatusChanged`.
- Do not raise `DeviceConnected` to session services until endpoint readiness passes.
- Endpoint readiness means at least one IPv4 endpoint pair from `GetConnectionEndpointPairs()`.
- Poll endpoint pairs with timeout, for example 10 seconds with 250 ms delay.
- Store `LocalIP`, `RemoteIP`, and inferred role only after endpoint validation.
- If endpoint validation fails, dispose the `WiFiDirectDevice`, keep/restore advertising, and return to `Idle` or `Advertising`.

## Disconnect And Cleanup

Rules:

- `DisconnectAsync(reason)` disposes the active `WiFiDirectDevice` and clears endpoint state.
- It should not destroy publisher/listener unless the app is shutting down or Wi-Fi Direct is unavailable.
- After disconnect, return to `Advertising`.
- Do not auto-restart scan. If the user had scan desired before connect, restore it only after a separate explicit `scanDesired` flag is added and tested.

## Diagnostics Required

Every log line should include:

```text
timestamp | platform=windows | opId | state | api | callback | result | reason | peerId | peerName | watcherStatus | publisherStatus | localIp | remoteIp
```

Decode native state names and errors. Do not log only numeric or enum integer values.

Minimum events to log:

- service initialized
- discoverable desired true/false
- publisher created/started/stopped/aborted
- listener created/disposed
- incoming request received/accepted/rejected/timed out
- watcher created/started/enumeration completed/stopping/stopped/aborted
- peer added/updated/removed
- connect requested
- scan pause before connect
- `FromIdAsync` success/failure
- endpoint polling start/success/timeout
- connection status changed
- disconnect start/complete

## Manifest Check

Before packaging tests, verify `Package.appxmanifest`.

Expected capabilities for this app:

```xml
<Capability Name="privateNetworkClientServer" />
<Capability Name="internetClient" />
<DeviceCapability Name="proximity" />
<DeviceCapability Name="wiFiControl" />
<DeviceCapability Name="radios" />
```

`privateNetworkClientServer` is needed for local socket traffic. `proximity` is documented by Microsoft for Wi-Fi Direct device communication. `wiFiControl` and `radios` should stay only if current capability validation or settings features require them.

## Implementation Order

1. Add diagnostics only. No lifecycle behavior changes.
2. Move incoming prompt ownership from `ConnectionPage` to app-level `MainWindow` or `ConnectionPromptService`.
3. Add global startup discoverability in `WiFiDirectService`.
4. Remove the "Accept Invitation" toggle UI and resource usage.
5. Refactor scanning into service-owned watcher lifecycle with `Stopped` handling.
6. Add endpoint readiness gate before `DeviceConnected`.
7. Add manifest `proximity` if missing.
8. Test Windows-to-Android, Android-to-Windows, reject incoming request, timeout incoming request, scan while discoverable, connect while scanning, disconnect and reconnect, Mobile Hotspot enabled failure.

## Do Not Do

- Do not recreate publisher/listener on every page navigation.
- Do not let `ConnectionPage` be the only owner of incoming request prompts.
- Do not treat `EnumerationCompleted` as watcher stopped.
- Do not restart `DeviceWatcher` until the previous watcher reaches `Stopped`.
- Do not raise session startup before endpoint pairs are valid.
- Do not auto-accept requests because the app is discoverable.
- Do not port Android `startListening`, `discoverPeers`, or `removeGroup` assumptions into Windows code.
