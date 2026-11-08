# Milestone 3 – test sheet

What's new:
- **Tray icon.** NetGuard sits next to the clock and starts with Windows. Its colour shows the
  state: green = normal internet, slate = Offline, blue = WoW, amber = Maintenance, purple = your
  own profiles, red = protection service not reachable. Closing the window keeps it in the tray.
- **Setup wizard:** find apps → check the programs → recovery code → a one-minute recovery test →
  starting profile.
- **Programs tab:** which programs each profile allows, and which are always blocked. Add a program
  file or a running program; remove with one click.

Expect about 20 minutes. Update first: **Tools → Check for updates → Install update** (or run
NetGuardSetup.exe from the Releases page). Then sign out and in once, so the tray icon starts.

---

## 1. Tray icon

| Check | Expected |
|---|---|
| After signing in | a small shield next to the clock (green); you may need to click the ^ arrow and drag it next to the clock |
| Hover | "NetGuard – normal internet" |
| Double-click | the main window opens |
| Close the window (X) | it disappears, the tray icon stays |
| Right-click | Open NetGuard / current state / Hide tray icon |
| Start NetGuard from the Start menu while it's in the tray | the existing window comes to the front (no second copy) |

## 2. Setup wizard

**Setup → Run setup wizard**, go through every step:

1. Welcome.
2. Find your apps: "Keep current configuration" is fine. (Or *Search again*: it replaces the program lists.)
3. Check the programs: Blizzard and Spotify files say *signed by …*.
4. Recovery code: you already have one. Don't create a new one unless you want to.
5. **Recovery test → Run the 1-minute test.** ✅ all four lines get a green tick:
   internet works before, blocked during, the lock ends by itself, internet works again.
6. Start: keep *normal internet for now* → Finish.

## 3. Programs tab

| # | Do | Expected |
|---|---|---|
| a | Click **WoW** | WoW, Battle.net, Blizzard Agent, Spotify with ✅ file lines and "only if signed by …" |
| b | Click **Always blocked** | Steam with ⛔ lines |
| c | **New profile…** → `Music` | appears in the list and as a card on the Profiles tab (purple) |
| d | With *Music* selected: start Spotify, **Add a running program…** → pick Spotify | confirmation shows "Signed by Spotify AB…" → OK → Spotify listed with ✅ |
| e | **Add program file…** → pick any small unsigned tool if you have one | it says "Not digitally signed … this exact version" |
| f | **Switch to Music** (Profiles tab) | only Spotify works; Chrome and WoW don't. Tray icon purple |
| g | Programs → *Music* | *Add* buttons greyed out: "only possible with normal internet or in Maintenance" |
| h | Remove Spotify from *Music* (allowed during a lock) | within seconds Spotify stops working too |
| i | Tools → Emergency restore (code) → back to normal internet | tray green |
| j | Delete the *Music* profile | gone from both tabs |

## 4. Tray during a test lock

Profiles → Test lock (3 min) on WoW. ✅ Tray blue, hover shows "WoW test lock until …",
a balloon about a minute before the end, green again afterwards.

## 5. Report back

✅/❌ for each row, screenshots of the tray (hover), the wizard's recovery-test page and the
Programs tab, and anything confusing or ugly.
