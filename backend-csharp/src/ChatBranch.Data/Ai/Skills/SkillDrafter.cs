using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// Describe → clarify → code. The operator describes a skill in plain words;
/// the model either asks up to four clarifying questions or returns a complete
/// draft (name, kind, use-when, parameters, JavaScript, test cases) that the
/// wizard then runs in the sandbox before anything is saved.
///
/// With no LLM configured (sandbox mode) a deterministic template library
/// answers instead — opening hours, time, week, dates, phone numbers, email
/// addresses and Malaysian IC numbers — so the whole flow is usable and
/// testable with zero credentials. Anything else gets a commented skeleton
/// after one clarifying question.
/// </summary>
public class SkillDrafter
{
    private readonly ILlmClient _llm;
    private readonly AppDbContext _db;

    public SkillDrafter(ILlmClient llm, AppDbContext db)
    {
        _llm = llm;
        _db = db;
    }

    public sealed record Answer(string Question, string Text);

    public sealed record DraftSkill(string Name, string Kind, string UseWhen, string Description,
        JsonElement Parameters, string Code, JsonElement Tests, string Instructions, string OnFail);

    public sealed record DraftResult(string Status, IReadOnlyList<string> Questions, DraftSkill? Skill,
        string Source, string Note);

    public async Task<DraftResult> DraftAsync(string description, string? kindHint,
        IReadOnlyList<Answer> answers, CancellationToken ct = default)
    {
        description = (description ?? "").Trim();
        var hint = NormalizeHint(kindHint);
        if (_llm.IsConfigured)
        {
            try
            {
                var viaLlm = await DraftWithLlmAsync(description, hint, answers, ct);
                if (viaLlm != null) return viaLlm;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* fall through to the template library */ }
            var fallback = Template(description, hint, answers);
            return fallback with { Note = "The AI's answer could not be read, so this is a starting template. " + fallback.Note };
        }
        return Template(description, hint, answers);
    }

    private static string NormalizeHint(string? h) => (h ?? "").Trim().ToLowerInvariant() switch
    {
        "tool" => SkillKinds.Tool,
        "check" or "reply_check" or "reply check" => SkillKinds.Check,
        "prompt" or "instructions" or "instruction" => SkillKinds.Prompt,
        _ => "",
    };

    // ---- the LLM path -----------------------------------------------------------------

    public const string SystemPrompt = """
        You help a business operator turn a plain-language description into an AI SKILL for a
        customer-conversation assistant. There are three kinds:

        - "prompt": instructions the assistant follows when a situation arises (no code).
        - "tool": a JavaScript function the assistant CALLS to get a correct answer
          (dates, opening hours, prices from a fixed table, validations). Define
              function run(args, ctx) { ... return <JSON-serialisable value>; }
        - "check": a JavaScript validator run on EVERY reply before it is sent. Define
              function check(reply, ctx) { ... return { ok: true } or { ok: false, reason: "...", fix: "..." }; }

        The JavaScript runs in a strict sandbox: ES2020, no network, no files, no require/import,
        no timers, no eval, 250 ms and 8 MB budget. Available inputs:
          ctx.now (ISO date-time in the business timezone), ctx.timezone, ctx.today ("YYYY-MM-DD"),
          ctx.time ("HH:mm"), ctx.weekday ("Monday"…), ctx.isoWeekday (1-7), ctx.hour, ctx.minute,
          ctx.channel, ctx.lastMessage (the customer's latest message),
          ctx.contact { name, firstName, language, tags[], company, fields{} }.
        Helpers on plain "YYYY-MM-DD" strings: dates.addDays(d,n), dates.addBusinessDays(d,n),
          dates.daysBetween(a,b), dates.isoWeek(d) -> {year, week}, dates.isoWeekday(d),
          dates.weekday(d), dates.weekStart(d), dates.weekEnd(d), dates.isWeekend(d),
          dates.minutes("HH:mm"), dates.format(Date), dates.parse(d).
        Keep code short, readable and commented; put business data (hours, lists) in constants at
        the top so the operator can edit them.

        Ask clarifying questions ONLY when an answer would change the code (e.g. the actual
        opening hours, which countries' phone numbers, what exactly must never be said). Ask at
        most 4 short questions, and never repeat a question that already has an answer.

        Tests: 3-6 cases. Tool case: {"name": "...", "args": {...}, "expect": {...subset of the
        result...}, "now": "YYYY-MM-DDTHH:mm:ss" (optional fixed clock)}. Check case:
        {"name": "...", "input": "<a reply>", "expect": true|false, "now": optional}. Include at
        least one passing and one failing case for checks. Use a fixed "now" whenever the result
        depends on the time.

        Respond with ONE JSON object and nothing else, either
          {"status":"questions","questions":["...", "..."]}
        or
          {"status":"draft","skill":{"name":"<a short title, 2-5 words, no trailing 'about/for/when'>","kind":"tool|check|prompt",
            "use_when":"...","description":"...","parameters":{JSON schema for args, tools only},
            "code":"...","tests":[...],"instructions":"... (prompt kind only)",
            "on_fail":"retry|handover|block (checks only)"}}
        """;

    private async Task<DraftResult?> DraftWithLlmAsync(string description, string hint,
        IReadOnlyList<Answer> answers, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Description:").AppendLine(description.Length > 0 ? description : "(empty)");
        sb.AppendLine().AppendLine(hint.Length > 0
            ? $"The operator wants kind: {hint}."
            : "Pick the kind that fits best.");
        if (answers.Count > 0)
        {
            sb.AppendLine().AppendLine("Answers to your earlier questions:");
            foreach (var a in answers) sb.AppendLine($"Q: {a.Question}").AppendLine($"A: {a.Text}");
            if (answers.Count >= 8) sb.AppendLine("Do not ask more questions — produce the draft now.");
        }
        var r = await _llm.ChatAsync(SystemPrompt, new[] { new LlmMessage("user", sb.ToString()) },
            null, null, 0.2, ct);
        RecordUsage(r);
        return Parse(r.Text, hint);
    }

    private void RecordUsage(LlmChatResult r)
    {
        try
        {
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = "skill_draft", Provider = r.Provider, Model = r.Model, UserName = AiQuota.CurrentUser(),
                PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens, CostUsd = r.CostUsd,
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* metering never breaks authoring */ }
    }

    /// <summary>Read the model's JSON answer (tolerating code fences / prose around it).</summary>
    public static DraftResult? Parse(string text, string hint)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        using var doc = JsonDocument.Parse(text[start..(end + 1)]);
        var root = doc.RootElement;
        var status = S(root, "status");
        if (status == "questions" && root.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            var list = qs.EnumerateArray().Select(q => q.ValueKind == JsonValueKind.String ? q.GetString() ?? "" : q.ToString())
                .Where(q => q.Trim().Length > 0).Take(4).ToList();
            if (list.Count > 0) return new DraftResult("questions", list, null, "ai", "");
        }
        if (!root.TryGetProperty("skill", out var sk) || sk.ValueKind != JsonValueKind.Object) return null;
        var kind = S(sk, "kind") is { Length: > 0 } k && (k is SkillKinds.Tool or SkillKinds.Check or SkillKinds.Prompt)
            ? k : (hint.Length > 0 ? hint : SkillKinds.Tool);
        var code = S(sk, "code");
        if (SkillKinds.IsCode(kind) && code.Trim().Length == 0) return null;
        var parameters = sk.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.Clone() : Json("{\"type\":\"object\",\"properties\":{}}");
        var tests = sk.TryGetProperty("tests", out var t) && t.ValueKind == JsonValueKind.Array ? t.Clone() : Json("[]");
        var name = S(sk, "name") is { Length: > 0 } n ? n : "New skill";
        return new DraftResult("draft", Array.Empty<string>(), new DraftSkill(
            ShortName(name, 80), kind, S(sk, "use_when"), S(sk, "description"),
            kind == SkillKinds.Tool ? parameters : Json("{}"), SkillKinds.IsCode(kind) ? code : "",
            SkillKinds.IsCode(kind) ? tests : Json("[]"),
            kind == SkillKinds.Prompt ? (S(sk, "instructions") is { Length: > 0 } ins ? ins : S(sk, "description")) : "",
            SkillService.NormalizeOnFail(S(sk, "on_fail"))), "ai", "");
    }

    private static string S(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ---- the sandbox template library ----------------------------------------------

    public static DraftResult Template(string description, string hint, IReadOnlyList<Answer> answers)
    {
        var all = (description + " " + string.Join(" ", answers.Select(a => a.Text))).ToLowerInvariant();
        var kind = hint.Length > 0 ? hint : GuessKind(description);

        if (kind == SkillKinds.Prompt) return PromptDraft(description);

        string? topic =
            Has(all, @"\b(business|opening|office|working|operating|store|shop)\s+hours?\b|\b(outside|after|out of)\s+(business\s+|opening\s+|office\s+|working\s+)?hours\b|\bopen(ing)?\s+(now|today|times?)\b|\b(are|is) (we|you|the (shop|store|office)) open\b|\bwe('| a)re open\b") ? "hours"
            : Has(all, @"\b(ic|nric|mykad|identity card|ic number|ic no)\b") ? "ic"
            : Has(all, @"\b(phone|mobile|whatsapp number|telephone|contact number)\b") ? "phone"
            : Has(all, @"\b(e-?mail|email address)\b") ? "email"
            : Has(all, @"\b(week|weekly|iso week|week number)\b") ? "week"
            : Has(all, @"\b(business days?|working days?|deadline|due date|delivery date|date|dates|days from|days until|tomorrow|next (monday|tuesday|wednesday|thursday|friday|saturday|sunday))\b") ? "date"
            : Has(all, @"\b(time|clock|today|weekday|day of the week|what day)\b") ? "time"
            : null;

        if (topic == "hours")
        {
            var hours = ParseHours(description + "\n" + string.Join("\n", answers.Select(a => a.Text)));
            if (hours == null && answers.Count == 0)
                return new DraftResult("questions", new[]
                {
                    "What are your opening hours for each day? (e.g. Mon–Fri 09:00–18:00, Sat 09:00–13:00, Sun closed)",
                }, null, "template", "Sandbox mode: no AI model is configured, so the built-in template library is asking.");
            var note = hours == null
                ? "Couldn't read the hours from your answer, so the usual Mon–Fri 09:00–18:00, Sat 09:00–13:00 is used — edit HOURS at the top of the code."
                : "Hours read from your answer — check the HOURS table at the top of the code.";
            return Done(kind == SkillKinds.Check ? HoursCheck(hours) : HoursTool(hours), note);
        }

        if (topic != null)
        {
            var draft = (topic, kind) switch
            {
                ("ic", SkillKinds.Check) => IcCheck(),
                ("ic", _) => IcTool(),
                ("phone", SkillKinds.Check) => PhoneCheck(),
                ("phone", _) => PhoneTool(),
                ("email", SkillKinds.Check) => EmailCheck(),
                ("email", _) => EmailTool(),
                ("week", SkillKinds.Check) => WeekCheck(),
                ("week", _) => WeekTool(),
                ("date", SkillKinds.Check) => DateCheck(),
                ("date", _) => DateTool(),
                (_, SkillKinds.Check) => TimeCheck(),
                _ => TimeTool(),
            };
            return Done(draft, "");
        }

        // Unrecognised: one clarifying question, then a commented skeleton.
        if (answers.Count == 0)
            return new DraftResult("questions", new[]
            {
                kind == SkillKinds.Check
                    ? "What exactly must a reply never (or always) contain? Give one reply that should FAIL and one that should PASS."
                    : "What should the tool return? Give one example input and the exact result you expect.",
            }, null, "template", "Sandbox mode: no AI model is configured, so a skeleton will be generated.");
        return Done(kind == SkillKinds.Check ? SkeletonCheck(description, answers) : SkeletonTool(description, answers),
            "No AI model is configured and this request matched no template, so this is a commented skeleton — fill in the rule, then add real test cases.");

        DraftResult Done(DraftSkill skill, string extra) => new("draft", Array.Empty<string>(),
            skill with { Description = description.Length > 0 ? description : skill.Description },
            "template", ("Sandbox mode: drafted from the built-in template library. " + extra).Trim());
    }

    public static string GuessKind(string description)
    {
        var d = description.ToLowerInvariant();
        int checkScore = Count(d, @"\b(never|must not|mustn't|should not|shouldn't|do not|don't|ensure|make sure|always|forbid|block|reject|replies|reply|answers?|respond|leak|mention|state|say)\b");
        int toolScore = Count(d, @"\b(calculate|compute|look ?up|get|find|convert|parse|normali[sz]e|format|tell|give|return|what is|how many|which|next|add|validate)\b");
        return checkScore > toolScore ? SkillKinds.Check : SkillKinds.Tool;
    }

    private static int Count(string s, string pattern) => Regex.Matches(s, pattern).Count;
    private static bool Has(string s, string pattern) => Regex.IsMatch(s, pattern);

    private static DraftResult PromptDraft(string description)
    {
        var when = "";
        var body = description;
        var m = Regex.Match(description, @"^\s*(?:when|whenever|if)\s+(.+?)[,:]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            when = m.Groups[1].Value.Trim();
            body = char.ToUpperInvariant(m.Groups[2].Value.Trim()[0]) + m.Groups[2].Value.Trim()[1..];
        }
        var skill = new DraftSkill(NameFrom(body, "New instruction"), SkillKinds.Prompt, when, description,
            Json("{}"), "", Json("[]"), body, "retry");
        return new DraftResult("draft", Array.Empty<string>(), skill, "template",
            "Sandbox mode: your description becomes the instructions — refine the wording and the use-when trigger.");
    }

    // words a title must not END on ("When a customer asks about" read as cut off)
    private static readonly HashSet<string> Dangling = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "for", "of", "about", "and", "or", "but", "with", "in", "on", "at", "by",
        "from", "into", "if", "when", "that", "which", "who", "is", "are", "be", "as", "their", "our", "your", "my",
    };

    /// <summary>A title cut at a word boundary (never mid-word), without a dangling last word.</summary>
    public static string ShortName(string name, int max)
    {
        name = Regex.Replace(name ?? "", @"\s+", " ").Trim().TrimEnd('.', ',', ';', ':');
        if (name.Length > max)
        {
            var cut = name[..max];
            var space = cut.LastIndexOf(' ');
            name = space > 0 ? cut[..space] : cut;
        }
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Dangling.Contains(words[^1].Trim(',', '.', ';', ':'))) words.RemoveAt(words.Count - 1);
        return string.Join(" ", words).TrimEnd(',', '.', ';', ':');
    }

    private static string NameFrom(string description, string fallback)
    {
        // up to 6 words / 40 characters, ending on a meaningful word
        var all = Regex.Matches(description, @"[A-Za-z0-9']+").Select(x => x.Value).ToList();
        var words = new List<string>();
        foreach (var w in all)
        {
            if (words.Count >= 6 || string.Join(" ", words.Append(w)).Length > 40) break;
            words.Add(w);
        }
        while (words.Count > 1 && Dangling.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count == 0) return fallback;
        var name = string.Join(" ", words);
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    // ---- opening hours ------------------------------------------------------------------

    private static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    /// <summary>
    /// Read "Mon-Fri 9am-6pm, Sat 9:00-13:00, Sun closed" style text into a
    /// day → (open, close) table. Null when nothing could be read.
    /// </summary>
    public static Dictionary<string, (string open, string close)?>? ParseHours(string text)
    {
        var table = new Dictionary<string, (string, string)?>();
        const string day = @"(?:mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)[a-z]*";
        const string open = @"(?<oh>\d{1,2})(?:[:.](?<om>\d{2}))?\s*(?<oa>am|pm)?";
        const string close = @"(?<ch>\d{1,2})(?:[:.](?<cm>\d{2}))?\s*(?<ca>am|pm)?";
        var segRe = new Regex(
            $@"(?<days>weekdays|weekends|daily|every\s*day|everyday|{day}(?:\s*(?:-|–|to|until)\s*{day})?(?:\s*(?:,|&|and)\s*{day})*)\s*:?\s*(?:(?<closed>closed|off)|{open}\s*(?:-|–|to|until)\s*{close})",
            RegexOptions.IgnoreCase);
        foreach (Match m in segRe.Matches(text))
        {
            var days = ExpandDays(m.Groups["days"].Value);
            if (days.Count == 0) continue;
            (string, string)? span = null;
            if (!m.Groups["closed"].Success)
            {
                var o = Hm(m.Groups["oh"], m.Groups["om"], m.Groups["oa"], null);
                var c = Hm(m.Groups["ch"], m.Groups["cm"], m.Groups["ca"], o);
                if (o == null || c == null) continue;
                span = (o, c);
            }
            foreach (var d in days) table[d] = span;
        }
        if (table.Count == 0) return null;
        foreach (var d in Days) table.TryAdd(d, null);
        return table;
    }

    /// <summary>One time of a segment as "HH:mm". `openedAt` marks a closing time:
    /// "9-6" with no am/pm reads the smaller closing hour as afternoon.</summary>
    private static string? Hm(Group hour, Group minute, Group ampm, string? openedAt)
    {
        if (!hour.Success || !int.TryParse(hour.Value, out var h)) return null;
        var min = minute.Success && int.TryParse(minute.Value, out var mm) ? mm : 0;
        var suffix = ampm.Success ? ampm.Value.ToLowerInvariant() : "";
        if (suffix == "pm" && h < 12) h += 12;
        if (suffix == "am" && h == 12) h = 0;
        if (openedAt != null && suffix.Length == 0 && h > 0 && h < 12 && int.Parse(openedAt[..2]) >= h) h += 12;
        if (h > 24 || min > 59) return null;
        return $"{h:00}:{min:00}";
    }

    private static List<string> ExpandDays(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.StartsWith("weekday")) return Days.Take(5).ToList();
        if (s.StartsWith("weekend")) return Days.Skip(5).ToList();
        if (s.StartsWith("daily") || s.StartsWith("every")) return Days.ToList();
        int Idx(string token) => Array.FindIndex(Days, d => d.ToLowerInvariant().StartsWith(token[..3]));
        var tokens = Regex.Matches(s, @"(mon|tue|wed|thu|fri|sat|sun)[a-z]*").Select(x => x.Value).ToList();
        if (tokens.Count == 0) return new List<string>();
        var isRange = Regex.IsMatch(s, @"(mon|tue|wed|thu|fri|sat|sun)[a-z]*\s*(-|–|to|until)\s*(mon|tue|wed|thu|fri|sat|sun)");
        if (isRange && tokens.Count == 2)
        {
            int a = Idx(tokens[0]), b = Idx(tokens[1]);
            var outv = new List<string>();
            for (var i = a; ; i = (i + 1) % 7) { outv.Add(Days[i]); if (i == b || outv.Count > 7) break; }
            return outv;
        }
        return tokens.Select(Idx).Where(i => i >= 0).Select(i => Days[i]).Distinct().ToList();
    }

    private static readonly Dictionary<string, (string, string)?> DefaultHours = new()
    {
        ["Monday"] = ("09:00", "18:00"), ["Tuesday"] = ("09:00", "18:00"), ["Wednesday"] = ("09:00", "18:00"),
        ["Thursday"] = ("09:00", "18:00"), ["Friday"] = ("09:00", "18:00"), ["Saturday"] = ("09:00", "13:00"),
        ["Sunday"] = null,
    };

    private static string HoursTable(Dictionary<string, (string open, string close)?>? hours)
    {
        hours ??= DefaultHours;
        var lines = Days.Select(d => hours.TryGetValue(d, out var v) && v is { } span
            ? $"  {d}: ['{span.open}', '{span.close}'],"
            : $"  {d}: null,");
        return "// Opening hours in the business timezone. null = closed all day.\nconst HOURS = {\n"
               + string.Join("\n", lines) + "\n};";
    }

    /// <summary>A fixed Monday/Sunday in the template tests, chosen from the table.</summary>
    private static (string openDay, string openTime, string closedDay) HoursFixtures(Dictionary<string, (string open, string close)?>? hours)
    {
        hours ??= DefaultHours;
        // Week of Mon 2026-10-05 … Sun 2026-10-11.
        var dates = Days.Select((d, i) => (d, date: $"2026-10-{5 + i:00}")).ToList();
        var openDay = dates.FirstOrDefault(x => hours.TryGetValue(x.d, out var v) && v != null);
        var closedDay = dates.FirstOrDefault(x => !hours.TryGetValue(x.d, out var v) || v == null);
        var openSpan = openDay.d != null ? hours[openDay.d]!.Value : ("09:00", "18:00");
        // a minute after opening
        var t = TimeOnly.Parse(openSpan.Item1).AddMinutes(1).ToString("HH:mm");
        return (openDay.date ?? "2026-10-05", t, closedDay.date ?? "");
    }

    private static DraftSkill HoursTool(Dictionary<string, (string open, string close)?>? hours)
    {
        var code = HoursTable(hours) + """


            // Is the business open right now, and if not, when does it next open?
            function run(args, ctx) {
              const today = HOURS[ctx.weekday];
              const now = dates.minutes(ctx.time);
              const open = !!today && now >= dates.minutes(today[0]) && now < dates.minutes(today[1]);
              return {
                open: open,
                today: ctx.weekday,
                hours_today: today ? today[0] + '-' + today[1] : 'closed',
                next_open: open ? null : nextOpening(ctx, now),
              };
            }

            function nextOpening(ctx, now) {
              for (let i = 0; i < 8; i++) {
                const day = dates.addDays(ctx.today, i);
                const h = HOURS[dates.weekday(day)];
                if (!h) continue;
                if (i === 0 && now >= dates.minutes(h[0])) continue;
                return { date: day, weekday: dates.weekday(day), at: h[0] };
              }
              return null;
            }
            """;
        var (openDate, openTime, closedDate) = HoursFixtures(hours);
        var tests = new List<object>
        {
            new { name = "Open just after opening time", args = new { }, now = $"{openDate}T{openTime}:00", expect = new { open = true } },
            new { name = "Closed late at night", args = new { }, now = $"{openDate}T23:30:00", expect = new { open = false } },
        };
        if (closedDate.Length > 0)
            tests.Add(new { name = "Closed on a day off", args = new { }, now = $"{closedDate}T11:00:00", expect = new { open = false, hours_today = "closed" } });
        return new DraftSkill("Opening hours", SkillKinds.Tool,
            "the customer asks whether we are open, our opening hours, or when we open next",
            "Tells whether the business is open now and when it next opens.",
            Json("{\"type\":\"object\",\"properties\":{}}"), code, JsonSerializer.SerializeToElement(tests), "", "retry");
    }

    private static DraftSkill HoursCheck(Dictionary<string, (string open, string close)?>? hours)
    {
        var code = HoursTable(hours) + """


            // Outside opening hours the assistant must not claim we are open
            // or that someone will reply right away.
            function check(reply, ctx) {
              const today = HOURS[ctx.weekday];
              const now = dates.minutes(ctx.time);
              const open = !!today && now >= dates.minutes(today[0]) && now < dates.minutes(today[1]);
              if (open) return { ok: true };
              if (/\b(we('| a)re open( now)?|open right now|someone will (reply|call you) (right away|now|shortly))\b/i.test(reply)) {
                return {
                  ok: false,
                  reason: 'The reply says we are open, but it is ' + ctx.weekday + ' ' + ctx.time + ' and we are closed.',
                  fix: 'Say we are closed right now and give the next opening time.',
                };
              }
              return { ok: true };
            }
            """;
        var (openDate, openTime, _) = HoursFixtures(hours);
        var tests = new object[]
        {
            new { name = "Fine during opening hours", input = "Yes, we're open now!", now = $"{openDate}T{openTime}:00", expect = true },
            new { name = "Wrongly claims open at night", input = "Yes, we're open now!", now = $"{openDate}T23:30:00", expect = false },
            new { name = "Honest after hours", input = "We're closed at the moment and open again tomorrow morning.", now = $"{openDate}T23:30:00", expect = true },
        };
        return new DraftSkill("Closed-hours honesty", SkillKinds.Check, "every reply",
            "Blocks replies that claim we are open outside opening hours.",
            Json("{}"), code, JsonSerializer.SerializeToElement(tests), "", "retry");
    }

    // ---- time ----------------------------------------------------------------------------

    private static DraftSkill TimeTool() => new("Local time", SkillKinds.Tool,
        "the customer asks what time or day it is for us",
        "The current local time, date and weekday at the business.",
        Json("{\"type\":\"object\",\"properties\":{}}"),
        """
        // The business's local time — ctx is already in the business timezone.
        function run(args, ctx) {
          return { time: ctx.time, date: ctx.today, weekday: ctx.weekday, timezone: ctx.timezone };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Thursday afternoon", args = new { }, now = "2026-10-08T14:30:00", expect = new { time = "14:30", date = "2026-10-08", weekday = "Thursday" } },
            new { name = "Just after midnight", args = new { }, now = "2026-10-11T00:05:00", expect = new { weekday = "Sunday" } },
        }), "", "retry");

    private static DraftSkill TimeCheck() => new("Correct weekday", SkillKinds.Check, "every reply",
        "Blocks replies that state the wrong day of the week for today.",
        Json("{}"),
        """
        // If the reply says "today is <weekday>", it must be the real weekday.
        function check(reply, ctx) {
          const m = /\btoday is (?:a )?(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b/i.exec(reply);
          if (m && m[1].toLowerCase() !== ctx.weekday.toLowerCase()) {
            return {
              ok: false,
              reason: 'The reply says today is ' + m[1] + ', but today is ' + ctx.weekday + ' (' + ctx.today + ').',
              fix: 'Today is ' + ctx.weekday + ', ' + ctx.today + '.',
            };
          }
          return { ok: true };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Right weekday", input = "Today is Thursday, so we can deliver tomorrow.", now = "2026-10-08T10:00:00", expect = true },
            new { name = "Wrong weekday", input = "Today is Monday, so we can deliver tomorrow.", now = "2026-10-08T10:00:00", expect = false },
            new { name = "No weekday mentioned", input = "Thanks, we'll be in touch.", now = "2026-10-08T10:00:00", expect = true },
        }), "", "retry");

    // ---- week ----------------------------------------------------------------------------

    private static DraftSkill WeekTool() => new("Week info", SkillKinds.Tool,
        "the customer asks about this week, a week number, or which dates a week covers",
        "ISO week number and the Monday–Sunday dates of the current (or a given) week.",
        Json("{\"type\":\"object\",\"properties\":{\"date\":{\"type\":\"string\",\"description\":\"YYYY-MM-DD; defaults to today\"}}}"),
        """
        // ISO week facts for today (or args.date).
        function run(args, ctx) {
          const day = args && args.date ? args.date : ctx.today;
          const w = dates.isoWeek(day);
          return {
            date: day,
            week: w.week,
            year: w.year,
            week_start: dates.weekStart(day),
            week_end: dates.weekEnd(day),
            weekday: dates.weekday(day),
          };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "This week", args = new { }, now = "2026-10-08T09:00:00", expect = new { week = 41, week_start = "2026-10-05", week_end = "2026-10-11" } },
            new { name = "Given date", args = new { date = "2027-01-01" }, expect = new { week = 53, year = 2026, weekday = "Friday" } },
        }), "", "retry");

    private static DraftSkill WeekCheck() => new("Correct week number", SkillKinds.Check, "every reply",
        "Blocks replies that mention the wrong ISO week number for this week.",
        Json("{}"),
        """
        // "this week (week 41)" must be the real ISO week number.
        function check(reply, ctx) {
          const m = /\b(?:this|current) week\b[^.\n]*?\bweek\s*(?:no\.?|number|#)?\s*(\d{1,2})\b|\bweek\s*(?:no\.?|number|#)?\s*(\d{1,2})\b[^.\n]*?\b(?:this|current) week\b/i.exec(reply);
          if (!m) return { ok: true };
          const said = Number(m[1] || m[2]);
          const real = dates.isoWeek(ctx.today).week;
          return said === real
            ? { ok: true }
            : { ok: false, reason: 'The reply says this is week ' + said + ', but it is week ' + real + '.', fix: 'This is week ' + real + '.' };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Right week", input = "This week (week 41) we have a sale.", now = "2026-10-08T09:00:00", expect = true },
            new { name = "Wrong week", input = "This week (week 40) we have a sale.", now = "2026-10-08T09:00:00", expect = false },
        }), "", "retry");

    // ---- dates ---------------------------------------------------------------------------

    private static DraftSkill DateTool() => new("Business-day date", SkillKinds.Tool,
        "the customer asks when something will arrive, be ready or be due a number of (business) days from now",
        "The date N business days (Mon–Fri) or calendar days from today.",
        Json("{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"integer\",\"description\":\"how many days ahead\"},\"business_days\":{\"type\":\"boolean\",\"description\":\"skip Saturdays and Sundays (default true)\"}},\"required\":[\"days\"]}"),
        """
        // The date N days from today; business days skip weekends by default.
        function run(args, ctx) {
          const n = Number(args && args.days) || 0;
          const business = !(args && args.business_days === false);
          const date = business ? dates.addBusinessDays(ctx.today, n) : dates.addDays(ctx.today, n);
          return { from: ctx.today, days: n, business_days: business, date: date, weekday: dates.weekday(date) };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "3 business days from Thursday", args = new { days = 3 }, now = "2026-10-08T10:00:00", expect = new { date = "2026-10-13", weekday = "Tuesday" } },
            new { name = "3 calendar days", args = new { days = 3, business_days = false }, now = "2026-10-08T10:00:00", expect = new { date = "2026-10-11" } },
        }), "", "retry");

    private static DraftSkill DateCheck() => new("No past dates", SkillKinds.Check, "every reply",
        "Blocks replies that promise a date that has already passed.",
        Json("{}"),
        """
        // Any YYYY-MM-DD or DD/MM/YYYY date in a reply must be today or later.
        function check(reply, ctx) {
          const found = [];
          reply.replace(/\b(\d{4})-(\d{2})-(\d{2})\b/g, function (_, y, m, d) { found.push(y + '-' + m + '-' + d); return _; });
          reply.replace(/\b(\d{1,2})\/(\d{1,2})\/(\d{4})\b/g, function (_, d, m, y) {
            found.push(y + '-' + (m.length < 2 ? '0' + m : m) + '-' + (d.length < 2 ? '0' + d : d));
            return _;
          });
          const past = found.filter(function (d) { return d < ctx.today; });
          return past.length === 0
            ? { ok: true }
            : { ok: false, reason: 'The reply mentions ' + past.join(', ') + ', which is in the past (today is ' + ctx.today + ').', fix: 'Only offer dates from ' + ctx.today + ' onward.' };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Future date", input = "We can deliver on 2026-10-12.", now = "2026-10-08T10:00:00", expect = true },
            new { name = "Past date", input = "We can deliver on 2026-10-01.", now = "2026-10-08T10:00:00", expect = false },
            new { name = "Past date, DD/MM/YYYY", input = "Your slot is 3/10/2026.", now = "2026-10-08T10:00:00", expect = false },
        }), "", "retry");

    // ---- phone ---------------------------------------------------------------------------

    private static DraftSkill PhoneTool() => new("Phone number check", SkillKinds.Tool,
        "the customer gives a phone number that must be confirmed or stored",
        "Validates a phone number and returns it in international (E.164) form.",
        Json("{\"type\":\"object\",\"properties\":{\"phone\":{\"type\":\"string\",\"description\":\"the number as the customer typed it\"}},\"required\":[\"phone\"]}"),
        """
        // Country code used when a number has none (60 = Malaysia).
        const DEFAULT_COUNTRY = '60';

        function run(args, ctx) {
          const raw = String((args && args.phone) || '');
          let digits = raw.replace(/\D/g, '');
          if (raw.trim().indexOf('+') !== 0) {
            if (digits.indexOf('00') === 0) digits = digits.slice(2);
            else if (digits.indexOf('0') === 0) digits = DEFAULT_COUNTRY + digits.slice(1);
            else if (digits.indexOf(DEFAULT_COUNTRY) !== 0) digits = DEFAULT_COUNTRY + digits;
          }
          const valid = digits.length >= 9 && digits.length <= 15;
          return { valid: valid, e164: valid ? '+' + digits : null, digits: digits };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Local mobile number", args = new { phone = "012-345 6789" }, expect = new { valid = true, e164 = "+60123456789" } },
            new { name = "Already international", args = new { phone = "+65 9123 4567" }, expect = new { valid = true, e164 = "+6591234567" } },
            new { name = "Too short", args = new { phone = "12345" }, expect = new { valid = false } },
        }), "", "retry");

    private static DraftSkill PhoneCheck() => new("No phone numbers in replies", SkillKinds.Check, "every reply",
        "Blocks replies that contain a phone number, except the ones listed as allowed.",
        Json("{}"),
        """
        // Numbers the assistant MAY share (digits only), e.g. your support line.
        const ALLOWED = ['60312345678'];

        function check(reply, ctx) {
          const matches = reply.match(/\+?\d[\d\s().-]{7,}\d/g) || [];
          for (const m of matches) {
            const digits = m.replace(/\D/g, '');
            if (digits.length < 9) continue;
            if (ALLOWED.indexOf(digits) === -1) {
              return { ok: false, reason: 'The reply contains the phone number ' + m.trim() + '.', fix: 'Remove the phone number; offer to continue the conversation here instead.' };
            }
          }
          return { ok: true };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "No number", input = "Sure, we can help with that here.", expect = true },
            new { name = "Leaks a number", input = "Call our manager on 012-345 6789.", expect = false },
            new { name = "Allowed support line", input = "Our hotline is +60 3-1234 5678.", expect = true },
        }), "", "retry");

    // ---- email ---------------------------------------------------------------------------

    private static DraftSkill EmailTool() => new("Email address check", SkillKinds.Tool,
        "the customer gives an email address that must be confirmed or stored",
        "Validates an email address and returns it normalised.",
        Json("{\"type\":\"object\",\"properties\":{\"email\":{\"type\":\"string\"}},\"required\":[\"email\"]}"),
        """
        // Checks the shape of an email address (it cannot check the mailbox exists).
        function run(args, ctx) {
          const email = String((args && args.email) || '').trim().toLowerCase();
          const valid = /^[^\s@]+@[^\s@]+\.[a-z]{2,}$/.test(email);
          return { valid: valid, email: valid ? email : null, domain: valid ? email.split('@')[1] : null };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Valid address", args = new { email = " Jane.Doe@Example.COM " }, expect = new { valid = true, email = "jane.doe@example.com", domain = "example.com" } },
            new { name = "Missing domain", args = new { email = "jane@" }, expect = new { valid = false } },
        }), "", "retry");

    private static DraftSkill EmailCheck() => new("No outside email addresses", SkillKinds.Check, "every reply",
        "Blocks replies that contain an email address outside the allowed domains.",
        Json("{}"),
        """
        // Domains the assistant MAY mention, e.g. your own.
        const ALLOWED_DOMAINS = ['example.com'];

        function check(reply, ctx) {
          const found = reply.match(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g) || [];
          for (const e of found) {
            const domain = e.split('@')[1].toLowerCase();
            if (ALLOWED_DOMAINS.indexOf(domain) === -1) {
              return { ok: false, reason: 'The reply contains the email address ' + e + '.', fix: 'Only share addresses @' + ALLOWED_DOMAINS.join(', @') + '.' };
            }
          }
          return { ok: true };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Own domain is fine", input = "Email us at help@example.com.", expect = true },
            new { name = "Outside address", input = "Write to john.smith@gmail.com directly.", expect = false },
        }), "", "retry");

    // ---- Malaysian IC (NRIC / MyKad) -----------------------------------------------------

    private static DraftSkill IcTool() => new("IC number check", SkillKinds.Tool,
        "the customer gives an IC (MyKad / NRIC) number",
        "Validates a Malaysian IC number and reads the birth date and gender from it.",
        Json("{\"type\":\"object\",\"properties\":{\"ic\":{\"type\":\"string\",\"description\":\"e.g. 900101-14-5678\"}},\"required\":[\"ic\"]}"),
        """
        // Malaysian IC: YYMMDD-PB-###G. Birth date from the first six digits,
        // place-of-birth code next, and an odd last digit = male.
        function run(args, ctx) {
          const digits = String((args && args.ic) || '').replace(/\D/g, '');
          if (digits.length !== 12) return { valid: false, reason: 'An IC number has 12 digits.' };
          const yy = Number(digits.slice(0, 2)), mm = Number(digits.slice(2, 4)), dd = Number(digits.slice(4, 6));
          const nowYY = Number(ctx.today.slice(2, 4));
          const year = (yy > nowYY ? 1900 : 2000) + yy;
          const birth = year + '-' + digits.slice(2, 4) + '-' + digits.slice(4, 6);
          const real = mm >= 1 && mm <= 12 && dd >= 1 && dd <= 31 && dates.format(dates.parse(birth)) === birth;
          if (!real) return { valid: false, reason: 'The first six digits are not a real date.' };
          return {
            valid: true,
            birth_date: birth,
            place_code: digits.slice(6, 8),
            gender: Number(digits[11]) % 2 === 1 ? 'male' : 'female',
            masked: '******-**-' + digits.slice(8),
          };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Valid IC", args = new { ic = "900101-14-5677" }, now = "2026-10-08T10:00:00", expect = new { valid = true, birth_date = "1990-01-01", gender = "male", masked = "******-**-5677" } },
            new { name = "Impossible date", args = new { ic = "050230-10-1234" }, now = "2026-10-08T10:00:00", expect = new { valid = false } },
            new { name = "Wrong length", args = new { ic = "12345" }, expect = new { valid = false } },
        }), "", "retry");

    private static DraftSkill IcCheck() => new("Never repeat IC numbers", SkillKinds.Check, "every reply",
        "Blocks replies that contain a full IC (MyKad) number — show at most the last four digits.",
        Json("{}"),
        """
        // A full IC number is personal data: the assistant may show the last four digits only.
        function check(reply, ctx) {
          if (/\b\d{6}-?\d{2}-?\d{4}\b/.test(reply)) {
            return { ok: false, reason: 'The reply contains a full IC number.', fix: 'Mask it like ******-**-1234.' };
          }
          return { ok: true };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Masked is fine", input = "We have your IC ending in 5677 on file.", expect = true },
            new { name = "Full IC is blocked", input = "Your IC 900101-14-5677 is verified.", expect = false },
            new { name = "Without dashes too", input = "IC 900101145677 confirmed.", expect = false },
        }), "", "handover");

    // ---- skeletons -----------------------------------------------------------------------

    private static string Comment(string description, IReadOnlyList<Answer> answers)
    {
        var lines = new List<string> { "// What it should do: " + OneLine(description) };
        foreach (var a in answers) lines.Add("// " + OneLine(a.Question) + " → " + OneLine(a.Text));
        return string.Join("\n", lines);
    }

    private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static DraftSkill SkeletonTool(string description, IReadOnlyList<Answer> answers) => new(
        NameFrom(description, "New tool"), SkillKinds.Tool, "", description,
        Json("{\"type\":\"object\",\"properties\":{\"input\":{\"type\":\"string\"}}}"),
        Comment(description, answers) + """

        // TODO: replace the placeholder below with the real rule.
        function run(args, ctx) {
          const input = String((args && args.input) || '');
          return { input: input, result: null, note: 'not implemented yet' };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Runs without errors", args = new { input = "example" } },
        }), "", "retry");

    private static DraftSkill SkeletonCheck(string description, IReadOnlyList<Answer> answers) => new(
        NameFrom(description, "New reply check"), SkillKinds.Check, "every reply", description, Json("{}"),
        Comment(description, answers) + """

        // Words or phrases a reply must never contain — edit this list.
        const FORBIDDEN = [];

        function check(reply, ctx) {
          const text = reply.toLowerCase();
          for (const phrase of FORBIDDEN) {
            if (text.indexOf(phrase.toLowerCase()) !== -1) {
              return { ok: false, reason: 'The reply contains "' + phrase + '".' };
            }
          }
          return { ok: true };
        }
        """,
        JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "A plain reply passes", input = "Thanks for reaching out!", expect = true },
        }), "", "retry");
}
