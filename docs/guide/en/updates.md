---
title: Updating the endpoint agent
summary: Ship a new version of the service and Tray to every PC without reinstalling, and let a PC go back to its old version by itself when the new one fails.
order: 9
---

# Updating the endpoint agent

When the development team adds something to the endpoint agent (the Windows service, the Tray and their features), the PCs do not need a reinstall. Each PC asks the control plane about once an hour whether there is a newer version for it, downloads it, checks it and switches to it. If the new version does not work on that PC, the PC goes back to the version it had before and stays there until someone looks at the problem.

No AI is involved. Updates are decided by fixed rules on the control plane and carried out by a small program on the PC, so they cost no tokens.

## What happens on a PC

1. **Ask.** About once an hour (and right away when an admin saves new rollout settings) the service tells the control plane its version, its Windows build and which versions failed on it before. The control plane answers with a version to install, or with nothing.
2. **Wait for a quiet moment.** The control plane offers nothing while the user is chatting with the helpdesk (or chatted in the last 10 minutes), while a repair is running, outside the update hours you set, or while updates are paused.
3. **Download and check.** The PC downloads the zip and checks its SHA-256, the signature of the manifest (the same signing key as repair packages), and the SHA-256 of every single file. Anything wrong and it stops; nothing changes.
4. **Install next to the old version.** The new version is unpacked into its own folder, `C:\Program Files\EndpointAgents\versions\<version>\`. The old version is not touched.
5. **Switch.** The updater stops the service, points it (and the Tray's sign-in entry) to the new folder and starts it again. The Tray is closed and opened again for the signed-in user.
6. **Health check.** The new service must connect to the control plane within 3 minutes. If it does, the update is done and the PC reports *Succeeded*. Old folders are removed, except the previous version, which is kept for going back.

The PC's own settings (control plane address, its device identity, trusted signing keys) live in `C:\ProgramData\EndpointAgents\Config\appsettings.site.json`, outside the version folders, so every version keeps them.

## Going back by itself

A PC goes back to its previous version, without anyone doing anything, in two cases:

- **The new version does not connect** within 3 minutes after the switch. The updater puts the old version back at once.
- **The new version keeps crashing** in its first 24 hours: if the service stops unexpectedly 3 times in that time, the PC goes back to the old version.

A power cut or a hard reset is not counted as a crash: when Windows itself started again after the service's last start, the PC went down, not the new version.

If the PC loses power in the middle of the switch, the updater never finishes its part. The service then finishes it by itself about 15 minutes later: a new version that reached the control plane is kept and watched for 24 hours like any other update, and one that never connected is rolled back.

The failed version is written down on the PC and reported to the control plane. **That PC will not try that version again**, even when it is the target for everyone, so a broken version cannot crash a PC over and over. The dashboard shows the PC as *Rolled back* with the reason. When the team has fixed the problem, they ship it as a **new** version number (for example 0.3.1 after 0.3.0), and the PC takes that one.

A PC never goes to an older version on its own (no downgrade), so a mistake in the settings cannot push old code to everyone.

## Making a new version (admin PC)

On the admin PC that has the package signing key:

```powershell
.\scripts\new-agent-update.ps1 -Version 0.3.0 -ReleaseNotes "Event log checks" -Sign
```

- `-Version` must be higher than what the PCs run. Use x.y.z.
- It builds the service, the Tray and the updater, removes development settings and debug files, writes `agent-update.json` with every file's SHA-256 and signs it.
- The result goes to the control plane's file repository, in `agent-updates\0.3.0\`. Add `-Development` to write into the repository's development copy instead.
- Without `-Sign` the version shows as *Not signed* and no PC takes it.

## Rolling it out (dashboard)

Open **Administration > Agent updates** (admins only).

1. **Versions in the file repository** lists each version as *Ready*, *Not signed*, *Hash mismatch* or *Invalid*. Only *Ready* versions can be chosen.
2. **Try it on a few PCs first.** Tick *Pilot* next to 2 or 3 PCs (your own, for example), choose the version as **Pilot version** and save. Online PCs check within a minute.
3. **Watch the PCs table.** Each PC shows its version and its state: *Up to date*, *Waiting* (it will update at the next quiet moment), *Updating*, *Rolled back* or *Failed*, with the reason.
4. **Then everyone.** When the pilot PCs are fine for a day, choose the version as **Version for everyone** and save.
5. **Update hours** (for example 12:00 to 14:00, server time) keep updates out of busy hours. **Pause all updates** stops new installs at once; PCs already switching finish or go back.

## The very first time

PCs that run version 0.1.0 do not have the updater yet. Install version **0.2.0** (the first one that can update itself) once on each PC in the usual way (`scripts\install-endpoint.ps1`). From then on, every later version arrives by itself.

## Limits to know

- A PC needs to reach the control plane to prove the new version works. If the control plane is down during the switch, the PC goes back to its old version and marks that version as failed, so it will wait for the next version number. This is safe, but "no network" then looks like a failed update: check the reason on the dashboard, and keep the control plane up during a rollout.
- The update only replaces the endpoint agent. It never runs a script, changes Windows settings or installs other software.
- The service runs as LocalSystem and the files sit under Program Files, so ordinary users cannot change them.
