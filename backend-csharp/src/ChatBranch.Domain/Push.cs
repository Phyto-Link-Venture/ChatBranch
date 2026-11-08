namespace ChatBranch.Domain;

/// <summary>How a device is reached.</summary>
public static class PushPlatforms
{
    /// <summary>A browser (or the installed PWA) through the Web Push protocol + VAPID.</summary>
    public const string Web = "web";
    /// <summary>The phone app on Android, through Firebase Cloud Messaging.</summary>
    public const string Android = "android";
    /// <summary>The phone app on iOS — also through FCM, which fronts APNs.</summary>
    public const string Ios = "ios";

    public static readonly string[] All = { Web, Android, Ios };

    public static bool IsKnown(string? platform) => platform != null && All.Contains(platform);

    /// <summary>Does this platform go out through Firebase rather than Web Push?</summary>
    public static bool IsFirebase(string platform) => platform is Android or Ios;
}

/// <summary>
/// One device a person has asked to be alerted on — a browser tab's Web Push
/// subscription, or the phone app's Firebase token. A person may have several (a
/// laptop, a phone, the installed app), and each is theirs alone: a device is
/// registered by the signed-in session that owns it and only ever receives that
/// person's own notifications.
///
/// Rows are disposable. A subscription the push service has retired answers 404 or
/// 410 and the row is deleted on the spot (<c>Gone</c>), which is how browsers
/// expect expiry to be handled — there is nothing to migrate and nothing to repair.
/// </summary>
public class PushDevice
{
    public int Id { get; set; }

    /// <summary>The Profile (login) this device belongs to.</summary>
    public int UserId { get; set; }

    /// <summary>web | android | ios (<see cref="PushPlatforms"/>).</summary>
    public string Platform { get; set; } = PushPlatforms.Web;

    /// <summary>
    /// Web Push: the push service's endpoint URL. Firebase: the device token. It is
    /// the identity of the device — registering the same one twice updates the row
    /// instead of adding another, so a reinstall or a re-grant cannot double-alert.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Web Push only: the browser's P-256 public key (base64url, from the subscription).</summary>
    public string P256dh { get; set; } = "";

    /// <summary>Web Push only: the subscription's 16-byte auth secret (base64url).</summary>
    public string Auth { get; set; } = "";

    /// <summary>What to call it in "your devices" — the browser or phone, never typed by hand.</summary>
    public string Label { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Last time this device was handed a notification without complaint.</summary>
    public DateTime? LastPushAt { get; set; }

    /// <summary>What the push service last said went wrong (for the devices list).</summary>
    public string LastError { get; set; } = "";

    /// <summary>
    /// Consecutive failures that were not "gone". A device whose service keeps
    /// refusing is parked rather than retried forever (<see cref="MaxFailures"/>).
    /// </summary>
    public int Failures { get; set; }

    /// <summary>After this many failures in a row a device is left alone until it re-registers.</summary>
    public const int MaxFailures = 10;
}
