# Windows TODO - Transport Rewrite

Last updated: 2026-06-13

This file is self-contained. An agent working only inside `WDCableWUI/` should be able to read this file and understand the product direction, current code shape, and next implementation milestones.

## Current Situation

This app is the Windows client. It is a .NET 8 / WinUI 3 app using Windows App SDK and Windows Wi-Fi Direct APIs.

The Wi-Fi Direct layer is already well tested and should be kept unless a specific bug proves otherwise:

- `WiFiDirectService.cs` handles advertising, scanning, connection requests, connect/disconnect, endpoint inspection, and role inference.
- `ConnectionPage.xaml.cs` drives most user-facing Wi-Fi Direct actions.

The part to replace is everything after Wi-Fi Direct creates the network link:

- `ConnectionService.cs` owns three raw TCP channels.
- Chat uses port `8888`.
- Speed test uses port `8889`.
- File transfer uses port `8890`.
- `ChatService.cs`, `SpeedTestService.cs`, and `FileTransferService.cs` parse and write ad hoc string headers directly on sockets.

That raw socket feature design is the source of fragility and future upgrade cost. The rewrite should replace it directly. Do not support the previous wire protocol and do not keep duplicate feature paths.

## Completed / Historical Work

- W-01 build warning and page subscription cleanup was completed. `dotnet build WDCableWUI.sln` was clean at that point.
- W-02 startup/service safety may be partly represented in current code. Verify before relying on it.
- W-03 Windows Wi-Fi Direct lifecycle/diagnostic work is considered implemented, but manual testing was not good enough. Treat it as useful cleanup, not proof that the current socket transport is stable.
- Android has already moved to the upgraded protocol/session runtime for control-channel chat, bulk file transfer, bulk speed test, diagnostics export, and Android-to-Android manual flow tested as generally good. Windows should now match that approach for cross-platform interop.
- Streaming is dropped from this phase. Do not implement audio/video/screen streaming, microphone capture, playback, jitter buffers, or streaming UI. If the current Android protocol still requires a `realtime` transport to reach `Ready`, Windows may mirror it only as a no-op compatibility channel.

## Target Architecture

After Wi-Fi Direct reports an IP link:

1. One Windows session manager owns all app transport setup.
2. Feature services do not read or write sockets directly.
3. The session manager performs protocol handshake, heartbeat, channel setup, teardown, and error reporting.
4. The UI reaches `Ready` only after the upgraded app protocol is negotiated.
5. Chat, file transfer, speed test, and diagnostics all use the session API.

Target channels:

- `control`: reliable small messages such as handshake, heartbeat, close, error, chat, command, ack, and feature control messages.
- `bulk`: reliable large ordered payloads such as file transfer, speed-test payloads, and diagnostics export.
- `realtime`: reserved/no-op only if needed for compatibility with the current Android protocol scaffolding. Do not send feature traffic on it in this phase.

Streaming scope:

- No streaming implementation work is scheduled.
- Do not add microphone capture, audio playback, codecs, sender pacing, jitter buffers, or streaming controls.
- A no-op `realtime` transport is allowed only if required to interoperate with the currently built Android protocol.

## Important Source Map

Services:

- `Services/WiFiDirectService.cs`: Windows Wi-Fi Direct lifecycle.
- `Services/ConnectionService.cs`: current raw socket owner. Replace it with the session runtime.
- `Services/ChatService.cs`: current chat protocol. Replace its socket path.
- `Services/SpeedTestService.cs`: current speed protocol. Replace its socket path.
- `Services/FileTransferService.cs`: current file protocol. Replace its socket path.
- `Services/ServiceManager.cs`: service bootstrap.
- `Services/DataManager.cs`: local settings and persistence.

UI:

- `MainWindow.xaml.cs`: shell connection display and events.
- `UI/Connection/ConnectionPage.xaml.cs`: Wi-Fi Direct page.
- `UI/Chat/ChatPage.xaml.cs`: chat page.
- `UI/SpeedTest/SpeedTestPage.xaml.cs`: speed page.
- `UI/FileTransfer/FileTransferPage.xaml.cs`: file page.
- `UI/Settings/SettingsPage.xaml.cs`: settings page.

Build/config:

- `WDCableWUI.csproj`: .NET 8, Windows App SDK, package references.
- `Package.appxmanifest`: package version and capabilities.
- `Strings/en/Resources.resw` and `Strings/zh-CN/Resources.resw`: localized resources.

## Agent Workflow Rules

- Work one milestone at a time, for example: `Do W-B only in WDCableWUI`.
- Each milestone is intentionally large. Complete implementation, focused tests, and local verification in the same pass.
- Do not ask the user to test after scaffolding-only milestones.
- Ask the user to install/test only at manual gates listed below.
- Keep Windows protocol constants, frame layout, state names, capability strings, and channel names aligned with Android.
- If `../PROTOCOL.md` exists, follow it. If it does not exist and W-A is the current task, create it.
- Do not add feature-level socket reads/writes. All app features should call the session API.
- Do not support previous builds at the protocol layer. When a feature is migrated, delete or disconnect its old socket path.
- Do not implement streaming features in Windows. Any `realtime` channel work is no-op compatibility only unless a later product decision explicitly reopens streaming.

## Standard Verification Commands

Run from `WDCableWUI/`:

```powershell
dotnet build WDCableWUI.sln
```

If a test project is added:

```powershell
dotnet test
```

If a command cannot run in the local environment, report the reason clearly.

## Manual Test Report Template

Use this only when a milestone says "Manual test gate".

```text
Task ID:
Build/run method: Visual Studio/dotnet/packaged/unpackaged
Windows device A: model, Windows version, Wi-Fi adapter
Windows device B: model, Windows version, Wi-Fi adapter, or not used
Android peer used: yes/no, Android model/version/build, or not used
Test scenarios run:
Results:
Crash/hang/stuck: yes/no
If stuck, last visible status/log line:
Screenshots/logs attached: yes/no
Notes:
```

## W-A - Protocol Spec And Windows Frame Codec

Goal: define the shared protocol and implement Windows frame encode/decode before replacing live feature paths.

No manual device test required.

Codex work:

- [x] Read the current protocol in `ConnectionService.cs`, `ChatService.cs`, `SpeedTestService.cs`, and `FileTransferService.cs`.
- [x] Create or update shared `../PROTOCOL.md` if missing.
- [x] Define Windows constants matching the shared spec:
  - magic
  - protocol version
  - frame header size
  - max metadata bytes
  - max payload bytes per frame
  - channel names: `control`, `bulk`, and optional no-op `realtime` only if Android compatibility requires it
  - frame type names/ids
  - capability strings: `control.chat`, `bulk.file`, `bulk.speed`, `diagnostics.export`; no new realtime/audio feature capability
- [x] Add C# protocol classes in a focused namespace, for example:
  - `ProtocolFrame`
  - `ProtocolFrameType`
  - `ProtocolChannel`
  - `ProtocolError`
  - `ProtocolCodec`
- [x] Implement binary frame encode/decode:
  - fixed magic/version
  - frame type
  - flags
  - channel id or stream id
  - sequence number
  - correlation id
  - metadata length
  - payload length
  - UTF-8 JSON metadata
  - payload bytes
- [x] Enforce maximum metadata and payload sizes.
- [x] Reject malformed magic/version and invalid lengths with typed protocol errors.
- [x] Add a test project if one does not exist.
- [x] Add tests for valid frames, partial reads, malformed headers, oversized metadata, oversized payload, zero-length payload, and JSON metadata round trip.
- [x] Do not wire new code into live feature flows yet unless the touched feature is fully replaced in the same milestone.
- [x] Run `dotnet build WDCableWUI.sln`.
- [x] Run tests if available.

Done means:

- Windows has a tested frame codec.
- `../PROTOCOL.md` documents exactly what Windows implemented.
- No agent is instructed to preserve the current wire protocol.

## W-B - Windows Session Runtime And Transport Adapter

Goal: create one Windows owner for transport/session lifecycle after Wi-Fi Direct connects.

No manual device test required unless the agent has both peer builds available. Local build/test verification is enough for this milestone.

Codex work:

- [x] Add a Windows `SessionManager` or equivalent single owner for:
  - current session id
  - peer info
  - role
  - control/bulk sockets or channels
  - optional no-op realtime socket/channel only if current Android interop requires it
  - accept/connect retries
  - handshake
  - heartbeat
  - teardown
  - disconnect reason
- [x] Add a transport abstraction so raw sockets are hidden behind:
  - connect
  - accept
  - read frame
  - write frame
  - close
  - cancel
- [x] Route post-link setup from `WiFiDirectService.cs` to the session manager.
- [x] Implement connection phases:
  - `WifiDirectConnected`
  - `ConnectingTransport`
  - `Handshaking`
  - `Ready`
  - `Degraded`
  - `Disconnecting`
  - `Disconnected`
  - `Failed`
- [x] Implement handshake with app id, protocol min/max, platform, app version, device name, role, session id, capabilities, and port/channel map. Advertise chat/file/speed/diagnostics; do not advertise realtime/audio as a feature.
- [x] Implement heartbeat and timeout.
- [x] Make cleanup idempotent.
- [x] Treat duplicate/stale Wi-Fi Direct connection callbacks for the same peer/role as idempotent; do not tear down a healthy session just because Windows reports the same link again.
- [x] Ensure all long-running reads/writes stop during disconnect, app shutdown, or session replacement.
- [x] Add events for session state, session ready, session failed, peer not running app, and disconnect reason.
- [x] Start replacing `ConnectionService` responsibilities with the session manager. Do not leave duplicate raw socket lifecycle owners.
- [x] Remove blocking `.Wait(...)` patterns from any new transport/session path.
- [x] Add focused tests for state transitions where practical.
- [x] Run `dotnet build WDCableWUI.sln`.
- [x] Run tests if available.

Done means:

- Windows owns app transport lifecycle in one session manager.
- Feature services no longer need to know about sockets.
- UI can display the new connection/session states when wired later.

## W-C - Windows UI State And Chat On Control Channel

Goal: make `Ready` mean the upgraded handshake is complete, then replace chat with `control` frames.

Manual test gate only after matching Android support exists, or when testing Windows-to-Windows with two upgraded Windows builds.

Codex work:

- [x] Add shared app/session state model for Windows UI.
- [x] Update `MainWindow` and feature pages so controls are enabled only after `Ready`.
- [x] Keep Wi-Fi Direct "connected" separate from app "ready".
- [x] Replace chat send/receive with `control` frames.
- [x] Include message id, timestamp, sender platform, and session id in chat metadata.
- [x] Add chat send result/failure handling instead of assuming optimistic send success.
- [x] Preserve message ordering per session.
- [x] Show a clear error when the peer is connected by Wi-Fi Direct but not running the upgraded WDCable protocol.
- [x] Delete the old chat socket path from the live app flow.
- [x] Add tests for ready/fail/chat event handling where practical.
- [x] Run `dotnet build WDCableWUI.sln`.
- [x] Run tests if available.

Manual test gate:

- [x] Windows-to-Windows or Windows-to-Android reaches `Ready`.
- [x] Send 10 chat messages each direction.
- [x] Disconnect and reconnect 5 times.
- [ ] Connect to a peer that has Wi-Fi Direct but not the upgraded WDCable protocol and confirm clear failure.

Done means:

- Chat no longer depends on newline JSON over a feature-owned socket.
- UI no longer treats Wi-Fi Direct alone as full app readiness.

## W-D - Windows Bulk Channel For File Transfer And Speed Test

Goal: replace file transfer and speed test with `bulk` streams.

Manual test gate only after matching peer support exists.

Codex work:

- [x] Implement reliable bulk stream API:
  - open stream
  - send metadata
  - send chunks
  - best-effort ack/error on `control`
  - cancel
  - error
  - close
- [x] Replace Windows file send/receive:
  - transfer id
  - safe file name metadata
  - zero-byte file support
  - duplicate filename handling
  - download path validation/fallback
  - partial-file cleanup
  - SHA-256 hash on complete when practical
- [x] Replace Windows speed test:
  - test id
  - `speed-request` download request metadata
  - `speed-data` payload metadata
  - timeout
  - cancel
  - failure result
  - no concurrent tests on the same session unless explicitly supported
- [x] Match Android bulk behavior:
  - `kind=file` for file streams
  - `kind=speed-request` for download requests
  - `kind=speed-data` for generated speed payload streams
  - unknown file size is `-1`
  - duplicate filenames are saved safely
  - completion is determined by `bulk.complete`; completion ack is diagnostic/best-effort, not required for sender success
- [x] Remove delimiter-sensitive `FILE:name:size` parsing from the live path.
- [x] Remove `SPEED_TEST_*` string headers from the live path.
- [x] Update Windows transfer/speed UI from protocol progress events.
- [x] Ensure `FileTransferPage` shows determinate progress.
- [x] Run `dotnet build WDCableWUI.sln`.
- [x] Run tests if available.

Manual test gate:

- [x] Transfer a small text file.
- [x] Transfer a duplicate filename twice.
- [x] Transfer a zero-byte file if available.
- [x] Change download location to a valid folder and receive a file.
- [x] Try an invalid/missing download path and verify graceful fallback.
- [x] Interrupt transfer by closing peer app or disabling Wi-Fi.
- [x] Run upload and download speed tests 5 times.
- [x] Disconnect during speed test and reconnect.

Done means:

- File and speed no longer depend on feature-owned sockets.
- Interrupted transfers/tests fail cleanly and do not corrupt the next operation.

## W-E - Windows Diagnostics And Release Gate

Goal: make Windows diagnosable and ready for cross-device beta/store validation.

Manual test gate required.

Codex work:

- [ ] Add structured ring-buffer logging for:
  - Wi-Fi Direct
  - session
  - transport
  - protocol
  - control
  - bulk
  - reserved realtime/no-op transport if present
  - chat
  - file
  - speed
  - UI
- [ ] Add export/copy logs from Windows UI.
- [ ] Include timestamp, session id, peer platform, role, channel, stream id, transfer id/test id, and disconnect reason in important logs.
- [ ] Add protocol and session tests where practical.
- [ ] Delete remaining raw socket feature code and stale scaffolding after chat/file/speed use the new runtime.
- [ ] Ensure `dotnet build WDCableWUI.sln` is clean.
- [ ] Ensure tests pass if available.
- [ ] Update README or release notes with the new manual QA checklist.

Manual test gate:

- [ ] Windows-to-Windows full flow.
- [ ] Windows-to-Android full flow in both initiation directions.
- [ ] Chat, file, and speed.
- [ ] Peer app missing.
- [ ] Outdated app build shows a clear upgrade-required failure.
- [ ] Reconnect loop 10 times.
- [ ] Wi-Fi off mid-transfer and during speed test.
- [ ] Export logs after one success and one failure.

Done means:

- Windows is ready for coordinated release validation with Android.
