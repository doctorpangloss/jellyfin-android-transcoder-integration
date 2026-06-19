# Jellyfin Android Transcoder Integration

Integration harness for the Jellyfin Android Transcoder project.

This repository intentionally keeps build configuration in the component repos:

- `third_party/ffmpeg-android`: FFmpeg fork with Android MediaCodec zero-copy work.
- `third_party/jellyfin-android-transcoder`: Android App Bundle, Jellyfin plugin, and FFmpeg shim.

Run:

```bash
git submodule update --init --recursive
dotnet test JellyfinAndroidTranscoderIntegration.sln
```

The .NET tests use Testcontainers to start a real Jellyfin container and verify
its public API is reachable. They also include a mock Android transcoder server
that accepts the same HTTP request shape the Jellyfin shim sends to the APK.

For Android end-to-end testing, build the AAB in
`third_party/jellyfin-android-transcoder`, generate installable APK sets with
`bundletool`, then install them on either a physical phone or an x86_64 Android
Emulator instance. The emulator is the right Linux/Windows automation target;
an Android application cannot be started as a normal Linux process.
