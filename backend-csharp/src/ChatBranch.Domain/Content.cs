namespace ChatBranch.Domain;

// Entity-scoped libraries an outlet inherits: canned responses, script
// templates, workflows, agent skills, knowledge items (Django app: crm). Each
// carries a nullable Entity (null = global, inherited by every entity).

public class CannedResponse : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string ShortCode { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>
    /// Channels this quick reply is offered on (thread channel ids: whatsapp,
    /// telegram, email, facebook = Messenger, instagram, website). EMPTY = every
    /// channel — the default, so templates written before the column existed
    /// stay shared.
    /// </summary>
    public List<string> Channels { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Last edit (null on rows written before the column existed).</summary>
    public DateTime? UpdatedAt { get; set; }
}

public class ScriptTemplate : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Workflow : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string FlowId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
    public List<object?> Nodes { get; set; } = new();
    public List<object?> Edges { get; set; } = new();
    public bool Active { get; set; }
    /// <summary>When the flow last went live (null while paused). "Contact
    /// created" and "Scheduled" triggers only act on what happens after it,
    /// so arming a flow never back-fires on the whole contact book.</summary>
    public DateTime? LiveSince { get; set; }
    public int Position { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FlowRun> Runs { get; set; } = new List<FlowRun>();
}

public class AgentSkill : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string SkillId { get; set; } = "";
    public string Name { get; set; } = "";
    public string UseWhen { get; set; } = "";
    public string Instructions { get; set; } = "";
    public int Position { get; set; }

    // ---- code skills (2026-10) ----------------------------------------------
    /// <summary>
    /// prompt = instructions in the SKILLS block (the original behaviour);
    /// tool = JavaScript `run(args, ctx)` the model may CALL; check = JavaScript
    /// `check(reply, ctx)` that validates every AI reply before it is sent.
    /// See <see cref="SkillKinds"/>.
    /// </summary>
    public string Kind { get; set; } = SkillKinds.Prompt;
    /// <summary>What the operator described, in their own words.</summary>
    public string Description { get; set; } = "";
    /// <summary>JavaScript source (tool / check only). Runs in the Jint sandbox.</summary>
    public string Code { get; set; } = "";
    /// <summary>JSON schema (text) of a tool's arguments; "" = no arguments.</summary>
    public string Parameters { get; set; } = "";
    /// <summary>JSON array (text) of test cases: {name, args|input, expect, now?}.</summary>
    public string Tests { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Channels the skill applies on; empty = every channel.</summary>
    public List<string> Channels { get; set; } = new();
    /// <summary>Check skills, when a reply fails: retry = rewrite with the reason
    /// (max 2×), then hand over · handover = hand over at once (a rewrite cannot
    /// fix it) · block = rewrite (max 2×), then send nothing.</summary>
    public string OnFail { get; set; } = "retry";
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Wire values of <see cref="AgentSkill.Kind"/>.</summary>
public static class SkillKinds
{
    public const string Prompt = "prompt";
    public const string Tool = "tool";
    public const string Check = "check";

    public static bool IsCode(string? kind) => kind is Tool or Check;
}

public class KnowledgeItem : IEntityScoped
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Kind { get; set; } = "fact";       // fact|policy|faq|event|notice
    public string Title { get; set; } = "";
    public string Question { get; set; } = "";
    public string Content { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<string> Channels { get; set; } = new();
    public int Priority { get; set; }
    // ---- Help Center (public portal) ----------------------------------------
    /// <summary>
    /// Published = readable by anyone at /help. SEPARATE from Active, which
    /// controls whether the AI may use it: an internal pricing note stays
    /// Active and unpublished, a public FAQ is both.
    /// </summary>
    public bool Published { get; set; }
    /// <summary>URL slug for the public article ("" = derived from the title).</summary>
    public string Slug { get; set; } = "";
    /// <summary>Free-text grouping on the portal ("Billing", "Getting started").</summary>
    public string Category { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>The uploaded document this item's text was extracted from ("" =
    /// typed in). The file itself is never stored.</summary>
    public string SourceFile { get; set; } = "";
}
