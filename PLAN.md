# Jellyfin "Hover Preview" plugin (Netflix-style card pop-out)

## Context

Build a Jellyfin plugin that reproduces Netflix's browse-row hover card: hold the mouse over any card for 500 ms and a larger card pops out, shows the still, then plays a ~1 min muted clip with the title logo pinned bottom-left, plus a Play / + / thumbs-up / details row and a metadata footer. Layout is a 1:1 copy of Netflix; skin is taken from the user's Jellyfin (NeutralFin theme + Home Screen Sections rows).

Target environment (verified against the live server, read-only):
- Jellyfin **10.11.11**, theme **NeutralFin 1.4.0** (ElegantFin-based), **Home Screen Sections** (HSS) plugin, which means **File Transformation** is already installed.
- Home rows use `.overflowBackdropCard` / `.overflowSmallBackdropCard` (16:9) at `clamp(210px, 15.6vw, 340px)` wide, with `.cardText` hidden. HSS Top Ten cards add a left margin on `.cardBox`, so geometry must be measured from the image container, not `.card`.

Decisions made with the user: **live-seek only** for clip source (no ffmpeg generation in v1, but clip selection must be a pluggable, improvable heuristic) and **all card lists** (home, library grids, search, "More Like This"), not just the home page.

## What Netflix does (read from the 3 screenshots; items marked *recollection* are not visible in them)

| Aspect | Observed / spec |
|---|---|
| Trigger | Dwell ~500 ms on a card. Leaving before the dwell cancels. |
| Geometry | Popup is **1.5× the card width** (263 px card -> 394 px popup), centered on the card both axes, overlaps neighbours without shifting them. Top part is a 16:9 still/video; info panel below it (~160 px). *Recollection:* at row edges the popup anchors to the screen-side edge rather than centering. |
| Top area | Still (poster/backdrop) -> after ~500 ms crossfades to video. Title **logo bottom-left** stays over still and video. **Mute/volume circle bottom-right**. Muted by default. Rounded top corners, deep shadow. |
| Controls row | White filled circular **Play**; outlined circles: **+** (My List), **thumbs-up**; far right outlined circle **chevron-down** (more info). Bell appears only on some items in Netflix; no Jellyfin equivalent, so omitted. |
| Not started | Row 2: maturity pill (`MA 15+`), runtime (`1h 44m`), outlined `HD` badge. Row 3: 3 tags separated by `•`. |
| In progress | Row 2 becomes `S1:E1 "Pilot"` (episodes) + thin red progress bar + `50 of 54m` (watched of total, minutes). Movies: bar + `X of Ym`, no S/E label. |
| Close | Pointer leaves card+popup (small grace period) -> reverse the scale-out, tear down video. *Recollection:* moving to a neighbouring card while a popup is open re-opens faster than 500 ms. |
| Timeline | t=0 enter, t=500 popup opens (~250 ms scale/fade), t~1000 video fades over still, clip ends -> fades back to still, stays open until you leave. |

## Skin: map Netflix elements to the user's theme (reuse NeutralFin CSS variables, don't hardcode)

Use theme vars with sane fallbacks so it keeps working if the theme changes.
- Radius: `var(--largeRadius, 1em)`; panel bg: `var(--lighterGradientPoint)` / `--drawerColor` with `backdrop-filter: var(--blurLarge)`; border: `var(--defaultLighterBorder)`.
- Text: `--textColor` (primary), `--dimTextColor` (metadata); play circle `--fullContrastColor` on `--zeroContrastColor`; outlined buttons use `--lighterBorderColor`, hover `--hoverBackgroundColor`.
- Progress bar: 3 px, fully rounded, 25% white track (matches the user's existing `.itemProgressBar` override), foreground = theme accent.
- Font: inherit (Inter via NeutralFin). Icons: Material Symbols Rounded already loaded by the theme (`.material-icons` w/ `--fill`/`--wght`), so no icon assets needed.
- Shadow/elevation: one large soft shadow; no Netflix red. Layout stays Netflix-exact.

## Architecture

```
Jellyfin.Plugin.HoverPreview/               (net9.0, Jellyfin.Controller/Model 10.11.x, runtime excluded)
  Plugin.cs                  BasePlugin<PluginConfiguration>, IHasWebPages (config page)
  PluginServiceRegistrator.cs  IPluginServiceRegistrator: DI for picker, cache, injector (IHostedService)
  Configuration/PluginConfiguration.cs + configPage.html (embedded)
  Api/HoverPreviewController.cs
        GET /HoverPreview/client.js   [AllowAnonymous]  embedded script (script tags can't send auth)
        GET /HoverPreview/client.css  [AllowAnonymous]
        GET /HoverPreview/Clip/{itemId}  [Authorize]  -> ClipPlan JSON
  Services/ClipPicker.cs     orchestrates IClipHeuristic ladder, returns {itemId, mediaSourceId, startTicks, durationTicks, strategy, playMethod}
  Services/ClipCache.cs      in-memory + JSON under plugin config dir, key = itemId + DateModified + heuristicVersion
  Services/Heuristics/*.cs   ChapterAwareHeuristic, PositionFallbackHeuristic (v1), TrickplayMotionHeuristic (v2)
  Injection/ScriptInjector.cs  File Transformation (reflection) with index.html fallback
  Web/hover-preview.js, hover-preview.css   embedded resources
Jellyfin.Plugin.HoverPreview.Tests/         xUnit for heuristics
build.yaml, meta.json, manifest.json        plugin repo packaging
```

### Script injection
1. **Primary:** reflection call into File Transformation: scan `AssemblyLoadContext.All` for an assembly name containing `.FileTransformation`, get `Jellyfin.Plugin.FileTransformation.PluginInterface`, invoke `RegisterTransformation` with `{id, fileNamePattern: "index\\.html$", callbackAssembly, callbackClass, callbackMethod}`; the callback receives `{contents}` and returns HTML with `<script defer src="/HoverPreview/client.js"></script>` inserted before `</body>`. (Exact payload/callback signature is only partly documented, so confirm against the File Transformation source in M0; HSS is a working reference.)
2. **Fallback:** if File Transformation is absent, patch `index.html` on disk at startup (can fail on read-only/Docker perms; log clearly).
3. Do not reference File Transformation directly (separate load contexts).

### Client (vanilla JS, no build step for v1)
- **One delegated `pointerover`/`pointerout` listener on `document`**, resolving `closest('.card[data-id]')`. This works for HSS rows and any plugin that injects cards later; no per-row wiring and no re-scan after navigation.
- Gate: `html.layout-desktop` and `(hover: hover)`; skip TV/mobile. Types allowed: Movie, Series, Season, Episode (BoxSet: info card, no clip). Skip when the main video player is open.
- State machine: `idle -> pending(500ms) -> opening -> still(~500ms) -> playing -> ended`; every transition cancels via `AbortController`/timers, so rapid sweeps across a row never leak.
- One reused `#hp-root` (fixed, high z-index) and one reused `<video muted playsinline>`. Position from `.cardImageContainer` `getBoundingClientRect()`, 1.5x width, clamp to viewport, `transform-origin` toward the nearer edge.
- Pointer stays in popup -> stays open; ~100 ms leave grace. Close also on scroll/wheel of the row scroller, `viewbeforehide`, tab hidden.
- Data via `window.ApiClient` (`getItem` with Genres, OfficialRating, RunTimeTicks, UserData, ImageTags, ParentLogo*, MediaSources; `NextUp` for series). Logo: `/Items/{id}/Images/Logo` (episodes/seasons use `ParentLogoItemId`+`ParentLogoImageTag`). Still: Backdrop (or Primary for episodes). `HD` badge: `Width >= 1280`.
- Buttons: Play/Resume -> delegate to the card's native overlay play button (verify in M0; fallback to navigating to details). `+` -> `ApiClient.updateFavoriteStatus`. Thumb -> `ApiClient.updateUserItemRating`. Chevron -> `#/details?id=..&serverId=..`.
- Config (served inline into client.js): delay, preview length, volume/mute default, enabled areas, hide-for-played toggle.

### Playback (live-seek)
- Server decides a hint in `ClipPlan.playMethod` from the MediaSource (container/codec/bit depth); client confirms with `video.canPlayType` / `MediaCapabilities`.
  - **Direct:** `/Videos/{id}/stream?static=true&mediaSourceId=..&api_key=..#t=start,end` (HTTP range seek). Works for mp4/H.264/AAC.
  - **Transcode:** `/Videos/{id}/stream.mp4?startTimeTicks=..&videoCodec=h264&audioCodec=aac&maxWidth=854&videoBitRate=1500000&mediaSourceId=..&playSessionId=<uuid>&deviceId=..`. Low-res, since the card is ~400 px wide.
  - On `error`, fall back direct -> transcode once, then degrade to still-only.
- `<video>` can't send headers, so the token goes in `api_key` (same as jellyfin-web's own stream URLs). Verify 10.11 still accepts it (M0).
- Always stop transcodes on close: abort the request and `DELETE /Videos/ActiveEncodings?deviceId=&playSessionId=`. Cap concurrent previews at 1.
- Stop at `startTicks + duration` (timeupdate guard) because `#t=` end is not reliable on transcodes.

## Clip selection ("which part is best?"): v1 heuristic ladder, v2 data-driven

All logic lives server-side behind `IClipHeuristic`, cached per item, versioned (`heuristicVersion` busts the cache when we improve it). Inputs available now with no extra analysis: **Media Segments** (`Intro, Recap, Outro, Commercial, Preview` via `GET /MediaSegments/{id}`, filled by Intro Skipper or chapter inference), **Chapters**, runtime, `UserData`.

v1 (deterministic, spoiler-safe):
1. Hard exclusions: Intro/Recap/Outro/Commercial/Preview segments; first 3% and last 15% (credits, finales) when no segments exist.
2. **Episodes:** if an Intro segment starts after ~60 s, the **cold open** is the hook, so use it (clamped to preview length). Else start at `introEnd + ~30 s`. No segments: ~20-40% in.
3. **Movies:** candidate windows starting at chapter boundaries within 15-55% of runtime (past setup, before the climax). No chapters: same range, snapped to a 10 s grid. Pick by stable hash of itemId so rows don't all start at 33%.
4. **Shows (decided with the user):** a pool of **3 clips from S1E1 and S1E2** (2 from E1, 1 from E2, rebalanced if an episode is too short). The browser picks one at random per page load. **Every** card of the show (series, season, any episode, Continue Watching) plays from this pool; no per-episode clips, so nothing past the start of the show is ever shown. The info panel still shows the card's own episode/progress.
5. Items under ~3 min or with no usable range: still-only card.
6. Duration: config, default 60 s, min 20 s.

v2 (after v1 ships; the "best parts" work): score candidate windows from Jellyfin's **Trickplay** tiles (already generated, 10 s interval): inter-frame difference for action density, mean luma to drop black/fade windows, low-variance (static talking head) penalty, hard cap before ~70% to avoid climax spoilers. Computed once per item by a scheduled task with SkiaSharp (ships with Jellyfin), stored in the cache. Include a small **eval harness**: dump the chosen window + 3 runners-up for ~20 mixed titles as a contact sheet so we tune by eye rather than guess.

## Milestones

- **M0 Spike (de-risk before coding the plugin):** install .NET 9 SDK; confirm File Transformation registration payload on 10.11.11; confirm `api_key` on `<video src>`; direct-play seek with `#t=` on one mp4 and one mkv; transcode `startTimeTicks` + clean teardown; confirm native play-button delegation; capture real DOM of an HSS card row (inspect via DevTools). Prototype popup in DevTools/userscript against the live server.
- **M1 Skeleton:** plugin project from `jellyfin-plugin-template`, GUID, embedded JS/CSS served, injection working, "hello" log on hover. Distributed through a GitHub plugin repository (`manifest.json` + release zip).
- **M2 Card UI (no video):** dwell/cancel, geometry, still->logo, controls, metadata/in-progress variants, edge anchoring, theme skin.
- **M3 Clips:** `ClipPicker` v1 + endpoint + cache, playback/fallback/teardown, mute button, crossfades, end-of-clip behaviour.
- **M4 Polish + packaging:** config page, per-area toggles, edge cases below, `build.yaml`/`manifest.json`, README.
- **M5 (later):** v2 trickplay-motion scoring + eval harness; optional local-trailer and generated-clip sources slot in as extra `IClipHeuristic`/sources.

## Risks / edge cases to design for
- Version lock: plugin ABI is pinned to the 10.11.x Jellyfin packages; set `targetAbi` to 10.11 and rebuild on server upgrades.
- Other plugins mutating cards (HSS top-ten margin, hero banner) so measure from `.cardImageContainer`, never assume `.card` size.
- Row horizontal scrolling while hovering; popup would detach, so close on scroll.
- HEVC/10-bit/DTS sources force transcode: guard concurrency, low bitrate, and respect library permissions (resolve item via `ILibraryManager` and `IsVisible(user)` before returning a plan).
- Items with multiple versions/media sources: pick the default source; skip 3D/ISO/BluRay folder sources (still-only).
- Tokens in URLs show in server logs: same exposure as jellyfin-web, documented.
- Script and CSS endpoints are anonymous by necessity; they contain no per-user data. Clip/Config endpoints require auth.

## Verification

- Unit: xUnit for each heuristic with synthetic chapters/segments (cold open, no intro, no chapters, 90 s runtime, hash stability, exclusion zones).
- Build: `dotnet build -c Release` and `dotnet test`; plugin loads on 10.11.11 with no `NotSupported` status; server log shows the transformation registered.
- Manual matrix on the real server (home, library grid, search, More Like This): Movie, Series, Episode in Continue Watching, partially watched movie, mp4/H.264 (direct), mkv/HEVC (transcode), no chapters, BoxSet, card at left/right/bottom viewport edge, HSS Top Ten row, hero-banner page.
- Fidelity: side-by-side against screenshots 1-3 at the same viewport. Check popup = 1.5x card width, 16:9 art, centered, 500 ms dwell, logo bottom-left through still and video, in-progress layout (`S1:E1 "Pilot"`, bar, `50 of 54m`).
- Leak check: sweep the pointer across a full row rapidly; `document.querySelectorAll('video').length <= 1`, no orphaned ffmpeg (`ps`, `/Videos/ActiveEncodings`), no console errors.

## Status (v1.0.0)

M0–M4 implemented. Verified on a local Jellyfin 10.11.11 container (with and without File Transformation 3.0.1) and the card skin checked against the live NeutralFin theme. Changes from the plan:
- Injection: File Transformation when present, otherwise a built-in ASP.NET startup-filter middleware rewrites index.html in memory (the on-disk patch fallback was dropped: it fails on package installs and is undone by upgrades).
- Clip plans are cached in memory only (they take milliseconds to compute); the cache key includes the heuristic version and the plan-affecting settings.
- Popups open only on real pointer movement, so a page rendering under a resting cursor doesn't pop a card.
- The still prefers the textless Backdrop (card Thumb art often has the title baked in, which doubled up with the logo).
