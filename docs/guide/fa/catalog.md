---
title: تیکت‌های رایج و کار مربوط به هرکدام
summary: تیکت‌های روزمرهٔ پشتیبانی ویندوز به تفکیک بخش، با کاری که هرکدام را انجام می‌دهد، آنچه ساخته شده، آنچه برنامه‌ریزی شده و کارهایی که عامل هرگز تنها انجام نمی‌دهد.
order: 4
---

# تیکت‌های رایج و کار مربوط به هرکدام

این نقشه‌ای است میان آنچه کاربران شکایت می‌کنند و کارهای ایمن و ثابتی که سیستم دارد. هنگام نوشتن مهارت از آن استفاده کنید: تیکت را پیدا کنید، شناسهٔ کارها را بردارید و آن‌ها را به مراحل تبدیل کنید. فهرست کامل با شناسه، نوع و سطح هر کار در `docs/operation-catalog.md` مخزن است.

سرویس‌های Microsoft 365 (Teams، OneDrive، Exchange Online، ورود Entra) جزو این سیستم **نیستند**. Office دسکتاپ و Outlook هستند.

## چطور بخوانیم

| واژه | معنی |
| --- | --- |
| **Look** | فقط واقعیت می‌خواند. آزادانه اجرا می‌شود. |
| **Fix** | یک SafeFix: کوچک و برگشت‌پذیر، بدون پرسیدن اجرا می‌شود و قبل و بعد بررسی می‌شود. |
| **Disrupt** | کاربر را مزاحم می‌کند (راه‌اندازی دوبارهٔ دستگاه، دسکتاپ یا کامپیوتر). فقط بعد از «بله»ی کاربر در گفتگو. |
| **IT** | در صفحهٔ **Approvals** منتظر تأیید یک کارشناس پشتیبانی می‌ماند. |
| **Built / Planned** | Built یعنی در کد هست. Planned یعنی برای دستهٔ بعدی برنامه‌ریزی شده؛ تا آن موقع عامل ارجاع می‌دهد یا درخواست قابلیت ثبت می‌کند. |

مدیر می‌تواند هر کار Fix یا Disrupt را در **Capabilities ← Actions** به **IT** ببرد («Needs IT approval»).

## شبکه و اینترنت

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| اینترنت یا شبکه نیست | `network.status`، `network.triage` و بعد تعمیری که واقعیت‌ها نشان می‌دهند | Built |
| کابل بیرون است، کارت شبکه غیرفعال است | `network.adapters`، `network.adapter_enable` (Fix)، `network.adapter_restart` (Disrupt) | Built |
| آدرس 169.254، DHCP نیست | `dhcp.renew` | Built |
| DNS کهنه یا DNS دستیِ اشتباه | `dns.flush`، `dns.reset_to_dhcp` (قابل برگشت) | Built |
| پروکسی مرورگر یا سیستم (WinHTTP) اشتباه | `proxy.get`، `proxy.reset_to_standard`، `proxy.system_get`، `proxy.system_reset_to_standard` (همه قابل برگشت) | Built |
| Wi-Fi وصل نمی‌شود، فایروال، نوع شبکه | `wifi.status`، `firewall.status`، `network.category` | Planned |
| قطعی که چند نفر را درگیر کرده | `check_known_incidents`، `recent_reports_from_others`، `probe_from_server` | Built |

## VPN

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| VPN وصل نمی‌شود | `vpn.status` برای یک VPN از فهرست **VPNهای سازمان** | Built |
| کلاینت VPN گیر کرده (FortiClient، Cisco، GlobalProtect، OpenVPN، WireGuard) | `vpn.client_restart` (فقط سرویسی که در فهرست آمده) | Built |
| سرویس VPN داخلی ویندوز متوقف است | `service.restart` با `RasMan` | Built |
| وصل است ولی نام‌های داخلی کار نمی‌کند | `vpn.status`، `dns.flush`، `network.triage` | Built |
| پروفایل VPN داخلی نیست، اتصال یا قطع | `vpn.ensure_profile`، `vpn.connect` | Planned |

## سایت‌ها و سامانه‌های داخلی

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| سامانهٔ وب داخلی باز نمی‌شود | `network.triage`، `web.probe` | Built |
| هشدار گواهی | `cert.check_trust` | Built |
| ساعت اشتباه HTTPS را خراب می‌کند | `time.status`، `time.resync` | Built |
| مرورگر کند است یا صفحه‌های قدیمی می‌آید | `browser.inventory`، `browser.clear_cache`، بستهٔ بازنشانی Chrome | Built |
| «این سایت عمداً بسته است؟»، باز کردن سایت تازه | فهرست سایت‌های مجاز و درخواست تأیید | Planned |

## پوشه‌های اشتراکی، چاپگر و اسکنر

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| درایو نیست یا ضربدر قرمز دارد | `drives.list`، `share.test`، `drive.remap` | Built |
| دسترسی به پوشه رد می‌شود | `share.test`، `check_user_account`، `whoami.groups`، `credman.list`، `credman.remove` | Built |
| چاپ نمی‌شود، کارها گیر کرده | `printer.list`، `printer.status`، `spooler.restart_and_clear` | Built |
| چاپگر نیست یا پیش‌فرض اشتباه است | `printer.add`، `printer.remove`، `printer.set_default` | Built |
| درایور چاپگر برای مدل نصب نیست | `drivers.inventory` و بستهٔ درایور از **فهرست درایور** | Built |

## دستگاه‌ها و درایورها

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| دستگاه علامت زرد دارد یا شناخته نمی‌شود | `drivers.inventory` (کد مشکل، شناسهٔ سخت‌افزار، درایور فعلی) | Built |
| دستگاه خاموش است | `device.enable` | Built |
| وب‌کم، کارت‌خوان یا بلوتوث هنگ کرده | `device.restart` (Disrupt؛ دیسک، نمایشگر، کیبورد، ماوس و کنترلر USB هرگز لمس نمی‌شوند) | Built |
| نصب درایور | بستهٔ درایور امضاشده که در فهرست درایور آمده و عامل اجرا می‌کند | Built |
| درایور تازه دستگاه را خراب کرد، حذف درایور بد | `driver.rollback`، `driver.remove_package` | Planned |

## نرم‌افزار

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| نصب برنامهٔ تأییدشده | بستهٔ نصب (Chrome، Foxit، Adobe Connect، Photos و فونت‌های فارسی هست؛ مال خودتان را اضافه کنید) | Built |
| حذف یک برنامه | `software.uninstall` (IT) برای برنامه‌های **نرم‌افزارهای قابل حذف**، یا بستهٔ حذف | Built |
| چه چیزی نصب است | `software.inventory`، `apps.find` | Built |

## Office و Outlook

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| Office فعال نیست | ابزارها و بسته‌های فعال‌سازی | Built |
| Outlook وصل نمی‌شود | `outlook.status` | Built |
| خرابی افزونه، تعمیر Office، بازسازی OST | `office.addins_list`، `office.quick_repair`، `outlook.rebuild_ost` | Planned |

## حساب و ورود

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| حساب قفل یا منقضی است | `check_user_account` و بعد ارجاع: پشتیبانی قفل را باز می‌کند | Built |
| trust relationship، سیاست اعمال نشده | `domain.health`، `gpo.refresh_computer`، `gpo.refresh_user` | Built |
| مدام رمز می‌خواهد | `credman.list`، `credman.remove`، `kerberos.purge` | Built |
| بعد از تغییر گروه دسترسی نیست | `whoami.groups` و بعد خروج و ورود | Built |

## کارایی، دیسک و به‌روزرسانی

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| کامپیوتر کند یا هنگ است | `perf.snapshot`، `system.recent_changes`، `events.summary` | Built |
| دیسک پر است | `disk.usage`، `disk.cleanup_safe` | Built |
| چند روز است نیاز به راه‌اندازی دوباره هست | `restart.pending`، `reboot.schedule` (Disrupt) | Built |
| Windows Update گیر کرده، نگرانی امنیتی | `windows_update.status`، `security.status`، `defender.quick_scan` | Built |
| وضعیت BitLocker، سلامت دیسک، بازنشانی آپدیت، SFC و DISM | کارهای تازهٔ Look و IT | Planned |

## دسکتاپ، صدا و لپ‌تاپ

| تیکت | چه چیزی انجامش می‌دهد | وضعیت |
| --- | --- | --- |
| صدا نیست، بی‌صدا شده، خروجی اشتباه است | `audio.status`، `audio.unmute`، `audio.set_default` (قابل برگشت) | Built |
| دوربین یا میکروفون بسته است | `media.status`، `privacy.allow_camera_mic` | Built |
| کیبورد فارسی نیست | `input.languages`، `input.add_layout` | Built |
| تسک‌بار یا Start گیر کرده | `explorer.status`، `explorer.restart` (Disrupt) | Built |
| باتری و برق | `power.status` | Built |

## کارهایی که سیستم هرگز تنها انجام نمی‌دهد

این‌ها همیشه دست یک آدم می‌ماند، حتی اگر کاری نزدیک به آن وجود داشته باشد:

- دادن **کلید بازیابی BitLocker**، ریست رمز، باز کردن حساب قفل یا تغییر گروه کاربر. عامل فقط درخواست می‌کند؛ یک کارشناس تصمیم می‌گیرد.
- اجرای دستور، اسکریپت یا فایلی که هوش مصنوعی نوشته، یا دانلود چیزی از اینترنت.
- خاموش کردن آنتی‌ویروس یا فایروال، یا تغییر سیاست امنیتی سازمان.
- حذف نرم‌افزاری که در فهرست **نرم‌افزارهای قابل حذف** نیست.
- دست زدن به دیسک، کارت گرافیک، کیبورد، ماوس و کنترلر USB.
- تعمیر کامپیوتر وقتی قطعی شناخته‌شده‌ای با شکایت می‌خواند.

## با تیکت‌های خودتان نقشه را بهتر کنید

هر سازمانی تیکت‌های پرتکرار خودش را دارد. بشمارید کاربران واقعاً چه می‌فرستند، برای ده‌تای اول مهارت بنویسید و آنچه سیستم نمی‌تواند به **Feature requests** برود (عامل درخواست کاربران را آنجا ثبت می‌کند). همان فهرست دستهٔ بعدی کارها را تعیین می‌کند.
