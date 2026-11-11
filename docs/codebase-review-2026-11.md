# IT HelpDesk Agent: honest codebase review

2026-11-11, reviewed on branch `claude/ops-catalog`; fixes on `claude/harden-review`.

The architecture is right and the code is better than most hand-written projects of this size. The main risk is not code quality: it is that the product has grown much faster than it has been tested on real PCs, and that two safety switches are still off by default.

## What is solid

I read the security-critical paths of all 13 projects (about 55,000 lines of C# and TypeScript, plus 11,000 lines of tests) on branch `claude/ops-catalog`. I came looking for the usual problems of AI-written code and mostly did not find them.

- **The core safety idea is right, and the code really enforces it.** The AI can only pick from a fixed list. The control plane decides who, what and when. The PC checks the signature and the hash of every package itself, with its own copy of the public key, so even a hacked server cannot make a PC run a new file. I found no place where text from the AI or a browser becomes a command line.
- **Self-update is built the careful way.** Versions sit side by side, nothing is overwritten in place, the update zip is checked file by file against a signed list, and a broken version goes back by itself and is never retried.
- **Sign-in for the dashboard is done properly.** Strong password hashing, lockout after 5 wrong tries, roles checked on the server, and any new page needs sign-in unless someone opens it on purpose.
- **The PC side distrusts everything.** The Tray is identified by what Windows says about it, not by what it claims. Folders that hold runnable files are checked for safe permissions before use.
- **Background work uses no AI**, as asked. Health rules, updates, inventory, lessons and problem reports are plain code.
- **Tests and docs are real.** 428 control-plane tests plus 326 Windows tests, and a bilingual guide that matches the code. That is rare for a project built this fast.

My opinion of the idea itself: it is a good product for an organisation with no SCCM or Intune and many repeated tickets (activation, printers, dialer, clock). The "no free scripts" rule is the feature that makes it safe to give to 100 PCs.

## What is weak or risky

Most important first. The first four matter before the 100-PC pilot.

| # | Risk | Why it matters | What to do |
| --- | --- | --- | --- |
| 1 | **Features are far ahead of real testing.** Self-update, Get logs, log server forwarding, AD sign-in, KMS, software installs and health rules were built in days and never ran on a real second PC. | Tests prove the logic, not that it works on 100 different Windows 10 and 11 machines. This is the biggest risk in the project. | Stop adding features for a while. Run 5 to 10 PCs for two weeks first, then 100. |
| 2 | **`AgentSecurity:RequireAuthentication` is off by default.** Any computer on the network can connect and show up as a PC. | The worst part is closed (see next section), but an unknown machine can still register and fill the dashboard. | Enroll every pilot PC, then turn it on. |
| 3 | **The pilot needs HTTPS on the control plane.** PCs refuse to download packages and updates from another machine over plain HTTP, on purpose. | Without a certificate, chat works but repairs and updates do not, on every PC except the server itself. | Decide where the certificate comes from (the domain CA is the natural choice). |
| 4 | **The work is spread over many unmerged branches.** `master` is far behind; PR #5 is very large; `claude/merge-nov` and `claude/kms-activation` hold more. | The longer they stay apart, the harder and riskier the merge. Nobody can review a PR that big line by line. | Merge to `master` soon and work in small PRs after that. |
| 5 | **No automatic test run.** Tests only run when someone remembers. | A change can break something and nobody notices for days. | A GitHub Actions job that builds and tests on every push. |
| 6 | **Things in git that should not be there.** The `.local` folder (300 MB, with a database and a device credential), `mustbedeleted.cs`, and the reseller BAT files in `tmpfiles`. | Slow clones, and private data in history. | Finish the cleanup before sharing the repo. |
| 7 | **The dashboard only works on the server itself**, over plain HTTP, and its Node process is started by hand. | The helpdesk team must remote into the server, and after a reboot the dashboard is down until someone starts it. | Run it as a Windows service; later decide how the team reaches it safely. |
| 8 | **One server, one database file.** | If the server dies, all PCs lose the helpdesk until it is back. | Fine for a pilot. The new daily backup covers the data. |
| 9 | **On a shared PC, the Tray does not check that the pipe it talks to belongs to the real service.** | A second logged-in user could try to pose as the service. The easy way to do it is removed; a full check is still missing. | Small follow-up change in the Tray. |
| 10 | **Old activation scripts cannot be signed** (`RequireForActivationRepairs` is off). | They are protected by hash only, not by the administrator's signature. | Move them into signed packages, then turn the setting on. |
| 11 | **Some files are very large** (`ChatStore.cs` is 1,800 lines, `Program.cs` 765) and every database call waits on one lock. | Harder to change safely; fine for 100 PCs, a limit at a few thousand. | Not urgent. Split when next touched. |
| 12 | **Chat text goes to an outside AI provider**, masked by patterns. | Unusual personal data in free text can get through. | An organisation decision, already noted in the security guide. |

## What changed on `claude/harden-review`

Eight fixes, made from `claude/ops-catalog`. Each is for a real gap found while reading, not a style change.

| Where | Before | Now |
| --- | --- | --- |
| Control plane | A machine with no device token could connect and call itself an enrolled PC. It would then receive that PC's commands. A revoked PC could come back the same way. | Refused. An enrolled or revoked ID needs its token. |
| Control plane | One enrollment code could be used to take over the identity and chats of another PC that was still active. | Refused from the network. The admin revokes the old device first. The refused code stays usable. |
| Control plane | No backup. A damaged database meant enrolling every PC again. | One complete copy a day in a `Backups` folder, newest 7 kept. No AI. |
| Control plane | One database error while picking up the next chat would stop the whole API. | It logs the error and tries again in 5 seconds. |
| PC service | If the control plane was unreachable at first start, or the saved credential was unreadable, the service ended. | It stays up, shows the reason in the Tray, and retries. |
| Self-update | Three power cuts in the first 24 hours looked like three crashes and rolled a good version back for ever. | A stop where Windows itself restarted is not counted as a crash. |
| Self-update | If power failed in the middle of the switch, the PC stayed on the new version with nobody watching it and no rollback. | The service finishes the update itself: keeps and watches a version that connected, rolls back one that never did. |
| Tray pipe | Every user had full control of the pipe, and a program that connected and stayed silent could block all Tray users. | Users can only read and write, and a silent caller is dropped after 10 seconds. |

The SignalR client package also moved from 10.0.9, which has a known high-severity warning, to 10.0.15. The server side of that warning is fixed by installing the newest .NET 10 runtime on the control-plane server.

**What is tested.** On the development PC the whole solution builds with 0 errors and every test passes: 428 of 428 control-plane tests and 326 of 326 Windows tests, including 8 new ones for these fixes. The new pipe permissions were checked there too: an ordinary user can still connect and write, and cannot create a pipe of the same name.

**What is not tested.** The service changes (retry at start, pipe timeout) have no automatic test. They build cleanly and are small, but the first real proof is a run of the dev stack from this branch.

## What I recommend next

1. **Try this branch on the PC.** Run the dev stack from `claude/harden-review` and use the Tray for a normal chat and one repair. If it behaves, merge it into `claude/ops-catalog`.
2. **Get everything onto `master`.** Merge PR #5, then `claude/merge-nov` and `claude/kms-activation`. After that, small PRs only.
3. **Decide the two pilot switches:** where the HTTPS certificate comes from, and the day `RequireAuthentication` is turned on.
4. **Pilot small first.** 5 to 10 PCs for two weeks, mixed Windows 10 and 11, with the Problems page open every morning. Fix what it shows before going to 100.
5. **Add an automatic test run on GitHub** so every push is built and tested without anyone remembering.
6. **Pause new features until the pilot is stable.** The Ideas page keeps them safe.
