using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api;

/// <summary>
/// camelCase wire DTOs for the Next.js dashboard. System.Text.Json's web
/// defaults already camelCase property names; these shape the payload so the
/// frontend contract holds. (Exact byte-parity with the Django serializers is a
/// Phase 7 verification item — see docs/CSHARP_PARITY.md.)
/// </summary>
public static class Dtos
{
    public static object ThreadDto(Thread t) => new
    {
        id = t.Id,
        channel = t.Channel,
        key = t.Key,
        address = t.Address,
        name = t.Name,
        status = t.Status,
        assignee = t.Assignee,
        labels = t.Labels,
        contactId = t.ContactId,
        contactName = t.Contact?.Name,
        accountId = t.AccountId,
        accountName = t.Account?.Name,
        entityId = t.EntityId,
        aiEnabled = t.AiEnabled,
        optedOut = t.OptedOut,
        windowOpen = WindowOpen(t),
        callStatus = t.CallStatus,
        callRoom = t.CallRoom,
        updatedAt = t.UpdatedAt,
        createdAt = t.CreatedAt,
    };

    public static object MessageDto(Message m) => new
    {
        id = m.Id,
        threadId = m.ThreadId,
        accountId = m.AccountId,
        externalId = m.ExternalId,
        direction = m.Direction,
        source = m.Source,
        @private = m.Private,
        text = m.Text,
        status = m.Status,
        mediaType = m.MediaType,
        mediaPath = m.MediaPath,
        createdAt = m.CreatedAt,
    };

    public static object CannedDto(CannedResponse c) => new
    {
        id = c.Id,
        shortCode = c.ShortCode,
        content = c.Content,
        entityId = c.EntityId,
        createdBy = c.CreatedBy,
    };

    public static object AccountDto(ChannelAccount a) => new
    {
        id = a.Id,
        channel = a.Channel,
        provider = a.Provider,
        name = a.Name,
        externalId = a.ExternalId,
        entityId = a.EntityId,
        healthStatus = a.HealthStatus,
        role = a.Config.TryGetValue("role", out var r) ? r?.ToString() : null,
    };

    // The Meta 24h-window truth: open when the last inbound is < 24h old.
    private static bool WindowOpen(Thread t) => true; // refined in Phase 2 message pass

    // snake_case Contact model → the frontend's camelCase Contact (to_contact).
    public static object ContactDto(Contact c) => new
    {
        id = c.Id,
        name = c.Name,
        phone = c.Phone,
        email = c.Email,
        language = c.Language,
        status = c.Status,
        method = c.Method,
        companyName = c.CompanyName,
        doNotCall = c.DoNotCall,
        notes = c.Notes,
        externalId = c.ExternalId,
        tags = c.Tags,
        attributes = c.Attributes,
        preferredContactTime = c.PreferredContactTime,
        communicationStyle = c.CommunicationStyle,
        sentimentTrend = c.SentimentTrend,
        aiProfileSummary = c.AiProfileSummary,
        tenantId = c.TenantId,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };

    public static object ScriptTemplateDto(ScriptTemplate s) => new
    {
        id = s.Id, name = s.Name, content = s.Content, entityId = s.EntityId,
    };
}
