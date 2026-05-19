# Building Fly Photos from Source

This document describes how to build Fly Photos locally. The automated CI workflow in
`.github/workflows/build.yml` mirrors these steps exactly and can be used as a reference.

---

## Prerequisites

Install the following tools before building. All items are **required** unless noted.

| Tool | Version | Notes |
|------|---------|-------|
| Windows 10/11 | 22H2 or later | Build targets Windows SDK 10.0.22621 |
| Visual Studio 2022 | 17.x | Install the **Desktop development with C++** workload |
| .NET SDK | 10.0 (preview) | Install via `winget install Microsoft.DotNet.SDK.10` or from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) |
| vcpkg | Latest | Clone from [github.com/microsoft/vcpkg](https://github.com/microsoft/vcpkg), bootstrap (`bootstrap-vcpkg.bat`), and add to `PATH` |
| MSBuild | Included with VS 2022 | Add the VS Developer Command Prompt to `PATH`, or use the *Developer PowerShell for VS 2022* |
| Rust toolchain | Stable (1.78+) | Optional — required only when modifying `fly_rust_bridge`; pre-built `.dll` is checked in |

---

## Repository Layout

```
Src/
├── build.ps1                  ← Local build script (mirrors CI)
├── build_libheif/             ← vcpkg manifest for libheif/AVIF dependencies
├── FlyNativeLib/              ← C++ DLL: Explorer integration, WIC codec discovery
├── FlyNativeLibHeif/          ← C++ DLL: HEIC/HEIF/AVIF decoding (links libheif)
├── FlyContextMenuHelper/      ← C++ helper EXE: native Shell context menu
├── fly_rust_bridge/           ← Rust cdylib: RAW (rawler) and SVG (resvg) decoding
└── FlyPhotos/                 ← .NET 10 WinUI 3 application (AOT-compiled)
```

---

## Quick Start (PowerShell)

Open a **Developer PowerShell for VS 2022** and run the build script:

```powershell
cd Src
.\build.ps1 -TargetPlatform x64      # build x64 only
.\build.ps1 -TargetPlatform ARM64    # build ARM64 only
.\build.ps1                          # build both (default)
```

The script performs all steps described below and places the finished binaries in
`Src\publish\win-x64\` and/or `Src\publish\win-arm64\`.

---

## Manual Build Steps

### 1 — Restore vcpkg Dependencies

The `FlyNativeLibHeif` project requires **libheif**, **libde265**, **dav1d**, **x265**,
and **libpng**, managed via vcpkg in manifest mode.

```powershell
cd Src\build_libheif
vcpkg install --triplet x64-windows --x-manifest-root=.
# For ARM64:
vcpkg install --triplet arm64-windows --x-manifest-root=.
```

The `vcpkg-configuration.json` file pins the baseline commit used for all packages.

### 2 — Build Native C++ Projects

Build the three native projects with MSBuild (Release configuration):

```powershell
# From Src\FlyNativeLib\
msbuild FlyNativeLib.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143

# From Src\FlyNativeLibHeif\
msbuild FlyNativeLibHeif.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143

# From Src\FlyContextMenuHelper\
msbuild FlyContextMenuHelper.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143
```

Replace `x64` with `ARM64` for an ARM64 build.

### 3 — Copy Native Binaries

The .NET project expects the native binaries in `Src\FlyPhotos\External\<Platform>\`.

```powershell
$platform = "x64"   # or "ARM64"
$triplet  = "x64-windows"   # or "arm64-windows"

$externalDir = "Src\FlyPhotos\External\$platform"
New-Item -ItemType Directory -Path $externalDir -Force | Out-Null

Copy-Item "Src\FlyNativeLib\$platform\Release\FlyNativeLib.dll" -Destination $externalDir -Force
Copy-Item "Src\FlyNativeLibHeif\$platform\Release\FlyNativeLibHeif.dll" -Destination $externalDir -Force
Copy-Item "Src\FlyContextMenuHelper\$platform\Release\FlyContextMenuHelper.exe" -Destination $externalDir -Force

# Copy vcpkg-built DLLs (libheif, dav1d, x265, libde265, libpng, …)
Get-ChildItem "Src\build_libheif\vcpkg_installed\$triplet\bin\*.dll" |
    Copy-Item -Destination $externalDir -Force
```

> **Note:** `fly_rust_bridge.dll` (RAW + SVG decoder) is pre-built and already present in
> `Src\FlyPhotos\External\<Platform>\`. Rebuild it only when modifying the Rust source:
> ```powershell
> cd Src\fly_rust_bridge
> cargo build --release
> # Output: target\release\fly_rust_bridge.dll
> ```

### 4 — Publish the .NET Application

```powershell
cd Src
dotnet publish FlyPhotos\FlyPhotos.csproj `
    -c Release `
    -r win-x64 `
    /p:Platform=x64 `
    -o publish\win-x64
```

For ARM64 replace `win-x64` / `x64` with `win-arm64` / `ARM64`.

The publish output in `Src\publish\win-<rid>\` is a self-contained, AOT-compiled
executable. No .NET runtime is required on the target machine.

---

## Building in Visual Studio

1. Open `Src\FlyPhotos.slnx` in Visual Studio 2022.
2. Complete steps 1–3 above (vcpkg restore + native build + copy) so that
   `Src\FlyPhotos\External\<Platform>\` is populated.
3. Select **Release | x64** (or ARM64) and press **Build Solution** (`Ctrl+Shift+B`).
4. To run the app from VS, press **F5** or use **Debug › Start Without Debugging**.

> Visual Studio does not auto-restore vcpkg dependencies or build the native projects.
> Always complete steps 1–3 before building inside the IDE.

---

## CI Workflow

The GitHub Actions workflow (`.github/workflows/build.yml`) is triggered manually
(`workflow_dispatch`) and builds both `x64` and `ARM64` in parallel. It:

1. Checks out the repository.
2. Installs .NET 10 preview and MSBuild.
3. Restores vcpkg packages (with a cache keyed on `vcpkg.json` + `vcpkg-configuration.json`).
4. Builds the native C++ projects.
5. Copies native binaries to `FlyPhotos\External\<Platform>\`.
6. Runs `dotnet publish` for the .NET application.
7. Uploads the output as a GitHub Actions artifact.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `vcpkg: command not found` | vcpkg not on PATH | Add vcpkg directory to `PATH` or run from vcpkg root |
| MSBuild error: toolset v143 not found | VS 2022 C++ workload not installed | Run VS Installer and add **Desktop development with C++** |
| `dotnet: command not found` | .NET 10 SDK not installed | Install .NET 10 preview SDK |
| Missing `FlyNativeLib.dll` at publish | Step 3 skipped | Copy native binaries before dotnet publish |
| `fly_rust_bridge.dll` missing | Pre-built DLL deleted | Run `cargo build --release` in `Src\fly_rust_bridge\` |
