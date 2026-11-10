# AGENTS.md — IRCameraView

Guidance for AI agents and contributors working on this repository.

## What this project is

A Windows camera viewer focused on **infrared (IR) cameras** (for example the
Windows Hello IR camera). It also previews colour cameras and exposes IR torch /
photo controls when the hardware supports them.

Two front-ends share one implementation:

| Project | UI stack | Target |
| --- | --- | --- |
| `IRCameraView.csproj` | WinUI 3 / Windows App SDK | Windows 10 1809+ |
| `IRCameraViewUWP/IRCameraViewUWP.csproj` | UWP | Windows 10 1809+ |
| `IRCameraViewShared/` | shared C# + XAML (`#if NETFX_CORE` for UWP) | both |

The shared project is imported with `IRCameraViewShared.projitems`; never
duplicate camera logic into one front-end only.

## Prerequisites

- Windows 10 1809 (build 17763) or newer, x64 or ARM64.
- **.NET SDK 10.0** (`dotnet --list-sdks` must show `10.x`).
- Windows App SDK runtime 1.8 (installed automatically for packaged apps).
- For packaged (MSIX) builds, a code-signing certificate you control. The repo
  intentionally does **not** contain a certificate or a certificate thumbprint.

## Build and run

Debug builds are unsigned and unpackaged so `dotnet build` works on a fresh
machine (no certificate required):

```powershell
dotnet restore IRCameraView.csproj
dotnet build IRCameraView.csproj -c Debug -p:Platform=x64
dotnet run --project IRCameraView.csproj -c Debug -p:Platform=x64 `
  -p:AppxPackageSigningEnabled=false -p:WindowsPackageType=None
```

Package (MSIX) build — replace the thumbprint with your own development
certificate:

```powershell
dotnet publish IRCameraView.csproj -c Release -p:Platform=x64 `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<YOUR_THUMBPRINT>
```

The UWP front-end consumes the same shared sources. Note that the UWP project
does **not** currently build with the plain .NET 10 SDK: its XAML compiler does
not generate the fields for the shared `CameraPage.xaml` (34 pre-existing
`CS0103` errors on the untouched HEAD). It needs the UWP/WinUI XAML tooling from
Visual Studio. Do not attribute those errors to a shared-code change; verify
against a clean worktree if unsure.

## Camera / hardware testing

Do not assume "no camera" means the code is wrong. Check the machine first:

```powershell
Get-PnpDevice -Class Camera | Select-Object Status, FriendlyName
Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam'
```

A working Windows Hello module commonly appears as two PnP devices:

- `USB2.0 FHD UVC WebCam` → colour source
- `USB2.0 IR UVC WebCam` → IR source (often 640×400 `L8` @ 15 fps)

`MediaFrameSourceGroup.FindAllAsync()` may also return a combined group
(`Rts-DMFT-Group`) that contains **both** a `Color` and an `Infrared` source.
This is the single most important detail in this codebase:

> **Always select a `MediaFrameSource` by `SourceInfo.SourceKind`.**
> `capture.FrameSources.First()` returns the colour source on combined groups and
> is the original cause of "Shows normal Camera instead of IR" (issue #8).

## Architecture notes

- `CameraController` owns `MediaCapture`, the `MediaFrameReader`, device
  enumeration, frame conversion and photo saving.
- `CameraPage` owns UI state: device picker, camera-kind picker, frame delivery
  to `SoftwareBitmapSource`, torch/photo mode controls.
- `SoftwareBitmap` ownership is explicit: the frame thread creates a bitmap,
  hands ownership to the subscriber (`OnFrameReady`), and the UI disposes it
  after `SoftwareBitmapSource.SetBitmapAsync`. Never dispose a bitmap while the
  UI may still be reading it. The controller keeps its own copy only for photo
  capture.
- The frame callback must never block (`.Wait()` / `.Result`) on the UI thread.
  UI updates are queued and coalesced; stale frames are dropped.
- `MediaCapture` uses exclusive mode first and falls back to shared read-only
  when another app (Windows Hello, Camera, Teams) holds the device.
- IR frames are `L8`/`Gray8` and are converted to `Bgra8` for display. Green
  mapping is an optional post-process.
- Windows Hello IR cameras commonly alternate illuminated and unilluminated
  frames (`InfraredMediaFrame.IsIlluminated` flips every frame). The UI defaults
  the frame filter to **Illuminated** for IR so the preview does not flicker or
  look black on half the frames. The controller still has a fallback that
  delivers all frames if the selected state never occurs.

## Build/packaging gotchas

- `Package.appxmanifest` (WinUI) **must** declare `webcam`,
  `picturesLibrary`, and `videosLibrary`. Without `webcam` a packaged app gets
  access-denied from `MediaCapture`; without the libraries photo/video saves
  fail or produce 0-byte files.
- Never commit a personal `PackageCertificateThumbprint`, a `.pfx`, or absolute
  paths from your machine (the old `C:\Users\Lasse\...` item groups broke other
  contributors' builds).
- Windows App Runtime and .NET runtime must match the TFM/package versions; a
  packaged app will show "install Windows App Runtime" if the required 1.8
  runtime is missing.

## What to verify before opening a PR

1. `dotnet build IRCameraView.csproj -c Debug -p:Platform=x64` succeeds with
   **0 errors** on a machine without the author's certificate.
2. The app launches and the device list shows both the colour and IR cameras.
3. Selecting **Infrared** → **Rts-DMFT-Group** shows a grayscale IR image, not
   the colour image.
4. Selecting the dedicated `USB2.0 IR UVC WebCam` also shows IR.
5. Switching to **Color** shows colour; switching back to **Infrared** recovers.
6. Photo capture writes a non-zero JPEG to `Pictures\IRCameraView`.
7. No unhandled exception when no camera exists, when the camera is busy, or
   when `InfraredTorchControl` is unsupported (it throws
   `COMException: The request is not supported` on many devices).
8. Run for several minutes and confirm memory stays flat (no `SoftwareBitmap`
   leak).
