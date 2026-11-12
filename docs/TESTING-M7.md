# Bass check – test sheet

**Bass check** is a short window (20 minutes, twice a day) on top of your profile, for finding
tabs on Songsterr, downloading CDLC from CustomsForge (and the hosts it links to), and grabbing
a song's audio with the YT Downloader.

While it's open:
- **Chrome** gets internet, but Chrome's own website policy limits it to the websites on your
  list (Programs → 🎸 Bass check). Everything else shows "blocked by your organization", and
  Chrome says "managed by your organization" – that's the restriction working.
- **yt-dlp** (in `C:\Users\<you>\yt-dlp.exe`) and anything else on that list get internet too.
- Your profile stays as it is underneath (e.g. Spotify keeps working in WoW).
- Afterwards everything closes again by itself, and Chrome's restriction is removed.

Rocksmith, the CDLC Maker and the Bass Trainer ↔ converter link already work offline in every
profile – nothing needed for them.

Update: **Settings → Check for updates → Install update**.

## One-time setup (with normal internet)

Programs → **🎸 Bass check** → **Add program file…** → pick **YT Downloader.exe** (wherever you
keep it). Check the website list and add anything you know you need.

## Test

| # | Do | Expected |
|---|---|---|
| 1 | Switch to Offline (or WoW), then Home → **🎸 Bass check (20 min)** → OK | banner shows "Bass check – time left 19:59"; tray hover says so too |
| 2 | Chrome → songsterr.com, open a tab | works, including the YouTube embed |
| 3 | Chrome → customsforge.com → a CDLC → download link (MediaFire / Dropbox / Drive …) | downloads. If a page or download is blocked, note the website (address bar) |
| 4 | Chrome → any other site (e.g. reddit.com) | "blocked by your organization" |
| 5 | YT Downloader → paste the YouTube link → Audio only → Download | works |
| 6 | Another browser (Edge, Firefox) | no connection |
| 7 | **Close now** in the banner | Chrome loses internet within seconds; Home shows "1 left today" |
| 8 | Open it twice more on the same day | the third time is refused: "Today's 2× Bass check are used up" |
| 9 | During a commitment | refused |

If a site you need is blocked (step 3): later, with normal internet or in Maintenance,
Programs → 🎸 Bass check → type the address → **Add website**.
