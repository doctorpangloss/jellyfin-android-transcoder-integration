# Jellyfin Android Transcoder Integration

Integration harness for the Android MediaCodec transcode bridge for Jellyfin.

Related repositories:

- Android worker + Jellyfin plugin: https://github.com/doctorpangloss/jellyfin-android-transcoder
- Patched FFmpeg fork: https://github.com/doctorpangloss/forks-ffmpeg-android
- Integration tests: https://github.com/doctorpangloss/jellyfin-android-transcoder-integration

## What This Tests

The integration suite validates the deployed shape rather than only unit-level code:

- Builds the Jellyfin plugin and `jfat-ffmpeg` shim from the component submodule.
- Builds the Android app bundle and installs it on an Android emulator.
- Starts Jellyfin `10.11.6` with Testcontainers.
- Installs the plugin into Jellyfin's `/config/plugins` volume.
- Writes the plugin configuration into Jellyfin's `/config/plugins/configurations` volume.
- Adds a large HEVC media fixture to the Jellyfin library.
- Uses Chromium/Playwright to request playback through Jellyfin.
- Verifies Jellyfin invokes the shim and the shim routes work to Android.
- Verifies browser-visible HLS media starts within 10 seconds.
- Verifies Android does not consume/upload the full 1 GiB source before first media output.

The large fixture is intentionally sparse: it is a valid short HEVC MP4 extended to 1 GiB. That proves startup streaming behavior without spending minutes generating or copying a full 1 GiB encoded movie.

## Prerequisites

- Docker with Testcontainers support.
- .NET SDK 9.
- JDK 21.
- Android SDK with an API 35 x86_64 emulator image.
- `bundletool` at `/home/administrator/Documents/tools/bundletool/bundletool-all-1.18.3.jar`, or update `Bundletool()` in the test.

## Run

```bash
git submodule update --init --recursive
dotnet test JellyfinAndroidTranscoderIntegration.sln --nologo
```

Focused browser/emulator test:

```bash
dotnet test tests/JellyfinAndroidTranscoder.IntegrationTests/JellyfinAndroidTranscoder.IntegrationTests.csproj \
  --filter FullyQualifiedName~JellyfinBrowserEmulatorTests \
  --nologo
```

If the emulator is wedged, restart it:

```bash
$ANDROID_HOME/platform-tools/adb -s emulator-5554 emu kill || true
```

The test will recreate/start the `jfat_api35` AVD if needed.

## Deployment Relevance

This repo is not installed on production systems. It exists to prove that the release artifacts in `doctorpangloss/jellyfin-android-transcoder` work together with Jellyfin and Android as deployed components.

For actual deployment, use the `v1.0.0` release assets from:

```text
https://github.com/doctorpangloss/jellyfin-android-transcoder/releases/tag/v1.0.0
```

Install:

- `jellyfin-android-transcoder-1.0.0.apk` or `jellyfin-android-transcoder-1.0.0.aab` on the Android phone.
- `Jellyfin.Plugin.AndroidTranscoder-1.0.0.zip` on the Jellyfin server.

The component repository README contains the Docker Compose and ADB/sideload deployment instructions.
