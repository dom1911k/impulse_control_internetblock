# Milestone 4 – test sheet

What's new:
- **Unlock requests.** Going to a looser profile: write a reason (≥ 20 characters) → 45-minute wait
  (can't be shortened) → 10 minutes to retype a random passage of about 400 characters (pasting is
  blocked) → otherwise it expires. At most 2 per day; cancelled and expired ones count.
  Maintenance reached this way ends by itself after 45 minutes.
- **Commitments.** "Stay in WoW (or stricter) until 23:00". No unlock requests until then. Can be
  extended, never shortened. Survive reboots.
- **Play time.** Per game profile: weekly timetable, daily and weekly limits, counted while the
  profile is active. Outside play time you're in Offline and the game is blocked in every profile,
  Maintenance included. Warnings at 15/5/1 minutes, then an automatic switch; once a day you can
  take 10 extra minutes (they come off the next day). Stricter plan changes apply at once; looser
  ones after 72 hours.
- **Changed programs.** A program pinned to its exact version that changed is blocked with a notice;
  re-approve it in Programs (with normal internet or in Maintenance).
- **Clock protection.** Changing the Windows clock doesn't speed anything up; it's logged.

Update first: **Tools → Check for updates → Install update**, then reopen NetGuard.
The full test takes a while because of the real waiting times. Spread it over an evening.

---

## 1. A play plan (≈ 5 min to set up)

**Play time** tab → profile **WoW** → tick *WoW has a play plan*.
- Remove the default windows for **today** and add one from **now** (round down to the half hour) to
  about **2 hours from now**.
- Per day: **30 min**. Per week: no limit.
- Game: only **World of Warcraft** ticked. Fallback: **Offline**.
- **Save**. ✅ "applies right away" (adding a plan is stricter).

## 2. Playing

Profiles → **Switch to WoW** (allowed: it's play time). ✅
- banner shows **Play time left** with a countdown (30:00 or less)
- WoW card: "🎮 Play time now until … · today 0min of 30min"
- tray hover: "WoW, play time until …"

Let it run. ✅ tray warnings at **15, 5 and 1 minute**. At 5 minutes the banner shows
**10 more minutes (once today)**: take it once ✅ (the label switches to "Extra minutes left").
When it ends: ✅ automatic switch to **Offline**, History shows *Switched automatically – play time
is over*. WoW card: "⏸ Today's play time is used up. Next play time: tomorrow …", *Switch* disabled.

## 3. Commitment

In Offline: **Commit…** → *Offline*, preset **+1 hour** → Commit.
✅ Banner "Offline mode – committed until …". Maintenance card says "You committed to Offline until …".
- **Commit…** again with an earlier time → ❌ "can be extended, never shortened".
- **Commit…** with a later time → ✅ extended.

Optional clock test (needs the admin prompt): Settings → Time & language → Date & time → turn off
*Set time automatically* → set the time **3 hours later**. ✅ The commitment is still active, History
shows *Clock change ignored*. Set the time back (turn *Set time automatically* on again).

## 4. Unlock (after the commitment has ended – about an hour)

Offline → Maintenance card → **Request unlock…** → write a reason → *Start the waiting time*.
✅ An amber panel: "Unlock to Maintenance – waiting 44:59", tray icon orange.
Wait 45 minutes (do something else). ✅ Tray balloon "The waiting time is over".
- Try to **paste** into the box → nothing happens.
- Type a wrong character → "✗ Typo at character …".
- Type it correctly → **Confirm unlock**. ✅ Maintenance active, banner "Maintenance until …, then back".
- In Maintenance: Chrome works ✅, **WoW can't connect** ✅ (outside your play time or used up), Steam still blocked ✅.
- After 45 minutes ✅ automatically back to Offline.

Then check the limit: request two more unlocks the same day (cancel them right away). ✅ The second
one is refused: "Today's 2 unlock requests are used up".

## 5. Looser plan change

**Play time** → WoW → per day **1 h** → Save. ✅ "takes effect in 72 hours", an amber box shows the
waiting change with **Cancel the waiting change**. Cancel it ✅.

## 6. Report back

✅/❌ per step, screenshots of the Profiles tab during the unlock wait and during the confirm
step, the Play time tab, and the History tab. Anything confusing, too harsh or too lax?
