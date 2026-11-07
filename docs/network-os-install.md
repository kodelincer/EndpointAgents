# Network OS installation (design, not built)

Idea P on the Ideas page. A new or empty PC starts from the network, installs Windows from an
approved image, gets its drivers, joins the domain, gets the endpoint agent, and then its
software through the normal install packages. This page is the design. Nothing is built yet,
because it needs a PXE server and a spare PC (or a VM) that can be wiped many times: it cannot
be tested on a PC someone works on.

## Rules it keeps

- **Fixed steps only.** Every step is a catalogued, typed step with fixed options, like the
  endpoint actions. No free scripts, no command line typed by a person or chosen by the AI.
- **Signed and hash-pinned files.** The Windows image (WIM), driver packs, the deploy client and
  the agent installer come from the control plane's file repository with SHA-256 pins and the
  administrator's signature, like packages.
- **No AI needed.** The build is chosen by fixed rules (PC model gives the driver pack, the
  department or a dashboard choice gives the software list). If the AI is ever used here, it may
  only pick one of the approved build profiles, never a disk or install command.
- **Nothing is wiped without a person.** A PC is installed only after an IT member approves it
  on the dashboard by its serial number or MAC address, and the screen on the PC shows a final
  "this erases the disk" confirmation.
- **No domain password on the PC.** The control plane makes an offline domain-join blob
  (`djoin /provision`) for each approved PC; the PC only gets that blob.
- **Windows 10 and 11**, UEFI first; legacy BIOS only if old models still need it.
- **No Microsoft 365, Intune or Autopilot.** Activation uses the KMS client key and the
  organisation's KMS server, as today.

## Pieces

| Piece | What it is |
| --- | --- |
| PXE boot | Windows Deployment Services (a free Windows Server role) or a small TFTP/HTTP server (iPXE). DHCP options 66/67, or an IP helper on the switches, point new PCs to it. |
| Boot image | A Windows PE image with network and storage drivers and the deploy client. Built once per Windows ADK version with a fixed build script kept in the repo. |
| Deploy client | A small signed program inside Windows PE. It talks to the control plane over HTTPS, shows Persian progress on screen, and runs the fixed steps below. It has no shell and accepts no commands. |
| Build profiles | Made on a new dashboard page: Windows edition and image, disk layout, language and time zone, computer-name pattern, OU, and the install packages for the first sign-in. |
| Driver map | The Environment page's driver catalog extended with driver packs per PC model (`Win32_ComputerSystemProduct` model and SKU). |

## The fixed steps

1. **Identify** the PC: model, serial number, MAC address, disk size, firmware (UEFI or BIOS).
   The control plane answers with the approved build profile, or "waiting for approval".
2. **Partition** with one of the fixed layouts (UEFI: EFI, MSR, Windows, Recovery). The layout
   is chosen by name; sizes are fixed in code.
3. **Apply the image** (`DISM /Apply-Image`) after checking the WIM's SHA-256 against the pin.
4. **Add drivers** from the model's driver pack (`DISM /Add-Driver`), checked by hash.
5. **Write the answer file** from a fixed template filled with the profile's values: computer
   name, locale fa-IR and en-US, time zone, the offline domain-join blob, KMS client key.
6. **Install the endpoint agent** offline into the image (service, Tray, enrollment token
   bound to that serial number, valid for 24 hours).
7. **Restart into Windows.** Windows finishes setup, joins the domain, and the agent connects.
8. **First software** comes through the normal install packages (Chrome, fonts, Office, Foxit and
   the profile's list), with their requirements checks.

Every step reports its result to the dashboard, which shows each PC's progress and the step
that failed. A failed step stops the build; it never "tries something else" on its own.

## Build order

1. **USB first.** A Windows PE USB stick with the deploy client and one build profile, to prove
   the steps on a spare PC without touching DHCP.
2. **PXE.** WDS or iPXE on the server, approval of new PCs on the dashboard.
3. **Driver map and profiles** on the dashboard, progress per PC.
4. **Re-install an existing PC** from the dashboard, with a warning that the user's files must be
   copied first (and a check that OneDrive or a file share copy is not assumed: we have neither).

## What the team must decide first

- Windows Server with WDS, or a Linux TFTP/HTTP server next to the control plane.
- Who can change DHCP options or IP helpers on the network.
- Which Windows images are approved (edition, build, language), and who builds them.
- A spare PC or a VM host for testing; the steps erase the disk every time.
