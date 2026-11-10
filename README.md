# IRCameraView

Get access to your infrared and depth cameras on Windows!

## Download

Download the [latest stable release](https://github.com/Iemand005/IRCameraView/releases/latest).

There are two versions, one built with WinUI and one built with UWP. The WinUI version has Windows 11 design and the UWP version Windows 10.

### Here's the WinUI version:

![image](./docs/IRCameraViewWinUI.png)

### And the UWP version:

![image](./docs/IRCameraViewUWP.png)

Both compile from the same source code. The app can now be used as a normal camera app too. Photo capture fully works, images are save to your Pictures library folder. Video capture only works for colour camera's at the moment. Feel free to suggest changes or features. Do report bugs, you may create an issue on GitHub to reach out about it.

For reference, the only other example I found on how to do this is: https://github.com/andresbeltranc/ir_camera_sample_win32

## Build from source

Requirements:

- .NET SDK 10.0
- Windows 10 version 1809 (build 17763) or newer, or Windows 11
- Windows App Runtime 1.8 (the packaged app installs/uses it automatically)

### Debug build (no certificate needed)

```powershell
dotnet restore IRCameraView.csproj
dotnet build IRCameraView.csproj -c Debug -p:Platform=x64
dotnet run --project IRCameraView.csproj -c Debug -p:Platform=x64 `
  -p:WindowsPackageType=None
```

The repository intentionally does not contain a signing certificate. To produce
a signed MSIX, use your own certificate:

```powershell
dotnet publish IRCameraView.csproj -c Release -p:Platform=x64 `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<YOUR_THUMBPRINT>
```

### Troubleshooting

- **"A compatible Windows App Runtime is required"** — install the latest
  Windows App Runtime 1.8 (x64 or ARM64) from Microsoft, then launch the app
  again. A packaged MSIX and an unpackaged `dotnet run` need the same runtime.
- **No cameras are listed** — open *Settings > Privacy & security > Camera* and
  make sure camera access and "Let desktop apps access your camera" are enabled.
  Close Windows Hello, Camera, Teams, or any other app that may be holding the
  camera; the viewer falls back to shared read-only mode when possible.
- **The IR preview looks black every other frame** — many Windows Hello IR
  cameras alternate an illuminated and a dark frame. The app now defaults the
  frame filter to *Illuminated* for IR. Select *None* if you want every frame.
- **The app shows the colour camera when Infrared is selected** — this was a
  source-selection bug on camera groups that expose both a colour and an IR
  source (for example `Rts-DMFT-Group`). Select the camera again after updating;
  the source is now chosen by `MediaFrameSourceKind`.

### UWP front-end

The shared sources are also used by `IRCameraViewUWP`. The UWP build currently
requires the UWP/WinUI XAML tooling from Visual Studio; building it with the
plain .NET 10 SDK on a machine without that workload is not supported yet.

