# Installing applications for users

When a user asks for an application ("می‌خواهم کروم برایم نصب شود"), the support agent
follows the `software-install` skill (`deploy/hermes/skills/software-install/SKILL.md`). It
runs the matching **install package**, which is an ordinary catalogued package (see
[bat-packages.md](bat-packages.md)): a reviewed BAT plus the vendor's installer, both
hash-pinned and signed by an administrator, run by the endpoint service as LocalSystem with
no Tray approval prompt. The agent never chooses commands or files; it only picks which
package to run.

Each install BAT works the same way:

1. **Check first.** If the application is already installed it changes nothing and exits 10.
2. **Install silently for all users** from the installer that the endpoint downloaded from
   the control plane's file repository (and verified against the pinned SHA-256).
3. **Check again**, then exit 0 (installed), 3010 (installed; the user signs out and back
   in, or restarts), or print a line starting with `ERROR:` and exit 1. Logs go to
   `%ProgramData%\EndpointAgents\Logs\<package>.log` on the user's PC.

After the run the agent asks whether it works. On "no", a failed run, or an installer that
is not ready yet, it escalates the conversation to the helpdesk, where it shows as unsolved.

## The packages

| Package | Installs | How | Installer in git? | State |
| --- | --- | --- | --- | --- |
| `chrome-install` | Google Chrome | Google's enterprise MSI, `msiexec /qn` | No, see [chrome-install.md](chrome-install.md) | Ready to sign |
| `persian-fonts-install` | Vazirmatn (9 weights, Latin and Persian digits) | Copies each `.ttf`/`.otf` to `C:\Windows\Fonts` and registers it | Yes (SIL OFL fonts) | Ready to sign |
| `adobe-connect-install` | Adobe Connect desktop app | Adobe's MSI, `msiexec /qn` | No | Needs `AdobeConnect.msi` |
| `foxit-reader-install` | Foxit PDF Reader | Foxit's MSI, `msiexec /qn` | No | Needs `FoxitPDFReader.msi` |
| `foxit-editor-install` | Foxit PDF Editor (no activation) | Foxit's MSI, `msiexec /qn` | No | Needs `FoxitPDFEditor.msi` |
| `photos-install` | Microsoft Photos | `DISM /Add-ProvisionedAppxPackage` with its dependencies | No | Needs the Store bundle |

The Packages tab of the dashboard shows each one as **Hash mismatch** (installer missing)
or **Not signed** until it is ready.

## Putting an installer in place

On the admin PC, with your package signing key (`scripts\new-package-signing-key.ps1`):

```powershell
.\scripts\set-package-files.ps1 -Id foxit-reader-install -File C:\Downloads\FoxitPDFReader20252_enu_Setup.msi -Development -Sign
```

The script checks the installer's vendor signature (and prints the signer), copies it into
the package under the name the BAT expects (`FoxitPDFReader.msi` here), lists it in
`package.json`, pins the hashes and signs. Drop `-Development` to work on the server's
`C:\ProgramData\EndpointAgents\ControlPlane\FileRepository`. Run it again for a newer
version. Use `.\scripts\test-bat-playbook.ps1 -Package <folder> -Sign` for a package whose
files are already there (the font pack).

Where to get each installer:

- **Adobe Connect**: the Adobe Connect application MSI for enterprise deployment, from
  Adobe's Connect downloads page.
- **Foxit PDF Reader / Editor**: the MSI package from Foxit's enterprise download page
  (choose "MSI"). The Editor still needs the organization's license afterwards; the package
  does not activate it and the agent never asks for keys.
- **Microsoft Photos**: on a PC signed in with a work account,
  `winget download --id 9WZDNCRFJBH4 --source msstore` saves the bundle, its dependency
  packages and a license file. Pass all of them, replacing what is there:

  ```powershell
  .\scripts\set-package-files.ps1 -Id photos-install -Replace -Development -Sign -File `
      <bundle>.msixbundle, <dependency>.appx, <dependency>.msix, <license>.xml
  ```

  The bundle must end up as `Microsoft.Windows.Photos.msixbundle` (the script renames the
  only `.msixbundle` automatically) and the license as `Microsoft.Windows.Photos-license.xml`
  (use `-As` on its own run if needed). Without a license the BAT uses `/SkipLicense`.
- **More Persian fonts**: add `.ttf` or `.otf` files with
  `set-package-files.ps1 -Id persian-fonts-install -File <fonts> -Sign`, and only fonts
  whose license allows redistribution.

## Adding another application

1. Copy one of the MSI packages (for example `foxit-reader-install`) to a new folder named
   `<app>-install`, rename its BAT to match, and change the header lines: `APP`, `MSI`,
   `DETECT`/`DETECT_ALT` (a `findstr` pattern for the `DisplayName` in the Uninstall registry
   keys), `MSIPROPS`, and the log name. Update `package.json` (`id`, `problem: install-<app>`,
   names, symptoms, success marker, supporting file name).
2. Add a row to the catalog table in `deploy/hermes/skills/software-install/SKILL.md` and
   the id to `InstallPackages` in `RepairPackageLoaderTests`, then run
   `scripts\sync-hermes-profile.ps1`.
3. Place the installer and sign with `set-package-files.ps1`.

An installer that has no silent mode and needs someone to click through a wizard cannot run
unattended (the run stops as "GUI dialog blocked"). Ask the vendor for an MSI or silent
switches first; such applications stay with the helpdesk until then.

## Known limits (first version)

- Detection runs as LocalSystem, so a copy installed only in the user's own profile is not
  seen; installing the all-users copy alongside it is harmless.
- Photos is provisioned for the PC; Windows adds it to the user at their next sign-in. If
  the user removed it themselves, a sign-out may not bring it back; then the helpdesk takes
  over.
- New fonts show up after the user signs out and back in.
- 64-bit Windows 10 and 11 only.
