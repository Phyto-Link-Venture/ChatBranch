namespace ChatBranch.Domain;

/// <summary>
/// A file uploaded ONCE and sent by its id afterwards — the answer for a file that is
/// nowhere online, where a link cannot help.
///
/// Someone drops an invoice or a photo into the media library from the dashboard or
/// their phone, and from then on sending it costs a few hundred characters
/// (<c>file_id</c>) instead of a megabyte of base64. It is kept
/// <see cref="RetentionDays"/> days, or until it is deleted.
/// </summary>
public class MediaLibraryItem
{
    public int Id { get; set; }

    /// <summary>The short public id a send refers to (<c>f_…</c>).</summary>
    public string FileId { get; set; } = "";

    /// <summary>What to call it in the library (the uploaded file's own name).</summary>
    public string Name { get; set; } = "";

    public string Mime { get; set; } = "";

    /// <summary>image | video | audio | document — the inbox's own vocabulary.</summary>
    public string Kind { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Where the bytes are, relative to the library root (<c>MediaStore.LibraryRoot</c>).</summary>
    public string Path { get; set; } = "";

    /// <summary>Who uploaded it — the library is shown to everyone, this is for the row.</summary>
    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>
    /// The outlet it belongs to, so a branch's uploads are not another branch's to send.
    /// Null = organization-wide (anyone who may send may use it).
    /// </summary>
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When it stops being kept. The sweep deletes the bytes and the row.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Last time it was sent, so the library can show what is actually used.</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>How long an upload is kept unless someone deletes it first.</summary>
    public const int RetentionDays = 30;
}
