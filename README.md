# PunchPlay Jellyfin Plugin

Scrobble Jellyfin playback to PunchPlay with per-user account linking and device-code authentication.

## Features

- Per-user PunchPlay account linking for shared Jellyfin servers
- Device-code login with user code and QR flow
- Movie and TV episode scrobbling
- Playback start, progress, pause, resume, and stop events
- Watching-now and continue-watching state aligned with PunchPlay's Kodi playback contract
- TMDB-first matching with IMDb/TVDB/title fallbacks
- Local retry queue for transient PunchPlay API failures with automatic background replay

## Compatibility

- Jellyfin `10.10.x`
- Plugin target framework: `.NET 8`
- Plugin SDK dependency: `Jellyfin.Controller 10.10.7`

`10.11.x` should be validated before publishing a release for that ABI.

## Install

1. Open Jellyfin Dashboard.
2. Go to `Plugins -> Repositories`.
3. Add the PunchPlay repository manifest URL.
4. Go to `Plugins -> Catalog`.
5. Install `PunchPlay`.
6. Restart Jellyfin when prompted.

## Connect an account

1. Open `Dashboard -> Plugins -> My Plugins -> PunchPlay`.
2. If you are an admin, select the Jellyfin user to manage.
3. Click `Connect to PunchPlay`.
4. Visit `punchplay.tv/link`.
5. Enter the displayed device code or scan the QR code.
6. Return to Jellyfin and confirm the plugin shows `Connected as ...`.

## Verify scrobbling

1. Start playback of a movie or episode in Jellyfin.
2. Confirm the PunchPlay plugin shows a recent successful scrobble in the admin diagnostics section.
3. Check the linked PunchPlay account for now-playing or history updates.

## Troubleshooting

- `Could not reach PunchPlay`: verify the `PunchPlay URL` in plugin settings.
- No scrobbles for a user: confirm that Jellyfin user is linked to a PunchPlay account.
- Items are skipped: ensure the library has TMDB, IMDb, or TVDB metadata where possible.
- Queue is growing: transient API and network failures are queued automatically, retried in the background, and can also be forced with `Retry Queue Now` in plugin diagnostics.
- Unexpected disconnects: a `401` response from PunchPlay clears the stored token, removes that user's queued scrobbles, and requires re-linking.
- Stop below the watched threshold should save progress but not mark an item watched.
- Stop at or above the watched threshold should mark the item watched.

## Development

- `dotnet build Jellyfin.Plugin.PunchPlay/Jellyfin.Plugin.PunchPlay.csproj`
- `dotnet test Jellyfin.Plugin.PunchPlay.Tests/Jellyfin.Plugin.PunchPlay.Tests.csproj`
- GitHub Actions runs restore, build, and tests on every push and pull request.

## Release checklist

- Build and publish the plugin zip artifact into `release-assets/`
- Update `manifest.json` with stable public releases only; do not list internal test, debug, or superseded hotfix builds
- Verify release checksum matches the published zip
- Keep the plugin version consistent across:
  - `Jellyfin.Plugin.PunchPlay.csproj`
  - `manifest.json`
  - release tag and zip name
  - payload `client_version`
