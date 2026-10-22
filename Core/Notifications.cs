namespace LLMHostProject.Core;

/// <summary>Which events may announce themselves (settings.json → <c>notifications</c>).</summary>
public class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool Downloads { get; set; } = true;
    /// <summary>A model that took longer than <see cref="LoadSeconds"/> to load is ready.</summary>
    public bool ModelLoaded { get; set; } = true;
    public int LoadSeconds { get; set; } = 10;
    /// <summary>An answer finished while the window was not in front.</summary>
    public bool Generation { get; set; } = true;
    public bool UpdateAvailable { get; set; } = true;
    /// <summary>Only announce while the window is minimised, hidden in the tray or not in front; when off, also while it is in front.</summary>
    public bool OnlyWhenMinimised { get; set; } = true;
}

/// <summary>One thing worth telling the user. <see cref="View"/> is where a click should go: chat, models, discover, settings.</summary>
public record NotificationEvent(long Id, string Kind, string Title, string Body, string View, DateTime Time);

/// <summary>
/// Collects the events (a download finished, a model is loaded, an update exists, an answer is done) for whoever shows them: the desktop window
/// as a tray notification, the web page through the browser's Notifications API. Events of a kind that is switched off are not even recorded.
/// </summary>
public static class NotificationHub
{
    private static readonly object Gate = new();
    private static readonly List<NotificationEvent> Events = new();
    private static long _next;
    public static event Action<NotificationEvent>? Published;
    public static readonly string[] Kinds = { "download", "download-failed", "model", "generation", "update", "test" };

    private static bool Wanted(string kind, NotificationSettings s) => s.Enabled && kind switch
    {
        "download" or "download-failed" => s.Downloads,
        "model" => s.ModelLoaded,
        "generation" => s.Generation,
        "update" => s.UpdateAvailable,
        "test" => true,
        _ => false,
    };

    /// <summary>Records an event when its kind is switched on; returns it, or null.</summary>
    public static NotificationEvent? Publish(string kind, string title, string body, string view = "chat", NotificationSettings? settings = null)
    {
        settings ??= AppSettings.Current.Notifications;
        if (!Wanted(kind, settings)) return null;
        NotificationEvent e;
        lock (Gate)
        {
            e = new NotificationEvent(++_next, kind, title, body, view, DateTime.Now);
            Events.Add(e);
            if (Events.Count > 100) Events.RemoveAt(0);
        }
        try { Published?.Invoke(e); } catch { }
        return e;
    }

    public static (long Last, IReadOnlyList<NotificationEvent> Events) Since(long id)
    {
        lock (Gate) return (_next, Events.Where(e => e.Id > id).ToList());
    }

    public static void Clear() { lock (Gate) { Events.Clear(); } }
}
