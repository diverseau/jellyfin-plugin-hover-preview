# Hover Preview for Jellyfin

Netflix-style hover cards for jellyfin-web. Rest the mouse on any card for half a second and a
larger card pops out of it with the backdrop, the title logo, **Play / + / 👍 / ⌄** buttons and the
item's details, then plays a short, muted preview clip.

- Works on every card list: home rows (including Home Screen Sections), library grids, search and
  "More Like This".
- **Movies** play a clip from the middle of the film (15–55% of the runtime by default), never the
  opening logos, the ending or the credits.
- **Shows** play one of three clips taken from the first two episodes of season 1, picked at random
  on each page load. Every card of the show (series, season, any episode, Continue Watching) uses the
  same pool, so a preview can never spoil anything past the start of the show.
- Already watching? The card shows `S1:E6 "Title"`, a progress bar and `32 of 46m`, like Netflix.
- Click the picture to play (from the start, or where you left off, exactly like the Play button;
  never from the preview's position). Click anywhere else on the card, or the ⌄ button, for details.
- Uses your theme's colours, corners and icons (tested with NeutralFin / ElegantFin and the default
  theme).

Requires **Jellyfin 10.11**. Desktop browsers only (a mouse is needed to hover); TV and mobile
layouts are left alone.

## Install

1. Dashboard → **Plugins** → **Repositories** → **+** and add:

   ```
   https://raw.githubusercontent.com/diverseau/jellyfin-plugin-hover-preview/main/manifest.json
   ```

2. **Catalog** → **Hover Preview** → **Install**, then restart Jellyfin.
3. Reload the web page.

[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) is used
to add the script to the web client when it is installed (it already is if you use Home Screen
Sections or Plugin Pages). Without it, Hover Preview injects the script itself; either way nothing on
disk is modified.

## Settings

Dashboard → Plugins → Hover Preview:

| Setting | Default | |
|---|---|---|
| Hover delay | 500 ms | How long the pointer rests before the card pops out |
| Play preview clips | on | Off = still image and details only |
| Clip length | 60 s | 20–180 s |
| Start muted | on | The mute button remembers your choice per browser |
| Where | all | Home, libraries, search, item details |
| Movies: earliest start / latest end | 15% / 55% | Spoiler guard for movies |
| Episodes: earliest start / latest end | 10% / 65% | Used for the show's first two episodes |
| Allow direct play | on | MP4/H.264/AAC files stream untouched |
| Direct play bitrate limit | 20 Mbps | Bigger files are transcoded |
| Transcode width / bitrate | 854 px / 1500 kbps | Size of transcoded previews |

## How it works

The plugin has a small server part and a browser script:

- On startup it registers an `index.html` transformation that adds
  `<script src="/HoverPreview/client.js">` and the stylesheet to the page.
- `GET /HoverPreview/Clips/{itemId}` (signed-in users only, respects library access, parental
  controls and playback permissions) returns where the clip starts, how long it is and the stream URLs. Clip positions come
  from chapters, Jellyfin's media segments (intro/recap/credits markers, e.g. from Intro Skipper) and
  the runtime, and are cached in memory.
- The browser script watches every `.card[data-id]`, builds the pop-out card with the same API
  jellyfin-web uses, and streams the clip from Jellyfin's normal stream endpoint: untouched for files
  the browser can play, otherwise as a small 854 px transcode that is stopped as soon as the card
  closes. Only one preview plays at a time. Previews don't count as plays or change watch progress.

Clip choice lives behind `IClipHeuristic`, so smarter selection (e.g. scoring trickplay frames for
motion) can be added without touching the rest.

## Security notes

- `client.js` and `client.css` are served without authentication because a `<script>` tag cannot
  send Jellyfin's auth header. They contain only the non-secret UI settings above.
- The clip endpoint requires a signed-in user and applies the same rules as Jellyfin's own playback:
  library access, parental controls, the "allow media playback" and "allow transcoding" permissions,
  and the remote bitrate limit. Hidden and non-existent items both return 404.
- Like jellyfin-web's own player, preview `<video>` URLs carry the user's access token (as `ApiKey`),
  because a video element cannot send headers. Tokens in URLs can show up in reverse-proxy access
  logs.
- Previews never report playback, so they don't mark items as watched or change resume positions.

## Development

```sh
dotnet build -c Release
dotnet test
scripts/package.sh        # builds artifacts/hover-preview_<version>.zip and updates manifest.json
```

The version lives in `Directory.Build.props` (`HoverPreviewVersion`, SemVer). For a quick manual
install, copy `Jellyfin.Plugin.HoverPreview.dll` into `<config>/plugins/HoverPreview_<version>/` and
restart Jellyfin.

## License

MIT
