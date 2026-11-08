# Milestone 5 – test sheet

What's new:
- **Weekly summary** at the top of the History tab (← → for earlier weeks): play time per day for
  each game profile, time in each profile, every unlock request with its reason and outcome, plus
  commitments, extra minutes, undone firewall changes, emergency restores. Practice mode doesn't count.
- **NetGuard Emergency Restore** in the Start menu: asks for your recovery code. If the service is
  broken, it asks Windows for admin rights and restores directly.
- **Tray:** *Switch to Offline now* (right-click). Stricter is always one click.
- The extra-minutes button shows how many minutes it gives (+10, or +1 in practice).
- **Installer:** a short "before you install" page, a clearer finish page, needs Windows 10 1809 or newer.

Update: **Tools → Check for updates → Install update** (the new installer pages appear only when
you run NetGuardSetup.exe yourself – that's fine to skip).

| # | Check | Expected |
|---|---|---|
| 1 | History tab | a "This week" card with your WoW play time per day (bars), time per profile, unlock requests (if any) |
| 2 | ← | last week's card; → brings you back |
| 3 | Practice from yesterday | not in the summary (time per profile ignores it, no practice unlocks listed) |
| 4 | Tray → right-click | *Switch to Offline now* (only when you're not in Offline). Click it → balloon "Offline mode is on" |
| 5 | Start menu → **NetGuard Emergency Restore** | a console window asking for the recovery code. Just press Enter → "Cancelled – nothing was changed" |
| 6 | Same, during a lock, type a **wrong** code | "That recovery code is not right. … attempts left" – nothing changes |
| 7 | Start menu → **NetGuard - manual recovery steps** | RECOVERY.md opens |

Then, as planned, the remaining practice-mode steps 4–7 from **TESTING-M4.md** (commitment,
unlock to Maintenance, request limit, looser plan change during a lock).

Report: ✅/❌ per row and a screenshot of the weekly summary.
