# Managed Windows activation repair

`system.windows_activation_status` checks the Windows edition, OS build, and license
state. For an activation problem, Hermes selects a repair from
`compatibleBugfixArtifactIds`. A licensed machine does not run an activation BAT.
After execution, the endpoint checks the license again; exit code `0` alone does not
mean the issue is resolved.

## Versioned repair catalog

The protected Control Plane repository contains `Windows/windows-bugfix-managers.json`
and one BAT per manifest entry in the same `Windows` directory. An entry is published
only when the BAT and every declared support file match their SHA-256 values.

```json
{
  "version": 1,
  "artifacts": [
    {
      "artifactId": "windows-11-pro-repair",
      "displayName": "Windows 11 Pro activation repair",
      "description": "Reviewed repair for this OS range and edition.",
      "fileName": "windows-11-pro-repair.bat",
      "sha256": "REPLACE_WITH_BAT_SHA256",
      "minWindowsVersion": "10.0.22000",
      "maxWindowsVersionExclusive": "10.0.30000",
      "editionIds": ["Professional"],
      "additionalCompatibility": [
        {
          "minWindowsVersion": "10.0.19041",
          "maxWindowsVersionExclusive": "10.0.19046",
          "editionIds": ["Professional"]
        }
      ],
      "supportFiles": [
        { "fileName": "helper.txt", "sha256": "REPLACE_WITH_HELPER_SHA256" }
      ],
      "playbook": {
        "version": 1,
        "steps": [
          { "expect": "Select an item:", "send": "1", "submit": "line", "timeoutSeconds": 30 },
          { "expect": "Continue [Y/N]:", "send": "Y", "submit": "key", "timeoutSeconds": 30 }
        ]
      }
    }
  ]
}
```

Store the example's helper at
`Windows/windows-11-pro-repair.files/helper.txt`. Support files are flat names in a
dedicated directory; the endpoint downloads them beside the BAT and verifies each
hash before execution. The `supportFiles` array may be empty. Version minimums are
inclusive and maximums exclusive. An empty `editionIds` list accepts any edition in
the OS range. Use an exact edition ID when a repair is edition-specific.
`additionalCompatibility` adds up to 31 more build/edition rules to the primary
rule. One complete rule must match; the same BAT, support files, and playbook are
used for every rule. These bounds are illustrative and require validation for the
specific reviewed BAT. Catalog rows are sorted by their earliest supported Windows
version. Older endpoint binaries see only the primary rule, so update them before
relying on the added ranges.

For every BAT, record a separate ordered playbook of literal prompt fragments and
fixed responses. `submit: "line"` sends the response and Enter to `set /p`;
`submit: "key"` sends one character to `choice`. The prompt must appear before the
BAT reads stdin. For reliable `set /p` automation, emit the prompt first:

```bat
<nul set /p "=Select an item: "
>nul ping -n 2 127.0.0.1
set /p "item="
```

An inline `set /p "item=Select an item: "` prompt can read an empty response through
redirected input before the agent writes to the pipe. A short pause after the prompt
gives the runner time to deliver its response. Test each menu transition in a
disposable Windows VM before publishing its manifest entry. BATs that demand UAC,
credentials, GUI input, or detached console windows are outside this playbook runner.

Hermes chooses from the endpoint's compatible IDs. If more than one ID matches, the
Windows support skill stops for an administrator selection rule. If versioned entries
exist but none match, the legacy unversioned BAT is not offered or run. The old
`Windows/windowsbugfixer.bat` path remains a migration fallback only when the catalog
has no valid versioned repair.

The service downloads the selected BAT and support files through its authenticated
Control Plane connection, stages them under protected, content-addressed paths, and
runs the approved playbook as LocalSystem. Automatic chat dispatch is pinned to the
current BAT, bundle, and playbook hashes. Direct operator dispatch still requires Tray
consent. The Windows support skill is currently limited to **activation problems**;
other Windows problems need their own diagnosis, selection policy, and post-check.

## Local test

The harmless two-menu Windows fixture and support-file check run on this PC with:

```powershell
dotnet test .\src\EndpointAgents.Platform.Windows.Tests\EndpointAgents.Platform.Windows.Tests.csproj -c Release --filter FullyQualifiedName~WindowsRunnerAnswersMenuAndVerifiesSupportingFile
```

This runs a temporary BAT that only checks its inputs and adjacent helper file. To
pilot an actual versioned repair, install the updated API and endpoint binaries, put
the reviewed BAT and support files in the protected repository, add their exact hashes
and compatibility range to the manifest, and check the endpoint's reported compatible
IDs before allowing Hermes to call `run_windows_activation_repair`.

The existing `deploy/control-plane/FileRepository/Windows/windowsbugfixer.bat` is a
legacy development sample. It starts the licensing service and invokes Microsoft's
activation command for an already-installed license. It is not the harmless menu
fixture. The production installer script for the legacy BAT remains
`scripts/install-windows-activation-repair.ps1`.
