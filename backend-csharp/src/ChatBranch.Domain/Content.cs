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
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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
    public bool Active { get; set; } = true;
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
