# ADR 0008: SkiaSharp replaces SixLabors.ImageSharp as the image library

## Status
Accepted (release 2.5.1, branch `feat/image-library`)

## Context

The product decodes every picture a user uploads (and every picture the AI chat attaches or an agent asks for with `bee_get_image`),
re-encodes it as JPEG and scales it. That was `SixLabors.ImageSharp` 3.1.12, behind `IImageTranscoder` (`libs/BeeMemoryBank.Media`).

ImageSharp 3.1.12 has published advisories whose fix exists only in 4.x: GHSA-jjfr-hcj7-qf5w, GHSA-j9gm-c75j-xc9q and GHSA-j3p4-wp97-rph4
(high) and two moderate ones. ImageSharp 4.x needs a paid Six Labors licence for every non-Debug build (an earlier agent verified that a Release
build fails without it, `reports/week-fix-fixsrv.md`). The owner decided to replace the library with a free, small, reputable one.

What the product needs from the library, and what the tests pin (`SkiaImageTranscoderTests`, `MediaServiceImageTests`):

* decode JPEG, PNG, WebP, BMP and (static) GIF; an animated GIF and an SVG never reach it (`MediaService` stores them untouched);
* resize with good quality; encode JPEG; **honour the EXIF orientation** (ImageSharp kept the EXIF block, so a viewer rotated the photo; a
  library whose JPEG carries no EXIF must bake the rotation into the pixels);
* refuse a corrupt, truncated or hostile file cleanly, never crash and never allocate for a picture that only *claims* to be huge;
* run in the Docker base image (`mcr.microsoft.com/dotnet/aspnet:10.0`, amd64 and arm64) without extra system packages, ship in the Windows
  and macOS arm64 packages, and survive the macOS signing of embedded native libraries.

## Decision

**SkiaSharp 3.119.4** (MIT; `SkiaSharp`, plus `SkiaSharp.NativeAssets.Linux.NoDependencies` for the Api and the Linux tests; the Windows and macOS native
assets come with the main package).

### Evidence (all fetched 2026-10-08)

Sources: NuGet registration API (`api.nuget.org/v3/registration5-gz-semver2`, `v3-flatcontainer`), GitHub Advisory Database
(`api.github.com/advisories?ecosystem=nuget&affects=<package>`), NVD (`services.nvd.nist.gov/rest/json/cves/2.0`, keyword and CPE queries), GitHub
repository metadata (`api.github.com/repos/...`), and the packages themselves (downloaded and listed). The raw outputs are in the task's working
folder (`ev-img`), the scripts in `fiximg-scripts`.

| | SkiaSharp 3.119.4 | Magick.NET-Q8-AnyCPU 14.17.2 | StbImageSharp 2.30.16 + StbImageWriteSharp 1.16.7 + StbImageResizeSharp 0.97.1 | NetVips 3.2.0 + NetVips.Native 8.18.0 | OpenCvSharp4 4.13 |
|---|---|---|---|---|---|
| (a) licence | MIT (Skia: BSD-3; native parts BSD/MIT/zlib/libpng/FreeType) | Apache-2.0 | Unlicense OR MIT (writer and resizer: no licence field on NuGet) | NetVips MIT, **libvips LGPL-3.0-or-later** | Apache-2.0 |
| (b) advisories | GHSA: one, GHSA-j7hp-h8jx-5ppr (libwebp, fixed in 2.88.6; we are on 3.119.4). NVD keyword "SkiaSharp": 0. See "Native components" below | GHSA: **166** (27 high, 154 of them published in the last 12 months; the last ones fixed in 14.15.0); a steady stream, every month | none listed | none listed for the packages (libvips has its own) | none listed |
| (c) maintained | repo pushed 2026-10-08; 3.119.4 released 2026-05-25, 4.153.1 on 2026-09-30 | repo pushed 2026-10-05; 14.17.2 on 2026-09-27 | reader 2026-08-08, writer 2022-06 (package; repo 2026-05), **resizer 2021-02** | repo 2026-09-28, 3.2.0 on 2026-01-01 | repo 2026-10-05 |
| (d) size, native part per runtime id | linux-x64 11.2 MB, linux-arm64 10.8 MB, win-x64 11.6 MB, osx 15.2 MB (universal, x64 + arm64); managed 0.49 MB. ImageSharp.dll was 2.1 MB and had no native part | 24.7 (win-x64) to 38.5 MB (linux-x64), 34.5 (linux-arm64), 29.5 (osx-arm64); the NuGet package is 102 MB | managed only, 0.18 MB together | 7.4 to 8.3 MB compressed per runtime id, separate package each | n/a (see (f)) |
| (e) formats | JPEG, PNG, GIF, WebP, BMP (+ other codecs, e.g. ICO, WBMP and RAW/DNG, that the transcoder does not let through); EXIF origin read from JPEG; mipmapped linear resize; JPEG encoder; failures are result codes, not exceptions | all of them, too many | JPEG, PNG, BMP, TGA, PSD, GIF, HDR: **no WebP**; stb does not read EXIF, so no orientation | all | most |
| (f) Docker base image, signing | `NoDependencies` Linux build has no libfontconfig need; the macOS dylib is one file, already shipped by Avalonia (see below) | native `.so` per runtime id, large | n/a | LGPL-3.0 native per runtime id | **no stable NuGet runtime package for osx-arm64** (0 stable versions of `OpenCvSharp4.runtime.osx_arm64`) |
| verdict | chosen | rejected: advisory rate and size | rejected: no WebP (a promised upload type), no EXIF, unmaintained resizer, home-made glue for decoding and scaling | rejected on (a) | rejected on (f) |

Also considered and rejected: staying on ImageSharp 3.1.12 (the three high advisories stay open); ImageSharp 4.1.2 (licence); ImageSharp 2.x
(the same advisories cover ">= 2.0.0, <= 4.1.1" and the fix is 4.1.2 only); `System.Drawing` / WIC (Windows only).

Why SkiaSharp wins on the owner's ordering: free for everyone (MIT); no advisory against the package; actively maintained by Microsoft's .NET
team and the Mono project; **already inside the product**: Avalonia.Skia 12.1.x pins SkiaSharp 3.119.4 (the Windows and macOS desktop apps carry
`SkiaSharp.dll` and `libSkiaSharp` today) and the Android app carries SkiaSharp through `Indiko.Maui.Controls.Markdown`
(`SkiaSharp.Views.Maui.Controls` 3.119.1, `Svg.Skia` 3.4.0; the transcoder lifts its SkiaSharp and Android native assets from 3.119.1 to 3.119.4, same line).
One image/graphics library across the product, one set of native files to watch, one Mac signing path that already works for `libSkiaSharp.dylib`.

### Version: 3.119.4, not 4.153.1

SkiaSharp 4.153.1 (2026-09-30) exists and bundles newer natives (Skia m153, zlib 1.3.2, libjpeg-turbo 3.1.4, FreeType 2.14.3, HarfBuzz 14.2.1,
RAW/DNG decoding disabled). It was not chosen because the rest of the product is on 3.119.x and cannot move with it yet: `Avalonia.Skia` 12.1.3
(the latest) still pins SkiaSharp 3.119.4, and `Indiko.Maui.Controls.Markdown` 1.5.0 pins `SkiaSharp.Views.Maui.Controls` 3.119.1 and `Svg.Skia`
3.4.0. `BeeMemoryBank.Media` is shared by the Api and the Android app; with 4.x in it, NuGet would lift the Android app's SkiaSharp to 4.x under
those 3.x binaries, which can only be tried on a device. Moving the whole Skia stack to 4.x is a separate, testable change (follow-up below).

### Native components of 3.119.4 and the NVD

The 3.119.4 native library bundles (from the component manifest `cgmanifest.json` of the tag): libpng 1.6.58, zlib 1.3.0.1, libjpeg-turbo 2.1.5.1,
libwebp 1.6.0, FreeType 2.13.3, HarfBuzz 8.3.1, libexpat 2.7.5, Brotli 1.2.0, Wuffs 0.3.3, Skia chrome/m119. NVD CPE queries for those versions:
libpng, libjpeg-turbo, libwebp, Brotli, Skia: no entry. Entries exist for zlib 1.3.0.1 (CVE-2026-22184, a flaw in the `untgz` demo utility; CVE-2026-27171,
CPU use in `crc32_combine64`; CVE-2023-45853, minizip), FreeType 2.13.3 (CVE-2026-23865, OpenType variable fonts), HarfBuzz 8.3.1 (CVE-2026-22693, text
shaping) and libexpat 2.7.5 (17 entries, XML parsing; the 4.153.1 bundle's expat 2.8.1 still has 15). **None of those code paths is reachable from
the transcoder**: it hands the bytes only to the JPEG, PNG, WebP, BMP and GIF decoders (after a signature check), never parses XML, never loads a font,
never combines CRCs, never opens an archive. The risk of the shared native library is therefore the image decoders themselves (libjpeg-turbo, libpng +
zlib inflate, libwebp, Skia's BMP codec, Wuffs for GIF), which have no NVD entry for the bundled versions.

## Implementation

* `libs/BeeMemoryBank.Media/SkiaImageTranscoder.cs` implements `IImageTranscoder` (the interface keeps `ConvertToJpeg` and `DownscaleJpeg`, and gains
  `ShrinkJpegToFit`, which is the size ladder `bee_get_image` used to carry inline). Chat attachments (`BuildVisionDataUrl`) and `bee_get_image` go through
  the same seam; no other project references the library. Constants: JPEG quality 90 (convert) and 85 (downscale), as before.
* **Decompression-bomb guard**: a file over 64 MB is refused; the signature must be JPEG, PNG, GIF, WebP or BMP (so the library's RAW/DNG, ICO and
  other decoders never see uploaded bytes); the codec is created from the header, the pixel count is checked (limit 100 million pixels, a 100 MP camera frame; the
  decode needs 4 bytes per pixel and the rotation copy another 4) **before** any pixel is decoded or allocated; a decode that is not a complete success
  (truncated, damaged) is refused. All refusals are `ArgumentException`, which the upload endpoint answers with 400.
* Behaviour changes against ImageSharp, all deliberate: the EXIF orientation is applied to the pixels (the written JPEG has no EXIF block); transparency is
  composited over white (ImageSharp wrote the colour of the transparent pixels, usually black); TIFF and other formats ImageSharp could decode but the
  product never listed (`AllowedContentTypes` is PNG, JPEG, GIF, WebP, SVG) are refused; a file the transcoder cannot read is a 400, not a 500.
* The boundary tests keep the blind node and the blind apps free of any image library (`SkiaSharp` joined `SixLabors.ImageSharp` in the refusal lists,
  and in the Android blind APK check).

## Packaging

* Docker (`Dockerfile`): the existing RID-specific publish copies `libSkiaSharp.so` of the target runtime id into the Api folder (checked for linux-x64 and
  linux-arm64); the NoDependencies build links only libc, libm, libpthread and libdl, so the aspnet image needs no package. The Linux native package is referenced by
  the Api and by the test projects that run on Linux, not by `BeeMemoryBank.Media`: through Media it would also reach the Android app, whose RID graph includes
  linux-bionic, and hand it a second `libSkiaSharp.so` next to its own.
* Windows: `publish-node.ps1` publishes the Api for win-x64: `libSkiaSharp.dll` next to it. The Windows native package also ships `libSkiaSharp.pdb`
  (84 MB); the Api publish leaves it out (`BmbLeaveNativeSymbolsOutOfPublish` in `BeeMemoryBank.Api.csproj`). The desktop app's own publish (Avalonia) already
  carried that file before this change.
* macOS: `libSkiaSharp.dylib` is universal (x86_64 + arm64; the arm64 slice needs macOS 11.0, below the package's floor) and lands in `api/` as well as in the
  app root. The signing loops of `pack-macos-full.sh` sign every Mach-O file of the bundle from a manifest made with `file`, so the new library is
  signed with the others; the self-check of the script and the layout test now require `api/libSkiaSharp.dylib`. Notarization could not be run on this PC.

## Consequences

* The server grows by the native library: +9.6 MB (linux-x64), +9.2 MB (linux-arm64), +10.0 MB (win-x64, pdb left out), +13.6 MB (osx-arm64 publish) against the ImageSharp
  publish (measured by publishing the Api for each runtime id on the old and the new tree). The Android and desktop apps carry no new native file.
* One more native library in the Api process; a crash inside it would take the Api down (ImageSharp was managed code). The guard above, the signature
  allow-list and the unreachable-component analysis are the mitigation; the Api already runs a native SQLite and ONNX runtime.
* Licence notices: `THIRD-PARTY-NOTICES.txt` carries the SkiaSharp MIT text and the list of the native components with their licences; the Docker image
  copies it to `/app`.

## Follow-ups

* Move SkiaSharp to 4.x together with Avalonia, `SkiaSharp.Views.Maui` and `Svg.Skia` when they publish versions that agree, and run the Android app on a device
  (its camera-roll upload path uses this transcoder).
* If the owner wants the complete notice texts of every bundled library inside the packages, `THIRD-PARTY-NOTICES.txt` of the SkiaSharp.NativeAssets packages can be
  copied in; it was not vendored here because the upstream text contains third-party e-mail addresses.
