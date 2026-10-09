namespace ChatBranch.Domain;

/// <summary>
/// The tag registry — one vocabulary for "what kind of thing is this?" across
/// contacts (boss / employee / driver…), entities (inner company / outside
/// company) and conversations (follow-up / urgent / resolved…).
///
/// A tag is more than a word: it can carry ATTRIBUTES (field definitions — a
/// driver has a licence number, an outside company a contract end date) whose
/// VALUES live on the <see cref="TagAssignment"/>. Tags in the same
/// <see cref="GroupName"/> are mutually exclusive on one target, so "Company
/// type" holds exactly one of Inner company / Outside company.
///
/// Entity scoping follows the library rule (ScopeInherited): a tag bound to an
/// entity is that outlet's own; an unbound one is organization-wide and every
/// outlet inherits it.
/// </summary>
public class Tag : IEntityScoped
{
    public int Id { get; set; }
    /// <summary>Which outlet owns this tag. null = organization-wide.</summary>
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Globally unique, immutable once created (conversation labels store it).</summary>
    public string Slug { get; set; } = "";
    /// <summary>A palette KEY (tangerine/ink/teal/plum/ochre/blue/green/rose), never a hex.</summary>
    public string Color { get; set; } = "ink";
    public string Description { get; set; } = "";
    /// <summary>contact | entity | conversation</summary>
    public string AppliesTo { get; set; } = "contact";
    /// <summary>Optional exclusivity group; "" = no group.</summary>
    public string GroupName { get; set; } = "";
    /// <summary>Field definitions whose values ride on each assignment.</summary>
    public List<TagAttribute> Attributes { get; set; } = new();
    public int Position { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One field a tag carries (serialized into Tag.Attributes JSON).</summary>
public class TagAttribute
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>text | number | date | select | boolean | phone | email</summary>
    public string Type { get; set; } = "text";
    public List<string> Options { get; set; } = new();
    public bool Required { get; set; }
}

/// <summary>
/// A tag applied to one target (a contact or an entity — conversation labels
/// keep living on Thread.Labels as slugs). Unique per (tag, target).
/// </summary>
public class TagAssignment
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    /// <summary>contact | entity</summary>
    public string TargetType { get; set; } = "contact";
    public int TargetId { get; set; }
    /// <summary>Attribute values keyed by TagAttribute.Key.</summary>
    public Dictionary<string, object?> Values { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
