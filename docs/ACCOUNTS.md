# Account separation – step by step

**Why:** NetGuard's service runs as SYSTEM, and only an administrator can stop it, change its files or
restore normal internet. If your everyday account *is* an administrator, a weak moment is a single
"Yes" click away. With a separate admin account whose password you don't keep handy, it's a
deliberate, slow step.

**What changes day to day:** almost nothing. Games, Battle.net/WoW updates, Spotify, Windows Update
all work as before. When something needs admin rights (installing a program, some drivers), Windows
asks for the **admin account's password** instead of just "Yes".

> ⚠️ Do the steps **in this order**. Never take away your own admin rights before the new admin
> account exists and you've checked its password works. Otherwise the PC could end up with no
> administrator at all.

(Labels below are from English Windows 11; German labels in brackets.)

## 1. Create the admin account (while you're still an admin)

1. **Settings → Accounts → Other users** [Einstellungen → Konten → Andere Benutzer] → **Add account** [Konto hinzufügen].
2. Click **I don't have this person's sign-in information** [Ich kenne die Anmeldeinformationen für diese Person nicht].
3. Click **Add a user without a Microsoft account** [Benutzer ohne Microsoft-Konto hinzufügen].
4. User name: `NetGuardAdmin`. Choose a **long password** (e.g. 4 random words). Answer the three
   security questions (they let you reset the password if you lose it).
5. Back in *Other users*, click `NetGuardAdmin` → **Change account type** [Kontotyp ändern] →
   **Administrator** → OK.

## 2. Check the password works

1. Start menu → type `cmd` → right-click **Command Prompt** → **Run as administrator**.
2. Because you're still an admin, Windows only asks "Yes/No". That's fine. Close it again.
3. Sign out, sign in **once** as `NetGuardAdmin` (Windows sets up the account, takes a minute),
   then continue with step 3 *while signed in as NetGuardAdmin*.

## 3. Make your everyday account a standard user (signed in as NetGuardAdmin)

1. **Settings → Accounts → Other users** → click your normal account (`domin`) →
   **Change account type** → **Standard User** [Standardbenutzer] → OK.
2. Sign out and sign back in as `domin`.

## 4. Check it

As `domin`, start menu → type `cmd` → right-click → **Run as administrator**.
✅ Windows now asks for a **user name and password**, and offers `NetGuardAdmin`. Cancel it.

## 5. Put the password away

Write it on paper and keep it somewhere inconvenient (a drawer in another room, or give it to a
friend or family member). **Don't** save it in the browser or a password manager on this PC.

## Undoing it

Signed in as `NetGuardAdmin`: Settings → Accounts → Other users → `domin` → Change account type →
Administrator.
