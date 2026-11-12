# Update & shut down, night guard, penalties, bass bonus – test sheet

What's new:
- **🛠 Update & shut down** (Home): Windows Update and Defender get internet – nothing else does.
  NetGuard installs all updates (progress in the banner), then shuts the PC down (60 seconds to
  cancel: Win+R → `shutdown /a`). The button turns **red** after 30 days without updates.
  Allowed any time, also during locks and at night.
- **🌙 Night guard 01:00–10:00:** only Offline (or stricter). Anything looser switches to Offline by
  itself; unlock requests and the Bass check are refused. Updates still work.
- **Tamper penalty:** each real tampering attempt during a lock adds **2 hours** to your commitment
  (or starts one): firewall off / allow-all, NetGuard rules deleted or changed, hosts file edited,
  the service killed, the clock changed, a SYSTEM task running a shell tool (also switched off),
  wrong recovery codes from the second one. Not for new firewall rules from installers. At most
  12 hours per day.
- **🎸 Bass practice earns play time:** every minute Rocksmith runs adds a minute to today's play-time
  limit, up to 1 hour a day. Dashboard card with today / earned / week / all time and a 30-day chart.
- **NetGuard updates in every profile:** the service has its own HTTPS rule for GitHub and checks once
  a day; the tray tells you when a new version is out (Settings → Install update).

Update: **Settings → Check for updates → Install update**.

| # | Do | Expected |
|---|---|---|
| 1 | In WoW or Offline: Settings → **Check for updates** | works during the lock ("up to date" or "version … available") |
| 2 | Home → **🛠 Update & shut down**, untick *Shut down* → Start | banner "🛠 Updating Windows: Searching… / Downloading … / Installing …"; Windows Settings → Windows Update shows activity; Chrome still has no internet |
| 3 | Wait until done | banner back to normal, History "Updates finished" with what was installed |
| 4 | Another day: with *Shut down* ticked | after the updates, a 60-second shutdown notice |
| 5 | Start Rocksmith for 10 minutes | dashboard bass card "Today 10m", "+10m earned"; WoW card's daily limit 10 minutes higher |
| 6 | During a lock (practice mode doesn't count!): admin PowerShell `Set-NetFirewallProfile -Profile Public -Enabled False` | undone within seconds; Home shows "Committed to … until" (+2 h) and "Tampering today extended your commitment by 2h" |
| 7 | After 01:00 (or tomorrow morning before 10:00) | banner "🌙 Night guard until 10:00"; WoW and Bass check refused; if you were in WoW, switched to Offline |

Penalties are real (2 hours per attempt) – only do step 6 when you don't mind the extra time.
