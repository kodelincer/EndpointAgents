---
title: Security and hardening
summary: What the design prevents, what it accepts, how roles and sign-in work, what is masked before the cloud LLM, and what to harden in production.
order: 10
---

# Security and hardening

The product lets software repair users' PCs, so the design assumes that the AI can be wrong or be tricked by text in a message. Safety does not depend on the AI behaving. It depends on what the control plane and the endpoint will refuse to do.

## What the design guarantees

| Guarantee | How it is enforced |
| --- | --- |
| **Only catalogued operations.** The Tray, the agent and the operators can only trigger operations that exist as fixed code or as signed packages. | The control plane's command catalogue, the action catalogue and the package catalogue; the Tray bridge has no method that runs arbitrary commands or files. |
| **No arbitrary shell.** Nothing the AI or a browser sends is executed as a command line. | Actions use Windows APIs and a short allow-list of System32 tools with fixed arguments. Packages run with the playbook's fixed arguments and answers. Credentials in command parameters are rejected. |
| **The server is the authority.** Identity, consent, allow-listing and audit belong to the control plane, not to Hermes. | Hermes never receives a device ID. A random per-run capability resolves server-side to one PC and one Windows user. |
| **One agent cannot reach another's PC or data.** | Conversations are keyed by authenticated device and Windows SID; forged IDs, revoked tokens and reused capabilities fail. |
| **Reviewed files only.** | Every package is hash-pinned and signed. See below. |
| **Everything leaves a trail.** | Conversations, tool calls, action runs, consent decisions and operator changes are stored. |

## No free scripts: how this differs from SCCM and Intune

Tools such as SCCM ("Run Scripts") and Intune ("platform scripts") let an administrator paste **any** PowerShell script into the console and run it as SYSTEM on every PC they choose. That is powerful, but one mistake, or one stolen admin account, can break or harm every PC at once.

This system has no such feature, on purpose. Nobody can type a new command and run it on a PC: not the AI agent, not a helpdesk member, not an admin. A PC runs only two kinds of work:

- **Fixed operations from the catalogue.** Each one is written and tested in the product's code (for example "clear the DNS cache" or "restart the print spooler"), takes only typed parameters, and appears on the **Capabilities** page where an admin can switch it off.
- **Packages the administrator signed.** A BAT or EXE file that a person reviewed, signed with the company's package key and pinned by its hash. If one byte changes, the PC refuses to run it.

What this means in practice:

- If the AI is wrong, or a user's message tries to trick it, the worst it can do is pick the wrong step **from the allowed list**. It cannot invent a new one.
- A new kind of fix is added by a person, on purpose: a new catalogued operation in a release, or a new signed package. See [Packages](/docs/packages/) and [Endpoint actions](/docs/actions/).
- Bulk or risky work still waits for a person: Disruptive steps need the user's yes, and IT-tier steps need approval on the **Approvals** page.

## Signing and hash pinning

- A package folder is bound by SHA-256 hashes to its file, supporting files and playbook.
- The administrator signs it with an ECDSA P-256 key that never leaves the admin PC. The signature covers the package ID, the hashes, the EXE publisher and the accepted risks.
- Both the control plane and every endpoint hold only the **public** key (`FileRepository:TrustedPackageSigningKeys`, `PackageSigning:TrustedPublicKeys`). Someone who can write files on the server still cannot make an endpoint run them.
- The endpoint downloads the file into a protected staging folder and re-checks the hashes and the signature before it runs anything. An EXE also needs a valid vendor signature with the pinned publisher.
- The lint rejects BATs that elevate themselves, run encoded or embedded code, or download code. An administrator can accept the first two for one package, and that choice is signed too. Downloading code can never be accepted.

## Roles and sign-in

Every dashboard page and operator API needs a signed-in operator; anything not explicitly opened requires sign-in, so a new route cannot ship unauthenticated by accident. The dashboard and operator APIs are also **loopback-only**: remote requests to them get HTTP 403, and only the endpoint hub, health checks and artifact downloads are reachable remotely.

| Role | Can |
| --- | --- |
| **Member** | Answer and take over conversations, run read-only diagnostics, declare incidents, draft skills |
| **Reviewer** | Plus triage feature requests, publish skills, undo fixes |
| **Admin** | Plus environment map, action switches and limits, endpoint commands, maintenance mode, device revoke, enrollment codes, operator accounts |

- **Local accounts** are created by an admin with a one-time password and always work, even when the domain controller is down. Keep at least one local admin.
- **Active Directory accounts** sign in with their domain password. The control plane checks it against the domain controller and never stores it. Group mapping only chooses the starting role; an admin sets the real role on the Operators page. Off by default (`OperatorDirectory:Enabled`).
- Passwords use PBKDF2-SHA256 with 600,000 iterations. Five wrong passwords for one name from one address lock it for 15 minutes.
- The session cookie is HttpOnly and SameSite=Strict, with a 10 hour sliding lifetime. Disabling an account, changing its role or resetting its password ends its sessions at once.
- On first start the API creates `admin` with a one-time password in `initial-admin-password.txt` next to the database. Sign in, set a new password and delete the file.

## Endpoint actions: tiers, limits and the kill switch

- Tiers decide whether the user must say yes (Disruptive) and whether the agent may run an action at all (OperatorOnly).
- Hourly limits per PC and action stop loops. Defaults are 30 for Look, 3 for SafeFix and 2 for Disruptive.
- Admins can switch off any action, or change its limit, on **Capabilities** without a release.
- **Maintenance mode** (admin) makes the control plane refuse new endpoint commands with HTTP 503 while keeping connections alive. It resets when the API restarts.
- **Device revoke** (admin) invalidates one PC's token. Enrollment codes are one-time and short-lived (30 minutes by default).
- **An enrolled PC's identity cannot be borrowed.** A connection without the device token cannot register under the ID of an enrolled (or revoked) PC, and an enrollment code cannot replace a PC that is still active: revoke the old device first, then enroll again.
- Run limits for the agent itself: four concurrent conversations, one active run per conversation, a 15 minute run timeout, and a short conversation window sent to the model (`Hermes:MaxHistoryMessages`, 5 by default).
- Anything outside the catalogue needs a proposal and the user's approval card in the Tray. A proposal is single-use, stored with exact parameters and expires after 10 minutes.

## Masking data sent to the cloud LLM

Hermes runs locally, but the configured LLM provider receives each prompt, the recent conversation, tool descriptions and sanitised tool results. Before that:

- **Prompts** have known credential patterns, bearer tokens, the device's own identity values, SIDs, user names, machine names, IPv4 and IPv6 addresses and MAC addresses replaced by markers.
- **Tool results** are redacted by field name (device and machine names, user names and SIDs, paths, serial numbers, passwords, tokens). Raw script output stays in the operator trace and never goes to the model.
- **Environment-map addresses** become their labels, names under your internal DNS suffixes become `[internal-host]`, and UNC paths become `[internal-share]`.
- IP addresses are removed from system information unless you set `Privacy:ShareIpAddressesWithAgent` to `true`.
- The organisation knowledge file is masked the same way before the model sees it.

This reduces risk; it does not prove that free-form text from a user contains no personal data. Get the organisation's approval for the provider you use, set its retention terms, and do not paste logs into chats.

## Accepted risks

Decisions to know about, so nobody is surprised later.

- **Packages run unattended as LocalSystem, without asking the user.** The trust is in the administrator's review and signature. A bad package that you signed will run.
- **The LLM provider sees masked conversation text.** Masking is pattern-based, so unusual personal data in free text may get through.
- **A skill is advice, not enforcement.** A skill that gives the agent poor instructions can waste a user's time. It cannot add a power, but the allowed tools and actions can still be used in the wrong order. Reviewers are the control.
- **Legacy manifest entries cannot be signed.** Until they are moved into packages, activation signing cannot be made mandatory.
- **The dashboard is plain HTTP on loopback.** It is meant to be used on the server itself. The session cookie follows the request scheme.
- **Maintenance mode is process-local.** A restart of the API clears it.
- **Development keys** in `appsettings.Development.json` and the development package-signing key are test values. They must not exist in production.
- **The dashboard Node process is started manually.** It does not restart after a reboot.

## What to harden in production

1. **HTTPS** with a trusted certificate for the endpoint hub (`AgentSecurity:RequireHttps` stays `true`). Keep the dashboard and operator APIs on loopback; do not put them behind a reverse proxy without an explicit forwarded-header configuration.
2. **Enroll every PC, then set `AgentSecurity:RequireAuthentication` to `true`** and restart the API, so only enrolled devices connect. Revoke lost or compromised devices.
3. **Protect the data tree.** Keep `C:\ProgramData\EndpointAgents\ControlPlane` on an encrypted volume (BitLocker). The installer restricts it to SYSTEM and Administrators, but does not enable encryption. Leave `FileRepository:RequireProtectedAcl` on.
4. **Replace every development key** with production values: the Hermes API key, the MCP key (different from each other), and trusted package-signing keys. Never commit real secrets.
5. **Run Hermes as its own low-privilege service** (`NT SERVICE\EndpointAgents.Hermes`) bound to `127.0.0.1`, pinned to a tested release with no auto-update, and with skill-write approval on. Keep the disabled-toolsets list from `config.yaml.example`.
6. **Keep the allow-lists in agreement.** Tools in the control plane and in the Hermes profile must match; do not promote skills (for example `ibsng-support`) until their tools are deliberately enabled.
7. **Signing keys.** Keep the private key on an admin PC that is protected and backed up as your policy requires. Trust a second key so one lost PC does not block you. Turn on `PackageSigning:RequireForActivationRepairs` once legacy manifests are gone.
8. **Roles.** Give most people Member. Keep Admin to a few people, keep one local admin, and disable accounts of people who leave.
9. **Active Directory.** Prefer the service's own domain identity for lookups. If you add a read-only bind account, keep its password out of git.
10. **Set the LLM provider's retention terms**, keep the `Privacy` settings at their defaults, and review `ORGANIZATION.md` for secrets.
11. **Watch and test.** Review escalated cases, action runs and the Hermes and control-plane logs (never log run capabilities, device tokens or provider keys). Before rollout, run the checklist in `docs/hermes-support-agent.md`, including prompt-injection and isolation tests.

> Remember that operators are trusted people. Everything above limits what the software can do; it does not replace choosing who gets Admin.
