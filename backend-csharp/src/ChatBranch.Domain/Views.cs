namespace ChatBranch.Domain;

/// <summary>
/// A named, saved set of list filters — one operator's own "VIP drivers" or
/// "Waiting on me" tab above the inbox. Strictly per user: nobody else lists,
/// renames or deletes another login's views. <see cref="Scope"/> names the list
/// the filters belong to ("chats" today) so the same table can later carry
/// contact-list views without a schema change.
/// </summary>
public class SavedView
{
    public int Id { get; set; }
    /// <summary>auth_user id of the owner (resolved server-side, never posted).</summary>
    public int UserId { get; set; }
    public string Scope { get; set; } = "chats";
    public string Name { get; set; } = "";
    /// <summary>The filter object exactly as the page posted it (raw JSON text).</summary>
    public string Filters { get; set; } = "{}";
    /// <summary>Tab order, ascending; ties break on id.</summary>
    public int Position { get; set; }
    public DateTime CreatedAt { get; set; }
}
