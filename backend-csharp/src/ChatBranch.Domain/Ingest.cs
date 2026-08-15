namespace ChatBranch.Domain;

// Multi-tenancy backbone (Django app: ingest). A Tenant owns Entities
// (outlets/branches). Entity scoping — the "entity override wins, global is
// fallback" rule — pivots on Entity, so these two come first.

public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Entity> Entities { get; set; } = new List<Entity>();
}

public class Entity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    /// <summary>
    /// Self-referencing hierarchy: null = a TOP-LEVEL entity (the "main" one),
    /// otherwise the entity it sits under. A child always inherits its parent's
    /// tenant, so operators only ever pick a parent — never a tenant.
    /// </summary>
    public int? ParentId { get; set; }
    public Entity? Parent { get; set; }
    public ICollection<Entity> Children { get; set; } = new List<Entity>();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
