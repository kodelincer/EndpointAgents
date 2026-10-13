namespace EndpointAgents.Shared.Actions;

/// <summary>Organisation standards an administrator sets in the dashboard environment map.</summary>
public static class ActionSettings
{
    /// <summary>none, pac or server.</summary>
    public const string ProxyMode = "proxy.mode";
    public const string ProxyPacUrl = "proxy.pacUrl";
    /// <summary>host:port</summary>
    public const string ProxyServer = "proxy.server";
    /// <summary>Semicolon list, e.g. "*.corp.local;&lt;local&gt;".</summary>
    public const string ProxyBypass = "proxy.bypass";
    /// <summary>Windows time zone id, e.g. "Iran Standard Time".</summary>
    public const string StandardTimeZone = "time.standardZone";
    /// <summary>Comma-separated SHA-1 thumbprints of the organisation's root CAs.</summary>
    public const string RootCaThumbprints = "trust.rootThumbprints";
    /// <summary>Comma-separated internal DNS suffixes; names under them are masked for the cloud model.</summary>
    public const string InternalDnsSuffixes = "network.internalSuffixes";

    public static IReadOnlyList<string> All { get; } =
        [ProxyMode, ProxyPacUrl, ProxyServer, ProxyBypass, StandardTimeZone,
         RootCaThumbprints, InternalDnsSuffixes];
}

public static class ActionAreas
{
    public const string Network = "network";
    public const string Domain = "domain";
    public const string Printers = "printers";
    public const string Drives = "drives";
    public const string Browsers = "browsers";
    public const string Office = "office";
    public const string Health = "health";
    public const string Desktop = "desktop";

    public static IReadOnlyList<string> All { get; } =
        [Network, Domain, Printers, Drives, Browsers, Office, Health, Desktop];
}

/// <summary>
/// Every general endpoint action. Adding an action means: a definition here, an
/// implementation on the endpoint (system or user side), a mention in a Hermes skill, and
/// tests. The control plane builds the agent's action list, the kill switches and the
/// dashboard's capabilities page from this list, so they never drift apart.
/// </summary>
public static class EndpointActionCatalog
{
    private const string AnyHostTarget = "Environment-map target id (see list_environment_targets).";

    private static readonly string[] HostKinds =
        [EnvironmentTargetKinds.Host, EnvironmentTargetKinds.WebApp,
         EnvironmentTargetKinds.Share, EnvironmentTargetKinds.Printer];

    /// <summary>Services the agent may look at.</summary>
    public static IReadOnlyList<ActionChoice> ServiceChoices { get; } =
    [
        new("Spooler", "Print Spooler"),
        new("W32Time", "Windows Time"),
        new("BITS", "Background Intelligent Transfer"),
        new("wuauserv", "Windows Update"),
        new("WSearch", "Windows Search"),
        new("Audiosrv", "Windows Audio"),
        new("AudioEndpointBuilder", "Windows Audio Endpoint Builder"),
        new("stisvc", "Windows Image Acquisition (scanners)"),
        new("bthserv", "Bluetooth Support"),
        new("Dnscache", "DNS Client"),
        new("Dhcp", "DHCP Client"),
        new("LanmanWorkstation", "Workstation (shared folders)"),
        new("Netlogon", "Netlogon (domain sign-in)"),
        new("WinDefend", "Microsoft Defender Antivirus"),
        new("mpssvc", "Windows Defender Firewall"),
        new("RasMan", "Remote Access Connection Manager (VPN)")
    ];

    /// <summary>Services the agent may restart: harmless to restart while the user works.</summary>
    public static IReadOnlyList<ActionChoice> RestartableServiceChoices { get; } =
    [
        new("Spooler", "Print Spooler"),
        new("W32Time", "Windows Time"),
        new("BITS", "Background Intelligent Transfer"),
        new("wuauserv", "Windows Update"),
        new("WSearch", "Windows Search"),
        new("Audiosrv", "Windows Audio"),
        new("AudioEndpointBuilder", "Windows Audio Endpoint Builder"),
        new("stisvc", "Windows Image Acquisition (scanners)"),
        new("bthserv", "Bluetooth Support")
    ];

    public static IReadOnlyList<ActionChoice> AppChoices { get; } =
    [
        new("outlook", "Outlook"),
        new("word", "Word"),
        new("excel", "Excel"),
        new("powerpoint", "PowerPoint"),
        new("teams", "Teams"),
        new("chrome", "Google Chrome"),
        new("edge", "Microsoft Edge"),
        new("firefox", "Firefox")
    ];

    public static IReadOnlyList<ActionChoice> BrowserChoices { get; } =
    [
        new("chrome", "Google Chrome"),
        new("edge", "Microsoft Edge"),
        new("firefox", "Firefox")
    ];

    public static IReadOnlyList<ActionChoice> EventAreaChoices { get; } =
    [
        new("crashes", "Application crashes and hangs"),
        new("bluescreen", "Blue screens and unexpected shutdowns"),
        new("disk", "Disk and file-system errors"),
        new("network", "Network, DNS and DHCP errors"),
        new("grouppolicy", "Group Policy errors"),
        new("printing", "Printing errors"),
        new("updates", "Windows Update failures"),
        new("signin", "Sign-in and domain trust errors")
    ];

    public static IReadOnlyList<ActionChoice> LayoutChoices { get; } =
    [
        new("persian", "Persian (فارسی)"),
        new("persian-standard", "Persian Standard"),
        new("english-us", "English (US)")
    ];

    public static IReadOnlyList<EndpointActionDefinition> All { get; } = Build();

    public static EndpointActionDefinition? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(item => item.Id == id);

    private static ActionParameter Target(string description, params string[] kinds) =>
        new("target", ActionParameterKind.Target, description, TargetKinds: kinds);

    private static EndpointActionDefinition[] Build() =>
    [
        // ---------- Network ----------
        new("network.status", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Network status", "وضعیت شبکه",
            "Quick health of the connected network adapters: link, address type (DHCP, static, APIPA), gateway answer and DNS servers present.",
            "Adapters with Connected, AddressKind, GatewayReachable, DnsServerCount; Summary with HasWorkingAdapter, HasApipaAddress, GatewayReachable.",
            [], TimeoutSeconds: 20,
            ExampleResult: """{"Summary":{"HasWorkingAdapter":true,"HasApipaAddress":false,"GatewayReachable":true}}"""),
        new("network.triage", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Network triage to a target", "عیب‌یابی لایه‌ای شبکه",
            "Walks the layers to one internal target and stops at the first broken one: adapter, IP address, gateway, DNS, TCP port, then HTTP(S) for web apps.",
            "Layers in order with Passed and Detail; FirstFailedLayer (Adapter, Address, Gateway, Dns, Port, Http) or null when everything works.",
            [Target(AnyHostTarget, EnvironmentTargetKinds.Host, EnvironmentTargetKinds.WebApp,
                EnvironmentTargetKinds.Share, EnvironmentTargetKinds.Printer)],
            TimeoutSeconds: 45,
            ExampleResult: """{"FirstFailedLayer":"Dns","Layers":[{"Layer":"Adapter","Passed":true},{"Layer":"Address","Passed":true},{"Layer":"Gateway","Passed":true},{"Layer":"Dns","Passed":false,"Detail":"Name not found"}]}"""),
        new("dns.resolve", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Resolve a name", "بررسی DNS یک نام",
            "Resolves a target's name with Windows and separately asks each configured DNS server, then compares with the expected address from the environment map.",
            "WindowsAnswer, PerServer answers, MatchesExpected, Disagreement (servers answer differently).",
            [Target(AnyHostTarget, HostKinds)], TimeoutSeconds: 20),
        new("net.test_port", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Test a TCP port", "تست پورت",
            "Opens a TCP connection to a target on one of its listed ports and measures the time.",
            "Reachable, ElapsedMs, Error.",
            [Target(AnyHostTarget, EnvironmentTargetKinds.Host, EnvironmentTargetKinds.WebApp),
             new("port", ActionParameterKind.Integer, "A port listed for this target.", Min: 1, Max: 65535)],
            TimeoutSeconds: 15),
        new("net.ping", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Ping a target", "پینگ",
            "Sends four pings to a target.",
            "Sent, Received, AverageMs.",
            [Target(AnyHostTarget, HostKinds)], TimeoutSeconds: 15),
        new("web.probe", ActionAreas.Network, ActionTier.Look, ActionRunAs.User,
            "Open an internal web app", "بررسی وب‌اپ داخلی",
            "Requests a web app as the signed-in user, through the user's proxy and Windows sign-in, without following more than five redirects.",
            "StatusCode, Reachable, AuthenticationRequired, Certificate (Valid, Errors, ExpiresUtc), UsedProxy, ElapsedMs, Error.",
            [Target(AnyHostTarget, EnvironmentTargetKinds.WebApp)], TimeoutSeconds: 30),
        new("proxy.get", ActionAreas.Network, ActionTier.Look, ActionRunAs.User,
            "Read proxy settings", "تنظیمات پروکسی",
            "Reads the user's proxy settings and compares them with the organisation standard.",
            "Enabled, Server, AutoConfigUrl, AutoDetect, Bypass, MatchesStandard.",
            [], UsesSettings: [ActionSettings.ProxyMode, ActionSettings.ProxyPacUrl,
                ActionSettings.ProxyServer, ActionSettings.ProxyBypass]),
        new("hosts.check", ActionAreas.Network, ActionTier.Look, ActionRunAs.System,
            "Check the hosts file", "بررسی فایل hosts",
            "Reports whether the hosts file has entries beyond the Windows default, and whether any of them override an environment-map target.",
            "EntryCount, OverridesTargets (target ids).",
            []),
        new("dns.flush", ActionAreas.Network, ActionTier.SafeFix, ActionRunAs.System,
            "Clear the DNS cache", "پاک کردن کش DNS",
            "Clears Windows' DNS client cache so names are looked up again.",
            "Done.", [], MaxPerHour: 5),
        new("dhcp.renew", ActionAreas.Network, ActionTier.SafeFix, ActionRunAs.System,
            "Renew the IP address", "تمدید آدرس IP",
            "Asks the DHCP server for a fresh address on every DHCP adapter. The connection is not dropped first.",
            "Done; the verification shows the network status before and after.",
            [], TimeoutSeconds: 60, Verify: new("network.status")),
        new("net.register_dns", ActionAreas.Network, ActionTier.SafeFix, ActionRunAs.System,
            "Re-register in DNS", "ثبت دوباره در DNS",
            "Re-registers this PC's name and address with the DNS server.",
            "Done.", [], TimeoutSeconds: 30),
        new("arp.clear", ActionAreas.Network, ActionTier.SafeFix, ActionRunAs.System,
            "Clear the ARP cache", "پاک کردن کش ARP",
            "Clears the cached addresses of neighbouring devices.",
            "Done.", []),
        new("proxy.reset_to_standard", ActionAreas.Network, ActionTier.SafeFix, ActionRunAs.User,
            "Reset proxy to the standard", "بازگرداندن پروکسی به استاندارد",
            "Sets the user's proxy to the organisation standard. The old settings are saved so the helpdesk can undo it.",
            "Done with the new settings, or NotNeeded.",
            [], Verify: new("proxy.get"), UndoActionId: "proxy.restore",
            UsesSettings: [ActionSettings.ProxyMode, ActionSettings.ProxyPacUrl,
                ActionSettings.ProxyServer, ActionSettings.ProxyBypass]),

        // ---------- Domain and sign-in ----------
        new("domain.health", ActionAreas.Domain, ActionTier.Look, ActionRunAs.System,
            "Domain health", "سلامت دامین",
            "Checks the computer's trust with the domain (secure channel), finds a domain controller, tests its sign-in ports (88, 389, 445, 135), the clock difference to it, and when Group Policy last applied.",
            "Joined, SecureChannelOk, DomainControllerFound, Ports, ClockOffsetSeconds, ClockOk (under 5 minutes), LastPolicyRefreshUtc, PolicyErrorsLast24h.",
            [], TimeoutSeconds: 45),
        new("time.status", ActionAreas.Domain, ActionTier.Look, ActionRunAs.System,
            "Clock status", "وضعیت ساعت",
            "Local time, time zone, the time service state and the clock difference to the domain controller.",
            "TimeZone, MatchesStandardZone, TimeServiceRunning, ClockOffsetSeconds, ClockOk.",
            [], TimeoutSeconds: 20, UsesSettings: [ActionSettings.StandardTimeZone]),
        new("time.resync", ActionAreas.Domain, ActionTier.SafeFix, ActionRunAs.System,
            "Sync the clock", "همگام‌سازی ساعت",
            "Starts the Windows Time service if needed and syncs the clock with the domain.",
            "Done.", [], TimeoutSeconds: 45, Verify: new("time.status")),
        new("gpo.refresh_computer", ActionAreas.Domain, ActionTier.SafeFix, ActionRunAs.System,
            "Refresh computer policy", "به‌روزرسانی سیاست‌های کامپیوتر",
            "Re-applies computer Group Policy. Never signs the user out or restarts.",
            "Done or Failed with the reason.", [], TimeoutSeconds: 150),
        new("gpo.refresh_user", ActionAreas.Domain, ActionTier.SafeFix, ActionRunAs.User,
            "Refresh user policy", "به‌روزرسانی سیاست‌های کاربر",
            "Re-applies the signed-in user's Group Policy (drive maps, printers, settings). Never signs the user out.",
            "Done or Failed with the reason.", [], TimeoutSeconds: 150),
        new("kerberos.purge", ActionAreas.Domain, ActionTier.SafeFix, ActionRunAs.User,
            "Renew sign-in tickets", "تمدید تیکت‌های ورود",
            "Clears the user's Kerberos tickets so Windows gets fresh ones (fixes access denied right after a group or password change).",
            "Done.", []),
        new("credman.list", ActionAreas.Domain, ActionTier.Look, ActionRunAs.User,
            "Saved Windows passwords", "رمزهای ذخیره‌شده ویندوز",
            "Lists the user's saved Windows credentials (only which server they are for and when they were saved, never the secret).",
            "Credentials with Ref, Kind, MatchesTarget, SavedUtc. Pass Ref to credman.remove.",
            []),
        new("credman.remove", ActionAreas.Domain, ActionTier.SafeFix, ActionRunAs.User,
            "Remove a stale saved password", "حذف رمز ذخیره‌شده قدیمی",
            "Removes one saved Windows (domain) credential for a server, so the old password stops being tried.",
            "Done or NotNeeded.",
            [new("ref", ActionParameterKind.LocalItem, "Ref from credman.list.")]),

        // ---------- Printers ----------
        new("printer.list", ActionAreas.Printers, ActionTier.Look, ActionRunAs.User,
            "Installed printers", "چاپگرهای نصب‌شده",
            "Lists the user's printers: default, network or local, status, stuck jobs, and the matching environment-map printer.",
            "Printers with Name, IsDefault, IsNetwork, Status, JobCount, ErrorJobs, MatchesTarget.",
            []),
        new("spooler.restart_and_clear", ActionAreas.Printers, ActionTier.SafeFix, ActionRunAs.System,
            "Clear stuck print jobs", "پاک کردن صف چاپ",
            "Stops the print service, deletes stuck print jobs for everyone on this PC, and starts it again.",
            "Done with RemovedJobFiles.", [], TimeoutSeconds: 60,
            Verify: new("services.status", new Dictionary<string, string> { ["service"] = "Spooler" })),
        new("printer.set_default", ActionAreas.Printers, ActionTier.SafeFix, ActionRunAs.User,
            "Set the default printer", "تنظیم چاپگر پیش‌فرض",
            "Makes one of the user's installed printers the default.",
            "Done or NotNeeded.",
            [new("printer", ActionParameterKind.LocalItem, "Printer Name from printer.list.")],
            UndoActionId: "printer.restore_default"),
        new("printer.add", ActionAreas.Printers, ActionTier.SafeFix, ActionRunAs.User,
            "Connect a network printer", "اتصال چاپگر شبکه",
            "Connects a print-server printer from the environment map for the signed-in user.",
            "Done, NotNeeded (already connected) or Failed with the Windows reason.",
            [Target("Printer target id.", EnvironmentTargetKinds.Printer)], TimeoutSeconds: 120,
            Verify: new("printer.list")),
        new("printer.remove", ActionAreas.Printers, ActionTier.SafeFix, ActionRunAs.User,
            "Remove a broken printer connection", "حذف اتصال چاپگر خراب",
            "Removes one network printer connection of the user (local printers are never removed).",
            "Done, NotNeeded or Blocked (not a network printer).",
            [new("printer", ActionParameterKind.LocalItem, "Printer Name from printer.list.")],
            TimeoutSeconds: 60),
        new("printer.test_page", ActionAreas.Printers, ActionTier.SafeFix, ActionRunAs.User,
            "Print a test page", "چاپ صفحه آزمایشی",
            "Sends the Windows test page to one installed printer.",
            "Done or Failed.",
            [new("printer", ActionParameterKind.LocalItem, "Printer Name from printer.list.")],
            MaxPerHour: 3),

        // ---------- Shared drives and profile ----------
        new("drives.list", ActionAreas.Drives, ActionTier.Look, ActionRunAs.User,
            "Mapped drives", "درایوهای شبکه",
            "Lists the user's mapped network drives, whether each is connected, and the matching environment-map share.",
            "Drives with Letter, Connected, MatchesTarget; MissingStandardDrives (share target ids with a standard letter that are not mapped).",
            []),
        new("share.test", ActionAreas.Drives, ActionTier.Look, ActionRunAs.User,
            "Test access to a shared folder", "تست دسترسی پوشه اشتراکی",
            "Checks whether the user can reach and open a shared folder (never lists its contents).",
            "ServerReachable (port 445), Accessible, Error (AccessDenied, NotFound, Unreachable).",
            [Target("Share target id.", EnvironmentTargetKinds.Share)], TimeoutSeconds: 30),
        new("drive.remap", ActionAreas.Drives, ActionTier.SafeFix, ActionRunAs.User,
            "Map a shared drive", "اتصال درایو شبکه",
            "Maps a share from the environment map to its standard letter. A different network drive on that letter is disconnected first; a local disk is never touched.",
            "Done, NotNeeded or Failed with the Windows reason.",
            [Target("Share target id with a drive letter.", EnvironmentTargetKinds.Share)],
            TimeoutSeconds: 60, Verify: new("drives.list")),
        new("profile.status", ActionAreas.Drives, ActionTier.Look, ActionRunAs.User,
            "User profile status", "وضعیت پروفایل کاربر",
            "Detects a temporary profile, folder redirection (Desktop, Documents on a server) and OneDrive known-folder backup.",
            "TemporaryProfile, RedirectedFolders, OneDriveRunning, ProfileSizeGb.",
            []),

        // ---------- Browsers and certificates ----------
        new("browser.inventory", ActionAreas.Browsers, ActionTier.Look, ActionRunAs.User,
            "Browsers", "مرورگرها",
            "Installed browsers and versions, the default browser, and whether each is open.",
            "Browsers with Name, Version, Running; DefaultBrowser.",
            []),
        new("browser.clear_cache", ActionAreas.Browsers, ActionTier.SafeFix, ActionRunAs.User,
            "Clear browser cache", "پاک کردن کش مرورگر",
            "Clears only the cache of one browser (never passwords, history, bookmarks or cookies). The browser must be closed.",
            "Done with FreedMb, or Blocked when the browser is open.",
            [new("browser", ActionParameterKind.Choice, "Which browser.", Choices: BrowserChoices)],
            TimeoutSeconds: 60),
        new("cert.check_trust", ActionAreas.Browsers, ActionTier.Look, ActionRunAs.System,
            "Organisation certificates", "گواهی‌های سازمان",
            "Checks that the organisation's root certificates (from the environment map) are trusted on this PC.",
            "Expected, Missing (thumbprints), Expired.",
            [], UsesSettings: [ActionSettings.RootCaThumbprints]),

        // ---------- Office ----------
        new("outlook.status", ActionAreas.Office, ActionTier.Look, ActionRunAs.User,
            "Outlook status", "وضعیت Outlook",
            "Outlook profiles, data file sizes (OST/PST), cached mode and add-ins that are disabled or slow.",
            "Running, ProfileCount, DataFiles (Kind, SizeGb, NearLimit), Addins (Name, LoadBehavior, Active).",
            []),
        new("teams.clear_cache", ActionAreas.Office, ActionTier.SafeFix, ActionRunAs.User,
            "Clear Teams cache", "پاک کردن کش Teams",
            "Clears the cache of classic and new Teams. Teams must be closed; the user may need to sign in again.",
            "Done with FreedMb, or Blocked when Teams is open.", [], TimeoutSeconds: 60),

        // ---------- Health and Windows ----------
        new("system.recent_changes", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "What changed recently", "تغییرات اخیر",
            "Software installed or removed, Windows updates, driver installs, restarts, unexpected shutdowns and blue screens in the last days.",
            "Changes ordered newest first with WhenUtc, Kind, Name.",
            [new("days", ActionParameterKind.Integer, "How many days back (1-30).", Required: false, Min: 1, Max: 30)],
            TimeoutSeconds: 45),
        new("perf.snapshot", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Performance snapshot", "وضعیت کارایی",
            "CPU, memory and system-disk use now, the busiest programs by name, uptime and number of startup programs.",
            "CpuPercent, MemoryLoadPercent, SystemDriveFreeGb, TopByCpu, TopByMemory, UptimeDays, StartupPrograms.",
            [], TimeoutSeconds: 30),
        new("events.summary", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Error summary", "خلاصه خطاهای ویندوز",
            "Counts recent Windows errors of one kind from fixed event sources (last 7 days), grouped, with one short sample each.",
            "Groups with Source, EventId, Count, LastUtc, Sample.",
            [new("area", ActionParameterKind.Choice, "Kind of errors.", Choices: EventAreaChoices)],
            TimeoutSeconds: 45),
        new("services.status", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Service status", "وضعیت سرویس‌ها",
            "State and start type of one support-relevant Windows service, or all of them.",
            "Services with Name, State, StartType.",
            [new("service", ActionParameterKind.Choice, "Service name; omit for all.", Required: false, Choices: ServiceChoices)]),
        new("service.restart", ActionAreas.Health, ActionTier.SafeFix, ActionRunAs.System,
            "Restart a service", "راه‌اندازی دوباره سرویس",
            "Restarts one Windows service from a short list that is safe to restart while the user works.",
            "Done with the new state.",
            [new("service", ActionParameterKind.Choice, "Service name.", Choices: RestartableServiceChoices)],
            TimeoutSeconds: 60,
            Verify: new("services.status", PassSameArguments: true)),
        new("windows_update.status", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Windows Update status", "وضعیت به‌روزرسانی ویندوز",
            "Last installed update, recent failures and whether a restart is pending.",
            "LastInstalledUtc, RecentFailures, RecentSuccesses, RestartPending.",
            [], TimeoutSeconds: 45),
        new("security.status", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Security status", "وضعیت امنیتی",
            "Defender on, real-time protection, signature age, last scan; firewall per profile; BitLocker on the system drive (never keys).",
            "Defender, Firewall, BitLocker.",
            [], TimeoutSeconds: 30),
        new("defender.quick_scan", ActionAreas.Health, ActionTier.SafeFix, ActionRunAs.System,
            "Start a quick virus scan", "اسکن سریع ویروس",
            "Starts a Microsoft Defender quick scan in the background.",
            "Done (started).", [], MaxPerHour: 1),
        new("devices.problems", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Device problems", "مشکلات سخت‌افزار",
            "Devices that Windows reports with a problem (camera, audio, USB, display, Bluetooth).",
            "Devices with Name, Class, ProblemCode, Problem.",
            [], TimeoutSeconds: 30),
        new("devices.rescan", ActionAreas.Health, ActionTier.SafeFix, ActionRunAs.System,
            "Rescan hardware", "جستجوی دوباره سخت‌افزار",
            "Asks Windows to detect hardware again (like Scan for hardware changes).",
            "Done.", [], Verify: new("devices.problems")),
        new("apps.find", ActionAreas.Health, ActionTier.Look, ActionRunAs.System,
            "Find an installed program", "جستجوی برنامه نصب‌شده",
            "Searches installed programs by name and reports versions.",
            "Matches with Name, Version, Publisher, InstalledOn.",
            [new("name", ActionParameterKind.SearchText, "Part of the program name.")]),
        new("disk.cleanup_safe", ActionAreas.Health, ActionTier.SafeFix, ActionRunAs.System,
            "Free disk space safely", "آزادسازی امن فضای دیسک",
            "Deletes temporary files older than two days (Windows and the user's temp folders) and old error reports. Never the Recycle Bin, Downloads or documents.",
            "Done with FreedMb and system drive FreeGbBefore/FreeGbAfter.",
            [], TimeoutSeconds: 180, MaxPerHour: 2),

        // ---------- Desktop and settings ----------
        new("explorer.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "Desktop and taskbar", "دسکتاپ و نوار وظیفه",
            "Whether Explorer (desktop, taskbar, Start), search and the shell are running and responding.",
            "Explorer (Running, Responding), StartMenu, Search.",
            []),
        new("explorer.restart", ActionAreas.Desktop, ActionTier.Disruptive, ActionRunAs.User,
            "Restart the desktop", "راه‌اندازی دوباره دسکتاپ",
            "Restarts Windows Explorer (desktop, taskbar, Start). Open folder windows close; programs keep running.",
            "Done.", [], TimeoutSeconds: 30),
        new("input.languages", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "Keyboard languages", "زبان‌های صفحه‌کلید",
            "Installed keyboard layouts (Persian, English) and the switch shortcut.",
            "Layouts, HasPersian, HasEnglish, SwitchHotkey.",
            []),
        new("input.add_layout", ActionAreas.Desktop, ActionTier.SafeFix, ActionRunAs.User,
            "Add a keyboard layout", "افزودن صفحه‌کلید",
            "Adds the Persian or English keyboard layout for the user.",
            "Done or NotNeeded.",
            [new("layout", ActionParameterKind.Choice, "Layout.", Choices: LayoutChoices)],
            Verify: new("input.languages")),
        new("region.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "Region and calendar", "تقویم و تنظیمات منطقه",
            "Format locale, calendar (Persian/Gregorian), date format and number format of the user.",
            "Locale, Calendar, ShortDate, DecimalSymbol.",
            []),
        new("timezone.set_standard", ActionAreas.Desktop, ActionTier.SafeFix, ActionRunAs.System,
            "Set the standard time zone", "تنظیم منطقه زمانی استاندارد",
            "Sets the PC's time zone to the organisation standard. The old zone is saved for undo.",
            "Done or NotNeeded.",
            [], Verify: new("time.status"), UndoActionId: "timezone.restore",
            UsesSettings: [ActionSettings.StandardTimeZone]),
        new("display.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "Displays", "نمایشگرها",
            "Connected monitors, resolution and which is primary; graphics adapter and driver date.",
            "Monitors, Adapters.",
            []),
        new("media.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "Camera, microphone and sound", "دوربین، میکروفون و صدا",
            "Whether apps may use the camera and microphone (Windows privacy switches), and audio devices with their status.",
            "Camera (AllowedForUser, BlockedByPolicy), Microphone, AudioDevices.",
            []),
        new("privacy.allow_camera_mic", ActionAreas.Desktop, ActionTier.SafeFix, ActionRunAs.User,
            "Allow camera or microphone", "اجازه دوربین یا میکروفون",
            "Turns on the user's Windows privacy switch for the camera and/or microphone. Never overrides an organisation policy.",
            "Done, NotNeeded or Blocked (blocked by policy).",
            [new("device", ActionParameterKind.Choice, "Which device.", Choices:
                [new("camera", "Camera"), new("microphone", "Microphone"), new("both", "Both")])],
            Verify: new("media.status")),
        new("power.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.System,
            "Power and battery", "برق و باتری",
            "Active power plan, battery charge and health (laptops).",
            "PowerPlan, Battery (ChargePercent, Status, HealthPercent).",
            []),
        new("onedrive.status", ActionAreas.Desktop, ActionTier.Look, ActionRunAs.User,
            "OneDrive status", "وضعیت OneDrive",
            "Whether OneDrive is installed, running and signed in for work.",
            "Installed, Running, BusinessAccount.",
            []),
        new("app.close", ActionAreas.Desktop, ActionTier.Disruptive, ActionRunAs.User,
            "Close an app", "بستن برنامه",
            "Asks one app to close normally (like clicking X). It is never forced; if it is still open after 20 seconds the user must close it.",
            "Done, NotNeeded or Blocked (still open, maybe an unsaved document).",
            [new("app", ActionParameterKind.Choice, "Which app.", Choices: AppChoices)],
            TimeoutSeconds: 40),
        new("reboot.schedule", ActionAreas.Desktop, ActionTier.Disruptive, ActionRunAs.System,
            "Restart the PC", "راه‌اندازی دوباره کامپیوتر",
            "Schedules a restart with a Windows warning to save work. Can be cancelled with reboot.cancel.",
            "Done with RestartAtUtc.",
            [new("minutes", ActionParameterKind.Choice, "Delay before the restart.", Choices:
                [new("5", "In 5 minutes"), new("15", "In 15 minutes"), new("60", "In 1 hour")])],
            MaxPerHour: 2),
        new("reboot.cancel", ActionAreas.Desktop, ActionTier.SafeFix, ActionRunAs.System,
            "Cancel a scheduled restart", "لغو راه‌اندازی دوباره",
            "Cancels a restart scheduled by reboot.schedule.",
            "Done or NotNeeded.", [], MaxPerHour: 5),

        // ---------- Helpdesk-only undo actions (never offered to the agent) ----------
        new("proxy.restore", ActionAreas.Network, ActionTier.OperatorOnly, ActionRunAs.User,
            "Undo proxy reset", "بازگرداندن پروکسی قبلی",
            "Restores the proxy settings saved by proxy.reset_to_standard.",
            "Done.",
            [new("enabled", ActionParameterKind.Choice, "Previous state.", Choices: [new("0", "Off"), new("1", "On")]),
             new("server", ActionParameterKind.LocalItem, "Previous server.", Required: false),
             new("autoConfigUrl", ActionParameterKind.LocalItem, "Previous PAC URL.", Required: false),
             new("bypass", ActionParameterKind.LocalItem, "Previous bypass list.", Required: false)]),
        new("printer.restore_default", ActionAreas.Printers, ActionTier.OperatorOnly, ActionRunAs.User,
            "Undo default printer change", "بازگرداندن چاپگر پیش‌فرض قبلی",
            "Makes the previous default printer the default again.",
            "Done.",
            [new("printer", ActionParameterKind.LocalItem, "Previous default printer.")]),
        new("timezone.restore", ActionAreas.Desktop, ActionTier.OperatorOnly, ActionRunAs.System,
            "Undo time zone change", "بازگرداندن منطقه زمانی قبلی",
            "Sets the previous time zone again.",
            "Done.",
            [new("zone", ActionParameterKind.LocalItem, "Previous Windows time zone id.")])
    ];
}
