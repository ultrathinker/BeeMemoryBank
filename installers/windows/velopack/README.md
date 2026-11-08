# Velopack packaging for Windows

This directory holds the configuration/assets for the Velopack Windows installer.

## What lives here

| File | Purpose |
|------|---------|
| `README.md` | This file |

### Icon

The app icon (`icon.ico`) used by `vpk pack --icon` is **copied at build time** by
`scripts/pack-windows.ps1` from `desktop/BeeMemoryBank.Desktop/Assets/icon.ico` (the app's own
multi-size icon, whose 16 to 48 px entries are drawn for those sizes).  The copy is placed in
`installers/windows/velopack/icon.ico` and is **excluded from source control**
(see `.gitignore`).

### Releases output

`vpk pack` writes the release artefacts (Setup.exe, RELEASES feed, nupkg files) to
`installers/windows/velopack/releases/`.  That directory is excluded from source
control as well.

## Key `vpk pack` flags used

```
vpk pack \
  --packId   BeeMemoryBank \
  --packVersion <VERSION>          # read from repo-root VERSION file \
  --packTitle "Bee Memory Bank" \
  --packAuthors "BeeMemoryBank Contributors" \
  --packDir  <publish/win-x64/payload>  # Desktop.exe at root + bmbd/api/web/cli subfolders \
  --mainExe  BeeMemoryBank.Desktop.exe \
  --icon     installers/windows/velopack/icon.ico \
  --outputDir installers/windows/velopack/releases \
  --skipVeloAppCheck               # app does not yet call VelopackApp.Build().Run()
```

## Code signing

Code signing is **explicitly out of scope**.  No `--signParams` / `--azureTrustedSignFile`
flags are passed; the produced `Setup.exe` is unsigned.

## Velopack CLI version

Tested against **vpk 1.2.0** installed via `dotnet tool install -g vpk`.
