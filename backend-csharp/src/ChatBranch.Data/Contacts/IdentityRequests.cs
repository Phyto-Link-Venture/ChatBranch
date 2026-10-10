using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Contacts;

/// <summary>What a customer's reply said about who they are.</summary>
public record IdentityDetails(string Name, string Phone, string Email, bool Refused)
{
    public bool HasContactPoint => Phone.Length > 0 || Email.Length > 0;
    public bool Empty => Name.Length == 0 && !HasContactPoint;
}

/// <summary>
/// Pulls a name, a phone number and an email address out of a free-text reply
/// ("my name is Ali, 012-345 6789", "saya Aminah, aminah@x.com") —
/// deterministic on purpose (EN + BM keyword lists, no model), so the same
/// reply always yields the same outcome.
/// </summary>
public static class IdentityParser
{
    private static readonly Regex EmailRx = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled);
    private static readonly Regex PhoneRx = new(@"(?<![\w@])\+?\d[\d\s\-().]{6,20}\d(?![\w@])", RegexOptions.Compiled);
    private static readonly Regex NameLeadRx = new(
        @"(?:^|[\s,.!])(?:my\s+name\s+is|my\s+name'?s|name\s*[:\-]|i\s+am|i'?m|this\s+is|it'?s|"
        + @"nama\s+saya(?:\s+ialah|\s+adalah)?|nama\s*[:\-]|saya(?:\s+ialah|\s+adalah)?)\s+(?<n>[^\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Words that are never part of a name ("Hi, Ali here" → Ali).</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "hai", "helo", "salam", "assalamualaikum", "good", "morning", "afternoon", "evening",
        "my", "name", "is", "names", "phone", "number", "no", "num", "tel", "telephone", "mobile", "hp", "contact",
        "email", "emel", "e-mail", "mail", "and", "dan", "nombor", "telefon", "saya", "ialah", "adalah", "nama",
        "i", "am", "im", "i'm", "here", "it's", "its", "this", "thanks", "thank", "you", "terima", "kasih", "tq",
        "ok", "okay", "yes", "ya", "yup", "sure", "boleh", "is:", "from", "dari", "the", "or", "atau", "me",
        "call", "reach", "at", "on", "di", "pada",
    };

    /// <summary>A leftover containing one of these is a question, not a name.</summary>
    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "how", "why", "when", "where", "which", "who", "price", "prices", "cost", "much", "many",
        "can", "could", "would", "do", "does", "did", "have", "has", "want", "need", "please", "help", "order",
        "apa", "bila", "mana", "berapa", "harga", "macam", "kenapa", "mengapa", "nak", "mahu", "tolong",
        "ada", "tak", "tidak", "bukan", "kos", "beli", "open", "close", "hours", "today", "tomorrow",
        "not", "dont", "don't", "won't", "rather", "skip", "nope",
    };

    private static readonly string[] RefusalPhrases =
    {
        "no thanks", "no thank you", "don't want", "do not want", "dont want", "rather not", "not telling",
        "none of your business", "why do you need", "skip", "nope", "no need", "not now",
        "tak nak", "taknak", "tak mahu", "tidak mahu", "x nak", "tak payah", "tidak perlu", "tak perlu",
        "bukan urusan", "kenapa nak tahu", "rahsia",
    };

    private static readonly string[] MalayWords =
    {
        "saya", "nak", "boleh", "tak", "apa", "berapa", "ada", "tolong", "terima kasih", "selamat",
        "macam mana", "bila", "harga", "encik", "puan", "kami", "awak", "anda", "sila", "mohon", "nombor",
        "hai", "salam", "assalamualaikum", "mahu", "tidak", "kenapa", "beli", "kedai", "buka", "tutup", "ke ",
    };

    /// <summary>"ms" when the text reads as Malay (≥ 1 strong keyword), else "en".</summary>
    public static string Language(string? text)
    {
        var t = " " + (text ?? "").ToLowerInvariant() + " ";
        var hits = MalayWords.Count(w => Regex.IsMatch(t, $@"(?<![a-z]){Regex.Escape(w.Trim())}(?![a-z])"));
        return hits >= 1 ? "ms" : "en";
    }

    public static IdentityDetails Parse(string? text, string countryCode)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Length > 1000) return new IdentityDetails("", "", "", false);

        var email = EmailRx.Match(s) is { Success: true } em ? em.Value.Trim('.').ToLowerInvariant() : "";
        var rest = email.Length > 0 ? EmailRx.Replace(s, " ") : s;

        var phone = "";
        foreach (Match m in PhoneRx.Matches(rest))
        {
            var e164 = PhoneNormalizer.ToE164(m.Value, countryCode);
            if (PhoneMatch.Digits(e164).Length < 8) continue;
            phone = e164;
            rest = rest.Remove(m.Index, m.Length).Insert(m.Index, " ");
            break;
        }

        var lower = s.ToLowerInvariant();
        var refused = email.Length == 0 && phone.Length == 0
                      && (RefusalPhrases.Any(p => lower.Contains(p))
                          || Regex.IsMatch(lower.Trim(), @"^(no|tak|tidak|x|nah|nope)[.!]*$"));

        var name = refused ? "" : NameFrom(rest, email.Length > 0 || phone.Length > 0);
        return new IdentityDetails(name, phone, email, refused);
    }

    private static string NameFrom(string rest, bool hadContactPoint)
    {
        // "my name is Ali bin Abu, …" — take what follows the lead-in
        var lead = NameLeadRx.Match(rest);
        if (lead.Success)
        {
            var n = Clean(lead.Groups["n"].Value, max: 5);
            if (n.Length > 0) return n;
        }
        // otherwise the leftovers, only when they plainly ARE a name: next to a
        // phone/email, or a short reply on its own (the answer to our question)
        var words = Tokens(rest);
        if (words.Count == 0 || words.Count > 4) return "";
        if (words.Any(w => NotAName.Contains(w))) return "";
        if (!hadContactPoint && rest.Contains('?')) return "";
        return Clean(string.Join(' ', words), max: 4);
    }

    private static List<string> Tokens(string s) =>
        Regex.Split(s, @"[\s,;:/|()\[\]!?.]+")
            .Select(w => w.Trim('\'', '"', '-'))
            .Where(w => w.Length > 0 && !Filler.Contains(w))
            .ToList();

    private static string Clean(string raw, int max)
    {
        // stop at the next clause: "Ali, my email is…", "Ali and my number…"
        var cut = Regex.Split(raw, @"[,;\n.!?]|\s(?:and|dan|my|nombor|number|phone|email|emel|no|tel)\s",
            RegexOptions.IgnoreCase)[0];
        var words = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('\'', '"', '-', ':'))
            .Where(w => w.Length > 0 && w.All(ch => char.IsLetter(ch) || ch is '\'' or '-' or '/' or '@' or '.'))
            .Where(w => !w.Contains('@'))
            .Take(max).ToList();
        if (words.Count == 0 || words.Any(w => NotAName.Contains(w))) return "";
        if (words.All(w => Filler.Contains(w))) return "";
        var joined = string.Join(' ', words.Where(w => !Filler.Contains(w) || words.Count == 1));
        if (joined.Length < 2) return "";
        // "ali bin abu" → "Ali Bin Abu"; a name typed with capitals is kept as typed
        return joined == joined.ToLowerInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(joined)
            : joined;
    }
}

/// <summary>What the identity step did with one inbound message.</summary>
public record IdentityOutcome(string State, bool Asked, bool Linked, int? ContactId = null);

/// <summary>
/// "Ask who they are" for unknown customers (per inbox: <c>identify_unknown</c>
/// = off | ask | ask_and_create, see <see cref="IdentifySettings"/>; or the
/// automation action <c>ask_identity</c>). A deterministic state machine, one
/// <see cref="IdentityRequest"/> per conversation:
/// <list type="number">
/// <item>An unlinked conversation's first customer message (or an armed
///   request) → a short greeting asks for name + phone or email (EN / BM by the
///   customer's own language; the inbox may supply its own wording). A first
///   message that already carries a phone / email resolves at once.</item>
/// <item>Each reply is parsed (<see cref="IdentityParser"/>): one matching
///   contact in the tenant (phone last-8, else exact email) → linked; several →
///   handed over with the candidates listed; none → <c>ask_and_create</c>
///   creates the contact (receiving inbox's entity, tag <c>self-identified</c>),
///   <c>ask</c> leaves a note + sets the chat pending for a person to create it.</item>
/// <item>At most 2 asks; a refusal, or nothing usable after the second, stops
///   asking and leaves the chat to a human. Opted-out chats are never asked.</item>
/// </list>
/// Asks are SYSTEM messages (never the AI gate): sandbox sends inline, a live
/// account queues the ask so quiet hours and pacing apply. Once linked, the
/// normal coverage gate opens and the inbound's AI step answers the customer.
/// Never throws into an inbound path.
/// </summary>
public class IdentityRequestService
{
    public const int MaxAsks = 2;
    public const int StaleHours = 24;
    public const string SelfIdentifiedTag = "self-identified";
    private const string LastMessageKey = "_last_message_id";

    private readonly AppDbContext _db;
    private readonly ScopingService _sc;
    private readonly ThreadStore _threads;
    private readonly OutboundSender? _sender;
    private readonly TimeProvider _clock;

    public IdentityRequestService(AppDbContext db, ScopingService sc, ThreadStore threads,
        OutboundSender? sender = null, TimeProvider? clock = null)
    {
        _db = db;
        _sc = sc;
        _threads = threads;
        _sender = sender;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public IdentityRequest? For(int threadId) => _db.IdentityRequests.FirstOrDefault(r => r.ThreadId == threadId);

    /// <summary>
    /// The automation action <c>ask_identity</c>: arm a request for this
    /// unlinked conversation — the ask goes out with the inbound step that
    /// follows (the event that fired the rule IS a customer message for
    /// conversation_created / message_created). One request per conversation:
    /// a finished one is never re-armed, so a rule cannot loop.
    /// </summary>
    public static bool Arm(AppDbContext db, Thread thread, string? mode)
    {
        try
        {
            if (thread.ContactId != null || thread.OptedOut || thread.Id == 0) return false;
            if (db.IdentityRequests.Any(r => r.ThreadId == thread.Id)) return false;
            var m = (mode ?? "").Trim().ToLowerInvariant();
            if (m != IdentifyModes.AskAndCreate) m = IdentifyModes.Ask;
            var now = DateTime.UtcNow;
            db.IdentityRequests.Add(new IdentityRequest
            {
                ThreadId = thread.Id, State = IdentityStates.Armed, Mode = m,
                StartedAt = now, UpdatedAt = now, Note = "armed by an automation rule",
            });
            return true;   // AutomationService.Run saves
        }
        catch { return false; }
    }

    /// <summary>
    /// The inbound step (after STOP/START and the automations, before the AI
    /// step). Null = nothing to do. Never throws.
    /// </summary>
    public IdentityOutcome? OnInbound(Thread thread, Message? inbound)
    {
        try { return Step(thread, inbound); }
        catch { return null; }
    }

    private IdentityOutcome? Step(Thread thread, Message? inbound)
    {
        var req = For(thread.Id);
        var open = req != null && IdentityStates.Open.Contains(req.State);
        if (thread.OptedOut)
        {
            if (open) Close(req!, IdentityStates.Cancelled, "The customer opted out — stopped asking who they are.");
            return null;
        }
        if (thread.ContactId != null)
        {
            if (open) Close(req!, IdentityStates.Cancelled, null);
            return null;
        }
        if (inbound == null || inbound.Direction != MessageDirection.In) return null;
        if (thread.Channel == ContactIdentityService.VoiceChannel) return null;

        var account = AccountOf(thread);
        var cc = PhoneNormalizer.CountryCode(_sc, thread.EntityId);
        if (req == null)
        {
            var mode = IdentifySettings.Mode(_sc, account, thread.EntityId);
            if (mode == IdentifyModes.Off) return null;
            var inbounds = _db.Messages.Count(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
            if (inbounds > 1) return null;   // only when a conversation STARTS
            req = new IdentityRequest
            {
                ThreadId = thread.Id, Mode = mode, State = IdentityStates.Armed, StartedAt = Now, UpdatedAt = Now,
            };
            _db.IdentityRequests.Add(req);
        }

        // a redelivered webhook hands the same stored message in again — once only
        if (Str(req.Collected, LastMessageKey) == inbound.Id.ToString()) return null;
        req.Collected = new Dictionary<string, object?>(req.Collected) { [LastMessageKey] = inbound.Id.ToString() };

        if (req.State == IdentityStates.Armed)
        {
            req.Language = IdentityParser.Language(inbound.Text);
            req.OriginalMessageId ??= inbound.Id;
            var first = IdentityParser.Parse(inbound.Text, cc);
            Merge(req, first);
            if (first.HasContactPoint) return Resolve(req, thread, account);
            Ask(req, thread, account, 1);
            return new IdentityOutcome(req.State, Asked: true, Linked: false);
        }
        if (req.State != IdentityStates.Asking) return null;

        var late = req.LastAskedAt is DateTime asked && Now - asked > TimeSpan.FromHours(StaleHours);
        var d = IdentityParser.Parse(inbound.Text, cc);
        Merge(req, d);
        if (d.Refused)
            return GiveUp(req, thread, "The customer preferred not to say who they are.");
        if (Ready(req, thread)) return Resolve(req, thread, account);
        if (late)
            return GiveUp(req, thread, $"The customer didn't say who they are within {StaleHours} hours.");
        if (req.Attempts >= MaxAsks)
            return GiveUp(req, thread, $"Asked {MaxAsks} times without a usable answer.");
        Ask(req, thread, account, req.Attempts + 1);
        return new IdentityOutcome(req.State, Asked: true, Linked: false);
    }

    private ChannelAccount? AccountOf(Thread thread) =>
        thread.Account ?? (thread.AccountId is int aid ? _db.ChannelAccounts.FirstOrDefault(a => a.Id == aid) : null);

    /// <summary>Phone-keyed chats already know the number — a name is enough there.</summary>
    private static bool KeyIsContactPoint(Thread thread) =>
        thread.Channel is ChatBranch.Domain.Channels.WhatsApp or ContactIdentityService.SmsChannel
            or ChatBranch.Domain.Channels.Email;

    private static bool Ready(IdentityRequest req, Thread thread)
    {
        var c = Collected(req);
        return c.HasContactPoint || (c.Name.Length > 0 && KeyIsContactPoint(thread));
    }

    public static IdentityDetails Collected(IdentityRequest req) =>
        new(Str(req.Collected, "name"), Str(req.Collected, "phone"), Str(req.Collected, "email"), false);

    private static void Merge(IdentityRequest req, IdentityDetails d)
    {
        var c = new Dictionary<string, object?>(req.Collected);
        if (d.Name.Length > 0) c["name"] = d.Name;
        if (d.Phone.Length > 0) c["phone"] = d.Phone;
        if (d.Email.Length > 0) c["email"] = d.Email;
        req.Collected = c;
    }

    // ---- asking ----------------------------------------------------------------

    public static string AskText(string language, string channel, int attempt, string? custom, string name)
    {
        var phoneKeyed = channel is ChatBranch.Domain.Channels.WhatsApp or ContactIdentityService.SmsChannel;
        if (attempt <= 1 && !string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        if (language == "ms")
        {
            if (attempt <= 1)
                return phoneKeyed
                    ? "Hai! Terima kasih kerana menghubungi kami. Supaya kami dapat membantu, boleh kongsikan nama anda "
                      + "(serta emel atau nombor telefon yang didaftarkan dengan kami, jika berbeza daripada nombor ini)?"
                    : "Hai! Terima kasih kerana menghubungi kami. Supaya kami dapat membantu, boleh kongsikan nama serta "
                      + "nombor telefon atau alamat emel anda?";
            return name.Length > 0
                ? $"Terima kasih, {name}! Boleh kongsikan juga nombor telefon atau alamat emel anda?"
                : "Maaf, saya kurang faham. Boleh hantar nama serta nombor telefon atau alamat emel anda?";
        }
        if (attempt <= 1)
            return phoneKeyed
                ? "Hi! Thanks for getting in touch. So we can help you properly, could you tell us your name "
                  + "(and the email or phone number you registered with us, if it's different from this one)?"
                : "Hi! Thanks for getting in touch. So we can help you properly, could you tell us your name "
                  + "and your phone number or email address?";
        return name.Length > 0
            ? $"Thanks, {name}! Could you also share your phone number or email address?"
            : "Sorry, I didn't quite catch that. Could you send your name and your phone number or email address?";
    }

    private void Ask(IdentityRequest req, Thread thread, ChannelAccount? account, int attempt)
    {
        var text = AskText(req.Language, thread.Channel, attempt, IdentifySettings.Message(account),
            Collected(req).Name);
        var now = Now;
        req.State = IdentityStates.Asking;
        req.Attempts = attempt;
        req.LastAskedAt = now;
        req.UpdatedAt = now;
        _db.SaveChanges();   // the request row exists before anything is sent

        // sandbox: inline (a demo that answers late looks broken); a live account:
        // queued as a system send, so quiet hours, pacing and the window apply
        if (_sender != null && !_sender.WouldSendLive(thread))
        {
            _sender.Send(thread, text, MessageSource.System, thread.AccountId);
            req.PendingJobId = null;
        }
        else
        {
            var job = new OutboundJob
            {
                Channel = thread.Channel, Kind = JobKind.Text, ToAddress = thread.Key,
                Payload = new Dictionary<string, object?>
                {
                    ["text"] = text, ["source"] = "system", ["identity_request"] = req.Id,
                    ["account_id"] = thread.AccountId,
                },
                AccountId = thread.AccountId, Status = JobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            };
            _db.OutboundJobs.Add(job);
            _db.SaveChanges();
            req.PendingJobId = job.Id;
        }
        _db.SaveChanges();
    }

    // ---- resolving -------------------------------------------------------------

    private IdentityOutcome Resolve(IdentityRequest req, Thread thread, ChannelAccount? account)
    {
        CancelPendingAsk(req);
        var c = Collected(req);
        var tenantId = TenantOf(thread, account);
        var candidates = Candidates(c, tenantId, account?.EntityId ?? thread.EntityId);
        if (candidates.Count == 1)
        {
            var match = candidates[0];
            LinkThread(thread, match);
            req.ContactId = match.Id;
            Finish(req, IdentityStates.Linked,
                $"Customer identified as {match.Name} ({How(c, match)}) — conversation linked to the contact.");
            Audit.AuditTrail.Add(_db, "contact.self_identified", $"{thread.Channel}:{thread.Key}",
                $"matched contact {match.Id} ({match.Name})", actor: "identity", source: AuditSources.System,
                entityId: thread.EntityId);
            _db.SaveChanges();
            return new IdentityOutcome(req.State, Asked: false, Linked: true, match.Id);
        }
        if (candidates.Count > 1)
        {
            var list = string.Join("; ", candidates.Take(6).Select(x =>
                $"{x.Name} (#{x.Id}{(x.Phone.Length > 0 ? ", " + x.Phone : "")}{(x.Email.Length > 0 ? ", " + x.Email : "")})"));
            Finish(req, IdentityStates.Ambiguous,
                $"The customer says they are {Describe(c, thread)} — that matches {candidates.Count} contacts: {list}. "
                + "Link the right one from the contact panel.");
            Handover(thread, "Unknown customer matches several contacts", Describe(c, thread));
            _db.SaveChanges();
            return new IdentityOutcome(req.State, Asked: false, Linked: false);
        }
        if (req.Mode == IdentifyModes.AskAndCreate)
        {
            var created = CreateContact(req, thread, account, tenantId);
            req.ContactId = created.Id;
            Finish(req, IdentityStates.Created,
                $"New contact created from the conversation: {Describe(c, thread)} — conversation linked.");
            Audit.AuditTrail.Add(_db, "contact.self_identified", $"{thread.Channel}:{thread.Key}",
                $"created contact {created.Id} ({created.Name})", actor: "identity", source: AuditSources.System,
                entityId: thread.EntityId);
            _db.SaveChanges();
            return new IdentityOutcome(req.State, Asked: false, Linked: true, created.Id);
        }
        Finish(req, IdentityStates.NeedsContact,
            $"Unknown customer says they are {Describe(c, thread)} — no matching contact. "
            + "Use \"Create contact\" on the chat to add them.");
        Handover(thread, "Unknown customer — create the contact", Describe(c, thread));
        _db.SaveChanges();
        return new IdentityOutcome(req.State, Asked: false, Linked: false);
    }

    private static string How(IdentityDetails c, Contact match) =>
        c.Phone.Length > 0 && PhoneMatch.Same(c.Phone, match.Phone) ? "matched by phone" : "matched by email";

    private static string Describe(IdentityDetails c, Thread thread)
    {
        var parts = new List<string>();
        if (c.Name.Length > 0) parts.Add(c.Name);
        if (c.Phone.Length > 0) parts.Add(c.Phone);
        if (c.Email.Length > 0) parts.Add(c.Email);
        return parts.Count > 0 ? string.Join(" · ", parts) : thread.Name;
    }

    /// <summary>Contacts in the tenant matching the phone (last 8), else the exact email.</summary>
    public List<Contact> Candidates(IdentityDetails c, int? tenantId, int? entityId = null)
    {
        IQueryable<Contact> q = _db.Contacts;
        if (tenantId is int tid) q = q.Where(x => x.TenantId == tid);
        var outv = new List<Contact>();
        if (PhoneMatch.Tail(c.Phone) is string tail)
            outv = ForEntity(q.Where(x => x.Phone != "").AsEnumerable()
                .Where(x => PhoneMatch.Tail(x.Phone) == tail).OrderBy(x => x.Id).ToList(), entityId);
        if (outv.Count == 0 && c.Email.Length > 0)
        {
            var e = c.Email.ToLower();
            outv = ForEntity(q.Where(x => x.Email.ToLower() == e).OrderBy(x => x.Id).ToList(), entityId);
        }
        return outv;
    }

    /// <summary>
    /// The matches that belong to the conversation's entity, per company
    /// (<see cref="ContactCompanies"/>): those linked inside its subtree, else
    /// inside its company, else the organization-wide ones — never another
    /// company's contact. No entity = every match (the old behaviour).
    /// </summary>
    private List<Contact> ForEntity(List<Contact> matches, int? entityId)
    {
        if (entityId is not int eid || matches.Count == 0) return matches;
        var ids = matches.Select(m => m.Id).ToList();
        var links = _db.ContactEntities.AsNoTracking().Where(l => ids.Contains(l.ContactId))
            .Select(l => new { l.ContactId, l.EntityId }).ToList();
        var subtree = _sc.EntitySubtreeIds(eid).ToHashSet();
        var companyTree = _sc.EntitySubtreeIds(ContactCompanies.CompanyOf(_sc, eid)).ToHashSet();
        List<int> Of(int id) => links.Where(l => l.ContactId == id).Select(l => l.EntityId).ToList();
        var inSubtree = matches.Where(m => Of(m.Id).Any(subtree.Contains)).ToList();
        if (inSubtree.Count > 0) return inSubtree;
        var inCompany = matches.Where(m => Of(m.Id).Any(companyTree.Contains)).ToList();
        if (inCompany.Count > 0) return inCompany;
        return matches.Where(m => Of(m.Id).Count == 0).ToList();
    }

    /// <summary>
    /// The tenant a conversation belongs to: its (or its inbox's) entity's
    /// tenant; an organization-wide inbox in a single-tenant install belongs to
    /// that one tenant (a tenant-less contact would be invisible to its admins).
    /// </summary>
    private int? TenantOf(Thread thread, ChannelAccount? account)
    {
        var entityId = thread.EntityId ?? account?.EntityId;
        if (entityId is int eid)
            return _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault();
        var tenants = _db.Tenants.AsNoTracking().Select(t => t.Id).Take(2).ToList();
        return tenants.Count == 1 ? tenants[0] : null;
    }

    private void LinkThread(Thread thread, Contact contact)
    {
        thread.ContactId = contact.Id;
        thread.Contact = contact;
        if (thread.AccountId is int aid && thread.Account == null)
            thread.Account = _db.ChannelAccounts.FirstOrDefault(a => a.Id == aid);
        _sc.SyncThreadEntity(thread, save: false);
        thread.UpdatedAt = Now;
    }

    /// <summary>The contact a self-identified customer becomes (ask_and_create, or the chat's quick action).</summary>
    public Contact CreateContact(IdentityRequest? req, Thread thread, ChannelAccount? account, int? tenantId,
        IdentityDetails? overrideDetails = null, string actor = "identity", bool tagSelfIdentified = true,
        int? entityOverride = null, string? language = null)
    {
        var c = overrideDetails ?? (req != null ? Collected(req) : new IdentityDetails("", "", "", false));
        var cc = PhoneNormalizer.CountryCode(_sc, thread.EntityId);
        var phone = c.Phone.Length > 0 ? c.Phone
            : thread.Channel is ChatBranch.Domain.Channels.WhatsApp or ContactIdentityService.SmsChannel
                ? PhoneNormalizer.ToE164("+" + PhoneMatch.Digits(thread.Key), cc)
                : "";
        var email = c.Email.Length > 0 ? c.Email
            : thread.Channel == ChatBranch.Domain.Channels.Email && thread.Key.Contains('@') ? thread.Key.ToLowerInvariant()
            : "";
        var name = c.Name.Length > 0 ? c.Name
            : thread.Name.Length > 0 && !thread.Name.Any(char.IsDigit) && thread.Name != thread.Key ? thread.Name
            : phone.Length > 0 ? phone : email.Length > 0 ? email : "Unknown customer";
        var now = Now;
        var contact = new Contact
        {
            Name = name, Phone = phone, Email = email,
            Language = language is { Length: > 0 } lang ? lang : req?.Language is "ms" ? "ms" : "en",
            Status = "active", TenantId = tenantId, CreatedAt = now, UpdatedAt = now,
        };
        _db.Contacts.Add(contact);
        _db.SaveChanges();
        var entityId = entityOverride ?? account?.EntityId ?? thread.EntityId;
        if (entityId is int eid)
        {
            _db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = eid, CreatedAt = now });
            _db.SaveChanges();
        }
        if (tagSelfIdentified)
            try { TagService.ApplyContactNames(_db, contact.Id, new[] { SelfIdentifiedTag }, actor); } catch { }
        try { new ContactIdentityService(_db, _sc, _clock).Discover(contact, "self"); } catch { }
        LinkThread(thread, contact);
        _db.SaveChanges();
        return contact;
    }

    private IdentityOutcome GiveUp(IdentityRequest req, Thread thread, string why)
    {
        CancelPendingAsk(req);
        var c = Collected(req);
        Finish(req, IdentityStates.GaveUp,
            why + (c.Empty ? "" : $" Collected so far: {Describe(c, thread)}.") + " Left for a person to follow up.");
        Handover(thread, "Unknown customer didn't say who they are", why);
        _db.SaveChanges();
        return new IdentityOutcome(req.State, Asked: false, Linked: false);
    }

    private void Finish(IdentityRequest req, string state, string note)
    {
        var now = Now;
        req.State = state;
        req.Note = note;
        req.ResolvedAt = now;
        req.UpdatedAt = now;
        var thread = _db.Threads.Local.FirstOrDefault(t => t.Id == req.ThreadId)
                     ?? _db.Threads.FirstOrDefault(t => t.Id == req.ThreadId);
        if (thread != null) NoteOn(thread, note);
    }

    /// <summary>
    /// The customer never answered: a request still asking 24 hours after its
    /// last ask stops — note, chat pending, people notified. Driven by the
    /// lookup worker (and safe to call any time). Returns how many it closed.
    /// </summary>
    public int SweepStale()
    {
        var cutoff = Now.AddHours(-StaleHours);
        var stale = _db.IdentityRequests
            .Where(r => r.State == IdentityStates.Asking && r.LastAskedAt != null && r.LastAskedAt < cutoff)
            .OrderBy(r => r.Id).Take(200).ToList();
        var closed = 0;
        foreach (var req in stale)
        {
            try
            {
                var thread = _db.Threads.FirstOrDefault(t => t.Id == req.ThreadId);
                if (thread == null || thread.ContactId != null)
                {
                    Close(req, IdentityStates.Cancelled, null);
                    continue;
                }
                GiveUp(req, thread, $"The customer didn't say who they are within {StaleHours} hours.");
                closed++;
            }
            catch { /* one bad row never stops the sweep */ }
        }
        return closed;
    }

    /// <summary>Close an open request without an outcome (opt-out, linked by hand).</summary>
    public void Close(IdentityRequest req, string state, string? note)
    {
        CancelPendingAsk(req);
        var now = Now;
        req.State = state;
        if (note != null) req.Note = note;
        req.ResolvedAt = now;
        req.UpdatedAt = now;
        if (note != null)
        {
            var thread = _db.Threads.FirstOrDefault(t => t.Id == req.ThreadId);
            if (thread != null) NoteOn(thread, note);
        }
        _db.SaveChanges();
    }

    private void CancelPendingAsk(IdentityRequest req)
    {
        if (req.PendingJobId is not int jid) return;
        var job = _db.OutboundJobs.FirstOrDefault(j => j.Id == jid && j.Status == JobStatus.Queued);
        if (job != null)
        {
            job.Status = JobStatus.Cancelled;
            job.LastError = "the customer answered before the ask went out";
            job.UpdatedAt = Now;
        }
        req.PendingJobId = null;
    }

    /// <summary>Hand the chat to a person: a tracked handover ("Needs human"),
    /// pending + an admin notification. The identity exchange writes its own
    /// note, and the chatbot stays as it was — once a person links the contact
    /// the AI may answer.</summary>
    private void Handover(Thread thread, string title, string detail)
    {
        if (thread.Status != ThreadStatus.Resolved) thread.Status = ThreadStatus.Pending;
        thread.UpdatedAt = Now;
        ChatBranch.Data.Ai.Handover.Mark(_db, thread, ChatBranch.Data.Ai.HandoverKinds.UnknownCustomer,
            $"{title}: {detail}", "identity", note: false, notify: false, save: false, standDown: false);
        try
        {
            NotificationService.Notify(_db, NotificationCategories.Messages, "identity", title,
                $"{thread.Name} ({thread.Channel}) — {detail}",
                $"/chats?thread={Uri.EscapeDataString(thread.Channel)}:{Uri.EscapeDataString(thread.Key)}",
                NotificationAudience.Admins(TenantOf(thread, AccountOf(thread))),
                new NotifyOptions { Thread = thread, Save = false, RefKey = $"identity:{thread.Id}" });
        }
        catch { /* a notification never breaks the inbound */ }
    }

    private void NoteOn(Thread thread, string text)
    {
        _db.Messages.Add(new Message
        {
            ThreadId = thread.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
            Private = true, Text = text, Status = "note", AccountId = thread.AccountId, CreatedAt = Now,
        });
        thread.UpdatedAt = Now;
    }

    private static string Str(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return "";
        if (v is JsonElement el) return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        return v.ToString() ?? "";
    }

    // ---- by hand (the chat's "Create contact" quick action) ----------------------

    public int? TenantFor(Thread thread) => TenantOf(thread, AccountOf(thread));

    public ChannelAccount? AccountFor(Thread thread) => AccountOf(thread);

    /// <summary>
    /// A person resolved the conversation: link it to <paramref name="contact"/>,
    /// close the request (if any) with <paramref name="state"/>, and leave the
    /// note. True when the customer had been waiting on the "who are you?"
    /// exchange (the caller then lets the AI answer them once).
    /// </summary>
    public bool ResolveByHand(Thread thread, Contact contact, string state, string note)
    {
        var req = For(thread.Id);
        var waiting = req != null && req.State is IdentityStates.Asking or IdentityStates.Armed
            or IdentityStates.NeedsContact or IdentityStates.GaveUp or IdentityStates.Ambiguous;
        if (thread.ContactId != contact.Id) LinkThread(thread, contact);
        if (req != null && req.State is not (IdentityStates.Linked or IdentityStates.Created))
        {
            CancelPendingAsk(req);
            req.ContactId = contact.Id;
            req.State = state;
            req.Note = note;
            req.ResolvedAt = Now;
            req.UpdatedAt = Now;
        }
        NoteOn(thread, note);
        if (thread.Status == ThreadStatus.Pending && waiting) thread.Status = ThreadStatus.Open;
        if (thread.HandoverKind == ChatBranch.Data.Ai.HandoverKinds.UnknownCustomer)
            ChatBranch.Data.Ai.Handover.Clear(_db, thread, "identity", "The contact was linked", save: false);
        _db.SaveChanges();
        return waiting;
    }

    /// <summary>The create form's prefill: what the customer said, else what the chat itself knows.</summary>
    public IdentityDetails Suggest(Thread thread, IdentityRequest? req)
    {
        var c = req != null ? Collected(req) : new IdentityDetails("", "", "", false);
        var cc = PhoneNormalizer.CountryCode(_sc, thread.EntityId);
        var phone = c.Phone.Length > 0 ? c.Phone
            : thread.Channel is ChatBranch.Domain.Channels.WhatsApp or ContactIdentityService.SmsChannel
                ? PhoneNormalizer.ToE164("+" + PhoneMatch.Digits(thread.Key), cc) : "";
        var email = c.Email.Length > 0 ? c.Email
            : thread.Channel == ChatBranch.Domain.Channels.Email && thread.Key.Contains('@') ? thread.Key.ToLowerInvariant() : "";
        var name = c.Name.Length > 0 ? c.Name
            : thread.Name.Length > 0 && !thread.Name.Any(char.IsDigit) && thread.Name != thread.Key ? thread.Name : "";
        return new IdentityDetails(name, phone, email, false);
    }

    /// <summary>Wire shape for the chat banner / API.</summary>
    public static Dictionary<string, object?> Payload(IdentityRequest r)
    {
        var c = Collected(r);
        return new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["thread_id"] = r.ThreadId,
            ["state"] = r.State,
            ["mode"] = r.Mode,
            ["attempts"] = r.Attempts,
            ["max_asks"] = MaxAsks,
            ["language"] = r.Language,
            ["collected"] = new Dictionary<string, object?> { ["name"] = c.Name, ["phone"] = c.Phone, ["email"] = c.Email },
            ["contact_id"] = r.ContactId,
            ["note"] = r.Note,
            ["open"] = IdentityStates.Open.Contains(r.State),
            ["started_at"] = Iso(r.StartedAt),
            ["last_asked_at"] = r.LastAskedAt is DateTime a ? Iso(a) : null,
            ["resolved_at"] = r.ResolvedAt is DateTime d ? Iso(d) : null,
        };
    }

    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o");
}

/// <summary>
/// The per-inbox "unknown customers" setting, stored next to the other inbox
/// keys in <see cref="ChannelAccount.Config"/> (<c>identify_unknown</c> =
/// off | ask | ask_and_create, optional <c>identify_message</c>). An inbox
/// without the key inherits the Setting <c>identify_unknown</c>, else off — so
/// an existing install changes nothing until someone turns it on.
/// </summary>
public static class IdentifySettings
{
    public const string ModeKey = "identify_unknown";
    public const string MessageKey = "identify_message";

    /// <summary>The inbox's own value ("" = inherit).</summary>
    public static string ModeWire(ChannelAccount? account)
    {
        var raw = account != null && account.Config.TryGetValue(ModeKey, out var v) && v != null
            ? (v is JsonElement el ? el.ToString() : v.ToString() ?? "") : "";
        raw = raw.Trim().ToLowerInvariant();
        return IdentifyModes.All.Contains(raw) ? raw : "";
    }

    public static string Mode(ScopingService sc, ChannelAccount? account, int? entityId)
    {
        var own = ModeWire(account);
        if (own.Length > 0) return own;
        string inherited;
        try { inherited = sc.SettingValue(ModeKey, entityId).Trim().ToLowerInvariant(); }
        catch { inherited = ""; }
        return IdentifyModes.All.Contains(inherited) ? inherited : IdentifyModes.Off;
    }

    public static string? Message(ChannelAccount? account)
    {
        if (account == null || !account.Config.TryGetValue(MessageKey, out var v) || v == null) return null;
        var s = v is JsonElement el ? (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString()) : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Apply a write (null = leave alone, "" = inherit/clear). Returns an error or null; the caller saves.</summary>
    public static string? Apply(ChannelAccount account, string? modeRaw, string? messageRaw)
    {
        var cfg = new Dictionary<string, object?>(account.Config);
        if (modeRaw != null)
        {
            var m = modeRaw.Trim().ToLowerInvariant();
            if (m is "" or "inherit" or "null") cfg.Remove(ModeKey);
            else if (IdentifyModes.All.Contains(m)) cfg[ModeKey] = m;
            else return "identify_unknown must be off, ask, ask_and_create or empty (inherit).";
        }
        if (messageRaw != null)
        {
            var t = messageRaw.Trim();
            if (t.Length > 600) return "identify_message is limited to 600 characters.";
            if (t.Length == 0) cfg.Remove(MessageKey);
            else cfg[MessageKey] = t;
        }
        account.Config = cfg;
        return null;
    }
}
