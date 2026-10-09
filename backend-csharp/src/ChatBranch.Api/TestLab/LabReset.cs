using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// What belongs to the lab. The fence is explicit — never "anything that looks
/// like a test": the <c>test-lab</c> entity and everything beneath it, the
/// accounts / teams / tags / libraries bound to those entities, the
/// <c>testlab_*</c> logins and the <c>testlab-*</c> role, and contacts that are
/// linked to a lab entity, carry a lab tag, or use a <c>@testlab.invalid</c>
/// address (the widget's visitor contacts). Conversations are the lab's when
/// they sit in a lab entity, on a lab account, or with a lab contact.
/// </summary>
public sealed class LabScope
{
    public List<int> EntityIds { get; } = new();
    public List<int> AccountIds { get; } = new();
    public List<int> UserIds { get; } = new();
    public List<int> ProfileIds { get; } = new();
    public List<string> Usernames { get; } = new();
    public List<int> TagIds { get; } = new();
    public List<int> ContactIds { get; } = new();
    public List<int> ThreadIds { get; } = new();
    public List<(string Channel, string Key)> ThreadKeys { get; } = new();
    public List<int> TeamIds { get; } = new();
    public List<int> WorkflowIds { get; } = new();
    public List<string> FlowIds { get; } = new();
    public List<int> KnowledgeIds { get; } = new();

    public static LabScope Load(AppDbContext db)
    {
        var s = new LabScope();
        var hqIds = db.Entities.AsNoTracking().Where(e => e.Slug == LabFixture.HqSlug && e.ParentId == null)
            .Select(e => e.Id).ToList();
        // the whole subtree (branches a person added under the lab are the lab's too)
        var frontier = new List<int>(hqIds);
        while (frontier.Count > 0 && s.EntityIds.Count < 500)
        {
            s.EntityIds.AddRange(frontier.Where(id => !s.EntityIds.Contains(id)));
            var next = db.Entities.AsNoTracking().Where(e => e.ParentId != null && frontier.Contains(e.ParentId.Value))
                .Select(e => e.Id).ToList();
            frontier = next.Where(id => !s.EntityIds.Contains(id)).ToList();
        }
        var ents = s.EntityIds;

        s.AccountIds.AddRange(db.ChannelAccounts.AsNoTracking()
            .Where(a => a.EntityId != null && ents.Contains(a.EntityId.Value)).Select(a => a.Id));
        var users = db.Users.AsNoTracking().Where(u => u.Username.StartsWith(LabFixture.UserPrefix))
            .Select(u => new { u.Id, u.Username }).ToList();
        s.UserIds.AddRange(users.Select(u => u.Id));
        s.Usernames.AddRange(users.Select(u => u.Username));
        var uids = s.UserIds;
        s.ProfileIds.AddRange(db.Profiles.AsNoTracking().Where(p => uids.Contains(p.UserId)).Select(p => p.Id));
        s.TagIds.AddRange(db.Tags.AsNoTracking().Where(t => t.EntityId != null && ents.Contains(t.EntityId.Value))
            .Select(t => t.Id));
        s.TeamIds.AddRange(db.Teams.AsNoTracking().Where(t => t.EntityId != null && ents.Contains(t.EntityId.Value))
            .Select(t => t.Id));
        var flows = db.Workflows.AsNoTracking().Where(w => w.EntityId != null && ents.Contains(w.EntityId.Value))
            .Select(w => new { w.Id, w.FlowId }).ToList();
        s.WorkflowIds.AddRange(flows.Select(f => f.Id));
        s.FlowIds.AddRange(flows.Select(f => f.FlowId));
        s.KnowledgeIds.AddRange(db.KnowledgeItems.AsNoTracking()
            .Where(k => k.EntityId != null && ents.Contains(k.EntityId.Value)).Select(k => k.Id));

        var tags = s.TagIds;
        var suffix = "@" + LabFixture.EmailDomain;
        var contacts = db.ContactEntities.AsNoTracking().Where(l => ents.Contains(l.EntityId)).Select(l => l.ContactId)
            .Concat(db.TagAssignments.AsNoTracking()
                .Where(a => a.TargetType == "contact" && tags.Contains(a.TagId)).Select(a => a.TargetId))
            .Concat(db.Contacts.AsNoTracking().Where(c => c.Email.EndsWith(suffix)).Select(c => c.Id))
            .Distinct().ToList();
        s.ContactIds.AddRange(contacts);

        var accts = s.AccountIds;
        var threads = db.Threads.AsNoTracking()
            .Where(t => (t.EntityId != null && ents.Contains(t.EntityId.Value))
                        || (t.AccountId != null && accts.Contains(t.AccountId.Value))
                        || (t.ContactId != null && contacts.Contains(t.ContactId.Value)))
            .Select(t => new { t.Id, t.Channel, t.Key }).ToList();
        s.ThreadIds.AddRange(threads.Select(t => t.Id));
        s.ThreadKeys.AddRange(threads.Select(t => (t.Channel, t.Key)));
        return s;
    }

    public bool IsEmpty => EntityIds.Count == 0 && UserIds.Count == 0 && ContactIds.Count == 0
                           && ThreadIds.Count == 0 && AccountIds.Count == 0;
}

/// <summary>Keeps the lab's own data from reaching outside the machine.</summary>
public static class LabSafety
{
    /// <summary>
    /// A lab contact's WhatsApp number must never be looked up through one of
    /// your connected QR numbers: discovery may have queued a lookup (contact
    /// create / import / self-identification) — answer it locally instead.
    /// </summary>
    public static int NeutraliseLookups(AppDbContext db)
    {
        var scope = LabScope.Load(db);
        if (scope.ContactIds.Count == 0) return 0;
        var ids = scope.ContactIds;
        return db.ContactIdentities
            .Where(i => ids.Contains(i.ContactId) && i.Verification == IdentityVerdicts.Pending)
            .ExecuteUpdate(u => u
                .SetProperty(i => i.Verification, IdentityVerdicts.Unknown)
                .SetProperty(i => i.VerificationDetail, "Test lab number — never looked up.")
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
    }
}

/// <summary>
/// <c>POST /api/testlab/reset</c>: delete everything inside the fence — and
/// nothing outside it. Conversations (+ messages, drafts, follow-up state,
/// identity requests, email headers), contacts (+ links, identities, invites,
/// records, tag assignments, summaries, usage rows), bookings, queued jobs,
/// flow runs + step events, the lab's flows, rules, knowledge, skills, quick
/// replies, tags, teams, settings, accounts, entities, the testlab_* logins
/// (their tokens, notifications, saved views, API keys, assistant history),
/// the lab role, notifications that point at lab conversations / accounts /
/// bookings, and the lab's custom fields in the org schema. Audit rows stay
/// (the trail outlives what it describes); a lab login's id is cleared from
/// them. The run history (crm_testlabrun) is kept.
/// </summary>
public static class LabReset
{
    public static Dictionary<string, int> Run(AppDbContext db)
    {
        var s = LabScope.Load(db);
        var counts = new Dictionary<string, int>();
        void Count(string what, int n) { if (n > 0) counts[what] = counts.GetValueOrDefault(what) + n; }

        using var tx = db.Database.BeginTransaction();

        var threads = s.ThreadIds;
        var contacts = s.ContactIds;
        var accounts = s.AccountIds;
        var ents = s.EntityIds;
        var users = s.UserIds;
        var workflows = s.WorkflowIds;
        var flowIds = s.FlowIds;

        // ---- notifications that point at lab things (before their targets go) -------------
        var bookingIds = db.Bookings.AsNoTracking()
            .Where(b => (b.EntityId != null && ents.Contains(b.EntityId.Value)) || contacts.Contains(b.ContactId)
                        || (b.ThreadId != null && threads.Contains(b.ThreadId.Value)))
            .Select(b => b.Id).ToList();
        var refKeys = threads.SelectMany(id => new[] { $"thread:{id}", $"ai-handover:{id}", $"identity:{id}" })
            .Concat(bookingIds.Select(id => $"booking:{id}"))
            .Concat(accounts.SelectMany(id => new[] { $"failover:{id}", $"recovery:{id}", $"account-health:{id}" }))
            .ToHashSet();
        var linkKeys = s.ThreadKeys.Select(k => k.Key).Where(k => k.Length >= 6).ToList();
        var notes = db.Notifications.AsNoTracking()
            .Where(n => users.Contains(n.UserId) || (n.RefKey != "" && refKeys.Contains(n.RefKey))
                        || n.Link.Contains("testlab") || n.Body.Contains("@" + LabFixture.EmailDomain))
            .Select(n => n.Id).ToList();
        if (linkKeys.Count > 0)
        {
            // a bell's link names its conversation (?key=… / ?thread=channel:key)
            var candidates = db.Notifications.AsNoTracking().Where(n => n.Link.Contains("/chats"))
                .Select(n => new { n.Id, n.Link }).ToList();
            var keySet = linkKeys.ToHashSet();
            foreach (var n in candidates)
                if (LinkedKey(n.Link) is string k && keySet.Contains(k)) notes.Add(n.Id);
        }
        Count("notifications", db.Notifications.Where(n => notes.Contains(n.Id)).ExecuteDelete());

        // ---- conversations ----------------------------------------------------------------
        var draftIds = db.AiDrafts.AsNoTracking()
            .Where(d => (d.ThreadId != null && threads.Contains(d.ThreadId.Value))
                        || (d.EntityId != null && ents.Contains(d.EntityId.Value))
                        || (d.ContactId != null && contacts.Contains(d.ContactId.Value)))
            .Select(d => d.Id).ToList();
        Count("ai_feedback", db.AiFeedback.Where(f => (f.ThreadId != null && threads.Contains(f.ThreadId.Value))
            || (f.DraftId != null && draftIds.Contains(f.DraftId.Value))
            || (f.EntityId != null && ents.Contains(f.EntityId.Value))
            || (f.UserId != null && users.Contains(f.UserId.Value))).ExecuteDelete());
        Count("ai_lessons", db.AiLessons.Where(l => l.EntityId != null && ents.Contains(l.EntityId.Value)).ExecuteDelete());
        Count("ai_drafts", db.AiDrafts.Where(d => draftIds.Contains(d.Id)).ExecuteDelete());
        Count("ai_followup_state", db.AiConversationStates.Where(x => threads.Contains(x.ThreadId)).ExecuteDelete());
        Count("identity_requests", db.IdentityRequests.Where(r => threads.Contains(r.ThreadId)).ExecuteDelete());
        Count("bookings", db.Bookings.Where(b => bookingIds.Contains(b.Id)).ExecuteDelete());

        // queued / sent jobs: a lab contact, a lab account, or addressed to a lab conversation
        var jobIds = db.OutboundJobs.AsNoTracking()
            .Where(j => (j.ContactId != null && contacts.Contains(j.ContactId.Value))
                        || (j.AccountId != null && accounts.Contains(j.AccountId.Value))
                        || (j.FlowRunId != null && db.FlowRuns.Any(r => r.Id == j.FlowRunId
                            && (workflows.Contains(r.WorkflowId) || (r.ContactId != null && contacts.Contains(r.ContactId.Value))))))
            .Select(j => j.Id).ToList();
        var keysByChannel = s.ThreadKeys.GroupBy(k => k.Channel).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
        foreach (var (channel, keys) in keysByChannel)
            jobIds.AddRange(db.OutboundJobs.AsNoTracking().Where(j => j.Channel == channel && keys.Contains(j.ToAddress))
                .Select(j => j.Id));
        jobIds = jobIds.Distinct().ToList();
        Count("outbound_jobs", db.OutboundJobs.Where(j => jobIds.Contains(j.Id)).ExecuteDelete());

        // flow runs: the lab's flows, or a lab contact enrolled in any flow
        var runIds = db.FlowRuns.AsNoTracking()
            .Where(r => workflows.Contains(r.WorkflowId) || (r.ContactId != null && contacts.Contains(r.ContactId.Value)))
            .Select(r => r.Id).ToList();
        Count("flow_step_events", db.FlowStepEvents.Where(e => runIds.Contains(e.RunId)
            || flowIds.Contains(e.WorkflowId) || (e.ContactId != null && contacts.Contains(e.ContactId.Value))).ExecuteDelete());
        // a job of a NON-lab flow that once targeted… nothing left: jobs were filtered above
        db.OutboundJobs.Where(j => j.FlowRunId != null && runIds.Contains(j.FlowRunId.Value))
            .ExecuteUpdate(u => u.SetProperty(j => j.FlowRunId, (int?)null));
        Count("flow_runs", db.FlowRuns.Where(r => runIds.Contains(r.Id)).ExecuteDelete());

        var messageIds = db.Messages.AsNoTracking().Where(m => threads.Contains(m.ThreadId)).Select(m => m.Id).ToList();
        db.OutboundJobs.Where(j => j.SentMessageId != null && messageIds.Contains(j.SentMessageId.Value))
            .ExecuteUpdate(u => u.SetProperty(j => j.SentMessageId, (int?)null));
        Count("email_headers", db.EmailHeaders.Where(h => threads.Contains(h.ThreadId)).ExecuteDelete());
        Count("messages", db.Messages.Where(m => threads.Contains(m.ThreadId)).ExecuteDelete());
        // a non-lab message carried by a lab account (never expected) keeps its row
        db.Messages.Where(m => m.AccountId != null && accounts.Contains(m.AccountId.Value))
            .ExecuteUpdate(u => u.SetProperty(m => m.AccountId, (int?)null));
        Count("threads", db.Threads.Where(t => threads.Contains(t.Id)).ExecuteDelete());

        // ---- contacts ------------------------------------------------------------------------
        Count("account_assignments", db.AccountAssignments.Where(a => contacts.Contains(a.ContactId)
            || accounts.Contains(a.AccountId)).ExecuteDelete());
        Count("contact_identities", db.ContactIdentities.Where(i => contacts.Contains(i.ContactId)).ExecuteDelete());
        Count("contact_invites", db.ContactInvites.Where(i => contacts.Contains(i.ContactId)
            || (i.EntityId != null && ents.Contains(i.EntityId.Value))).ExecuteDelete());
        Count("tag_assignments", db.TagAssignments.Where(a => (a.TargetType == "contact" && contacts.Contains(a.TargetId))
            || (a.TargetType == "entity" && ents.Contains(a.TargetId)) || s.TagIds.Contains(a.TagId)).ExecuteDelete());
        var recordIds = db.Records.AsNoTracking().Where(r => contacts.Contains(r.ContactId)).Select(r => r.Id).ToList();
        db.RecordEvents.Where(e => recordIds.Contains(e.RecordId)).ExecuteDelete();
        Count("records", db.Records.Where(r => recordIds.Contains(r.Id)).ExecuteDelete());
        Count("summaries", db.ConversationSummaries.Where(x => contacts.Contains(x.ContactId)).ExecuteDelete());
        Count("llm_usage", db.LlmUsage.Where(x => x.ContactId != null && contacts.Contains(x.ContactId.Value)).ExecuteDelete());
        db.PendingToolActions.Where(x => contacts.Contains(x.ContactId)).ExecuteDelete();
        db.CallRecords.Where(x => x.ContactId != null && contacts.Contains(x.ContactId.Value)).ExecuteDelete();
        db.Profiles.Where(p => p.ContactId != null && contacts.Contains(p.ContactId.Value))
            .ExecuteUpdate(u => u.SetProperty(p => p.ContactId, (int?)null));
        Count("contact_entity_links", db.ContactEntities.Where(l => contacts.Contains(l.ContactId) || ents.Contains(l.EntityId))
            .ExecuteDelete());
        Count("contacts", db.Contacts.Where(c => contacts.Contains(c.Id)).ExecuteDelete());

        // ---- the lab's libraries + configuration ---------------------------------------------
        var knowledge = s.KnowledgeIds;
        db.AiEmbeddings.Where(e => e.OwnerType == "knowledge" && knowledge.Contains(e.OwnerId)).ExecuteDelete();
        Count("knowledge", db.KnowledgeItems.Where(k => knowledge.Contains(k.Id)).ExecuteDelete());
        Count("skills", db.AgentSkills.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        Count("quick_replies", db.CannedResponses.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        Count("script_templates", db.ScriptTemplates.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        Count("flows", db.Workflows.Where(w => workflows.Contains(w.Id)).ExecuteDelete());
        Count("automation_rules", db.AutomationRules.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        Count("tags", db.Tags.Where(t => s.TagIds.Contains(t.Id)).ExecuteDelete());
        Count("integrations", db.Integrations.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        db.Integrations.Where(x => x.OwnerUserId != null && users.Contains(x.OwnerUserId.Value))
            .ExecuteUpdate(u => u.SetProperty(x => x.OwnerUserId, (int?)null));
        Count("settings", db.Settings.Where(x => x.EntityId != null && ents.Contains(x.EntityId.Value)).ExecuteDelete());
        Count("team_members", db.TeamMembers.Where(m => s.TeamIds.Contains(m.TeamId) || users.Contains(m.UserId)).ExecuteDelete());
        db.Threads.Where(t => t.TeamId != null && s.TeamIds.Contains(t.TeamId.Value))
            .ExecuteUpdate(u => u.SetProperty(t => t.TeamId, (int?)null));
        Count("teams", db.Teams.Where(t => s.TeamIds.Contains(t.Id)).ExecuteDelete());
        db.OutboundJobs.Where(j => j.AccountId != null && accounts.Contains(j.AccountId.Value))
            .ExecuteUpdate(u => u.SetProperty(j => j.AccountId, (int?)null));
        db.Threads.Where(t => t.AccountId != null && accounts.Contains(t.AccountId.Value))
            .ExecuteUpdate(u => u.SetProperty(t => t.AccountId, (int?)null));
        Count("channel_accounts", db.ChannelAccounts.Where(a => accounts.Contains(a.Id)).ExecuteDelete());

        // ---- the lab's logins + role ----------------------------------------------------------
        db.AuditLogs.Where(a => a.UserId != null && users.Contains(a.UserId.Value))
            .ExecuteUpdate(u => u.SetProperty(a => a.UserId, (int?)null));
        db.SavedViews.Where(v => users.Contains(v.UserId)).ExecuteDelete();
        db.ApiKeys.Where(k => users.Contains(k.UserId)).ExecuteDelete();
        db.AssistantActions.Where(a => users.Contains(a.UserId)).ExecuteDelete();
        var convos = db.AssistantConversations.AsNoTracking().Where(c => users.Contains(c.UserId)).Select(c => c.Id).ToList();
        db.AssistantMessages.Where(m => convos.Contains(m.ConversationId)).ExecuteDelete();
        Count("assistant_conversations", db.AssistantConversations.Where(c => convos.Contains(c.Id)).ExecuteDelete());
        db.AuthTokens.Where(t => s.ProfileIds.Contains(t.ProfileId)).ExecuteDelete();
        // the profile ↔ entity join (no DbSet): cleared through the tracked profiles
        foreach (var p in db.Profiles.Include(p => p.Entities)
                     .Where(p => s.ProfileIds.Contains(p.Id) || p.Entities.Any(e => ents.Contains(e.Id))).ToList())
        {
            foreach (var e in p.Entities.Where(e => ents.Contains(e.Id) || s.ProfileIds.Contains(p.Id)).ToList())
                p.Entities.Remove(e);
        }
        db.SaveChanges();
        db.ChangeTracker.Clear();
        db.Profiles.Where(p => s.ProfileIds.Contains(p.Id)).ExecuteDelete();
        Count("users", db.Users.Where(u => users.Contains(u.Id)).ExecuteDelete());
        db.RolePermissions.Where(p => p.Role.StartsWith(LabFixture.RolePrefix)).ExecuteDelete();
        Count("roles", db.Roles.Where(r => r.Slug.StartsWith(LabFixture.RolePrefix)).ExecuteDelete());

        // ---- the entities, children first ------------------------------------------------------
        var remaining = new List<int>(ents);
        for (var guard = 0; remaining.Count > 0 && guard < 50; guard++)
        {
            var parents = db.Entities.AsNoTracking().Where(e => e.ParentId != null && remaining.Contains(e.ParentId.Value))
                .Select(e => e.ParentId!.Value).Distinct().ToList();
            var leaves = remaining.Where(id => !parents.Contains(id)).ToList();
            if (leaves.Count == 0) break;
            Count("entities", db.Entities.Where(e => leaves.Contains(e.Id)).ExecuteDelete());
            remaining = remaining.Except(leaves).ToList();
        }

        Count("custom_fields", RemoveLabFields(db));
        tx.Commit();
        return counts;
    }

    /// <summary>"/chats?channel=whatsapp&amp;key=601…" or "/chats?thread=whatsapp:601…" → the key.</summary>
    private static string? LinkedKey(string link)
    {
        var q = link.IndexOf('?');
        if (q < 0) return null;
        foreach (var part in link[(q + 1)..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            if (kv[0] == "key") return value;
            if (kv[0] == "thread" && value.IndexOf(':') is var c and > 0) return value[(c + 1)..];
        }
        return null;
    }

    /// <summary>Drop the lab's <c>testlab_*</c> fields from the org custom-field schema.</summary>
    public static int RemoveLabFields(AppDbContext db)
    {
        var row = db.Settings.FirstOrDefault(x => x.Key == "custom_fields" && x.EntityId == null);
        if (row == null || string.IsNullOrWhiteSpace(row.Value)) return 0;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(row.Value) as System.Text.Json.Nodes.JsonObject;
            if (node?["customer"] is not System.Text.Json.Nodes.JsonArray arr) return 0;
            var drop = arr.Where(f => (f?["key"]?.GetValue<string>() ?? "").StartsWith(LabFixture.FieldPrefix)).ToList();
            if (drop.Count == 0) return 0;
            foreach (var f in drop) arr.Remove(f);
            row.Value = node.ToJsonString();
            row.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
            return drop.Count;
        }
        catch { return 0; }
    }
}
