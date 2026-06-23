<div align="center">
  <img src="figures/icon.png" width="96" alt="WDCable app icon">

  <h1>WDCable for Windows</h1>

  <p>
    <strong>WiFi Direct Cable (WDCable)</strong> connects Windows and Android devices directly
    for fast file sharing, messaging, speed testing, and audio streaming—without a router,
    hotspot, or internet connection.
  </p>

  <a href="https://apps.microsoft.com/store/detail/9MZQMRHFFJJW?cid=DevShareMCLPCS">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get WDCable from the Microsoft Store" width="200">
  </a>

  <p>Windows 10 version 1809+ and Windows 11 · x64 and ARM64 · Wi-Fi Direct</p>
</div>

<p align="center">
  <img src="figures/Invitation_incoming.png" width="900" alt="WDCable receiving a connection invitation from an Android device">
</p>

<p align="center">
  <em>Receive and accept connection invitations directly from an Android device.</em>
</p>

## What's New in 2.0.1

Version 2.0.1 is the Protocol v2 release. It separates Wi-Fi Direct group-owner/client roles from WDCable TCP listener/connector roles, uses UDP rendezvous when needed, and improves diagnostics for transport setup and handshake failures.

Audio Link now uses RTP/RTCP over UDP with libopus for lower-latency streaming. Senders can choose from Standard (32 kbps), Balanced (64 kbps), High (128 kbps), and Near lossless (256 kbps) quality presets, along with Low latency and Stable latency modes.

> [!IMPORTANT]
> Protocol v2 is not compatible with Protocol v1. Both devices must run WDCable 2.0.1 or another Protocol v2-compatible build.

## ✨ Features

- **Connect without network infrastructure** — Link nearby devices directly over Wi-Fi Direct, with no router, hotspot, or internet connection required.
- **Transfer files quickly** — Send files between Windows and Android with drag-and-drop support and a clear transfer history.
- **Chat across devices** — Exchange messages with a connected device over the same direct connection.
- **Stream audio with Audio Link** — Send Windows system audio to Android or stream Android microphone audio to Windows using low-latency RTP/RTCP and Opus encoding, with selectable quality and latency modes.
- **Measure connection performance** — Run a built-in speed test to check the quality and throughput of the direct link.
- **Use a native Windows experience** — Work in a modern WinUI 3 interface designed for Windows 10 and Windows 11.

## 📸 Screenshots

<table>
  <tr>
    <td width="50%" align="center">
      <img src="figures/Screenshot1.png" alt="WDCable connection page">
      <br><strong>Connection</strong>
    </td>
    <td width="50%" align="center">
      <img src="figures/Screenshot2.png" alt="WDCable chat page">
      <br><strong>Chat</strong>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="figures/Screenshot3.png" alt="WDCable speed test page">
      <br><strong>Speed Test</strong>
    </td>
    <td width="50%" align="center">
      <img src="figures/Screenshot4.png" alt="WDCable file transfer page">
      <br><strong>File Transfer</strong>
    </td>
  </tr>
</table>

### Audio Link

<table>
  <tr>
    <td width="68%" align="center" valign="top">
      <img src="figures/audio.png" alt="WDCable streaming Windows system audio">
      <br><strong>Windows system-audio streaming</strong>
    </td>
    <td width="32%" align="center" valign="top">
      <img src="figures/android_side_streaming.png" width="260" alt="WDCable Audio Link streaming on Android">
      <br><strong>Android Audio Link</strong>
    </td>
  </tr>
</table>

## 🔗 How It Works

WDCable uses Wi-Fi Direct to establish a peer-to-peer connection between supported devices. Protocol v2 assigns the WDCable TCP listener to the Wi-Fi Direct client and uses UDP rendezvous when necessary to discover the peer endpoint safely. Data travels directly between the devices instead of passing through a router or the internet.

<p align="center">
  <img src="figures/demonstration.png" width="760" alt="Diagram showing a direct Wi-Fi connection between two devices without a router">
</p>

## 💻 Requirements

- Windows 10 version 1809 (build 17763) or later, or Windows 11.
- An x64 or ARM64 Windows device.
- A Wi-Fi network adapter and driver that support Wi-Fi Direct.
- A second compatible Windows or Android device for peer-to-peer communication.

## 📱 Android Version

Use [WDCable for Android](https://github.com/jingcjie/WDCable_flutter) to connect an Android device and transfer files, exchange messages, test connection speed, or use Audio Link across platforms.

## 🚀 Getting Started

Install WDCable from the Microsoft Store using the button above, or build it from source.

### Install with winget

```powershell
winget install --id 9MZQMRHFFJJW --source msstore
```

### Building from Source

1. Clone this repository:

   ```sh
   git clone https://github.com/jingcjie/WDCableWUI.git
   ```

2. Open `WDCableWUI.sln` in Visual Studio.
3. Select the `x64` or `ARM64` platform.
4. Build and run the project.

## 🛠️ Built With

- [.NET 8](https://dotnet.microsoft.com/)
- [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/)
- [Opus](https://opus-codec.org/) audio encoding

## 📄 License

WDCable is licensed under the terms in the [LICENSE](LICENSE) file.
