using EndpointAgents.Shared.Actions;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

/// <summary>Where the Tray shows a notice next to the clock; the Tray host sets it at start.</summary>
public static class UserNoticeSink
{
    /// <summary>Title, text, and whether it is a warning. Null when no Tray window is running.</summary>
    public static Action<string, string, bool>? Show { get; set; }

    /// <summary>The fixed Persian text of each notice. Nothing outside this table can be shown.</summary>
    public static IReadOnlyDictionary<string, (string Title, string Text, bool Warning)> Texts { get; } =
        new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal)
        {
            [UserNotices.RestartNeeded] = ("لطفاً کامپیوتر را دوباره راه‌اندازی کنید",
                "چند روز است کامپیوتر خاموش و روشن نشده است. کارهایتان را ذخیره کنید و آن را ری‌استارت کنید.", true),
            [UserNotices.DiskFull] = ("فضای درایو C تقریباً پر است",
                "فایل‌های بزرگ و غیرضروری را پاک کنید یا به پشتیبانی پیام دهید.", true),
            [UserNotices.DiskFailing] = ("ممکن است دیسک کامپیوتر خراب شود",
                "از فایل‌های مهم خود نسخهٔ پشتیبان بگیرید و به پشتیبانی خبر دهید.", true)
        };
}

/// <summary>Shows one fixed notice from <see cref="UserNoticeSink.Texts"/> in the Tray.</summary>
public sealed class NoticeShowAction : IEndpointAction
{
    public string Id => "notice.show";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        var id = invocation.Argument("notice");
        if (id is null || !UserNoticeSink.Texts.TryGetValue(id, out var notice))
            return Task.FromResult(EndpointActionResult.Failed("Unknown notice."));
        if (UserNoticeSink.Show is not { } show)
            return Task.FromResult(EndpointActionResult.Failed("The Tray cannot show notices right now."));
        show(notice.Title, notice.Text, notice.Warning);
        return Task.FromResult(EndpointActionResult.Done("The notice was shown.", new { Notice = id }));
    }
}
