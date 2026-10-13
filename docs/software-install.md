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

## Adding any other application (admins and helpdesk)

One command turns a vendor installer into a package. On the admin PC:

```powershell
.scripts
ew-install-package.ps1 -Installer C:Vendor
pp.8.7.Installer.x64.exe -Id notepadpp-install `
    -App "Notepad++" -PersianSymptom "نوت‌پد پلاس پلاس روی سیستم نصب نیست"
# fill in the TODO lines in package.json (source, reviewer), then:
.scripts	est-bat-playbook.ps1 -Package <folder> -Execute     # try it on a test PC, really installs
.scripts	est-bat-playbook.ps1 -Package <folder> -Fix -Sign
```

The script refuses an installer without a valid vendor signature, copies it into the
package, and writes the BAT from `deploypackage-templatesinstall-app.bat`, so every
install package behaves the same (check, install, check again, exit 0/10/3010/1). It reads
what it needs from the installer itself:

| Installer | Recognised by | Silent install the BAT runs |
| --- | --- | --- |
| MSI | extension | `msiexec /i <file> /qn /norestart` (+ `-MsiProperties`) |
| Inno Setup EXE | version comment, setup data | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` |
| NSIS EXE | Nullsoft header | `/S` |
| WiX Burn bundle EXE | `.wixburn` section | `/quiet /norestart` |
| InstallShield EXE | InstallShield strings | `/s /v"/qn REBOOT=ReallySuppress"` |
| Squirrel EXE | Squirrel strings | `--silent` |
| Anything else | | `-SilentArguments` from the vendor's documentation |

Detection uses the installed program's name in Apps & features (the MSI's ProductName or
the EXE's product name, without version numbers), matched as a whole word at the start, so
"Git" does not count "GitHub Desktop". Override with `-Detect "<name>"` (and
`-DetectAlt` for an older name), or `-DetectPattern` for a raw `findstr` pattern.

New packages need no skill change: the agent finds any package whose `problem` starts with
`install-` by its name and symptoms. Add a row to the table in
`deploy/hermes/skills/software-install/SKILL.md` only to fix a try order (for example a
second, fallback installer for the same application), then run
`scriptssync-hermes-profile.ps1`. To prepare a package before the installer arrives, use
`-InstallerName <name>.msi` instead of `-Installer`, and place it later with
`set-package-files.ps1`.

### Installers with a wizard

Endpoints install in a hidden session where nobody can click, so every wizard step has to
be answered in advance. In order of preference:

1. **Silent switches with options.** Most wizards only choose a folder, shortcuts or
   components, and their silent mode takes those as arguments (for example Inno's
   `/DIR=` and `/COMPONENTS=`, or MSI properties). Pass them with `-SilentArguments` or
   `-MsiProperties`.
2. **Record the answers once.** For InstallShield and Inno Setup installers,
   `-RecordAnswers` runs the wizard on the admin PC, you click through it the way endpoints
   should be set up (this installs the application there), and the answers are saved into
   the package (`setup.iss` or `setup.inf`) and replayed silently on every endpoint.
3. **An MSI from the vendor.** Many vendors offer an enterprise MSI or an "admin install"
   (`setup.exe /a`) that extracts one.
4. **Repackage** it with a packaging tool (for example the MSIX Packaging Tool, or an MSI
   repackager) on a clean PC, then package the result.

A wizard with none of these cannot run unattended: the run stops as "GUI dialog blocked"
and the agent hands the user to the helpdesk.

## Known limits (first version)

- Detection runs as LocalSystem, so a copy installed only in the user's own profile is not
  seen; installing the all-users copy alongside it is harmless.
- Photos is provisioned for the PC; Windows adds it to the user at their next sign-in. If
  the user removed it themselves, a sign-out may not bring it back; then the helpdesk takes
  over.
- New fonts show up after the user signs out and back in.
- 64-bit Windows 10 and 11 only.
