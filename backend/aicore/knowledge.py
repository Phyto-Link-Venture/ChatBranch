"""COMPANY KNOWLEDGE — the operator-authored knowledge base, assembled into a
bounded prompt block.

This module is the read half of Settings → Knowledge Base. It is deliberately
built to the same three rules as aicore/memory.py, because it is injected at
the same two seams (the chat system prompt and the voice dispatch metadata)
and therefore reaches all six channels plus phone calls with no per-channel
code:

1. **No embeddings, on purpose.** Keyword + recency + priority scoring is
   deterministic, debuggable, testable and needs no vector store in a SQLite
   deployment. `memory.py` says the same thing; keep it that way.
2. **Bounded output.** A knowledge block that grows without limit silently
   pushes the persona out of the context window. `BUDGET` is a hard character
   cap, truncation is PER ITEM and labelled ("…[truncated]"), never a
   mid-sentence cut of the whole block.
3. **Exception-safe.** `build_knowledge_context` catches everything and
   returns "". If retrieval breaks, the AI answers without knowledge — it
   never breaks a send, a webhook or a dial.

Assembly order (earlier tiers win the budget, because urgency beats
completeness):

  1  notice  — all active + in-window. Few by nature, always relevant.
  2  event   — all active + in-window.
  3  fact    — all active. Short by definition (hours, address, number).
  4  faq/policy — TOP-N keyword match against the caller's text, scored on
                  title/question/tags/content overlap, then priority, then
                  recency. No keyword hits contributes NOTHING rather than
                  padding the prompt with arbitrary rows.
"""
from __future__ import annotations

import logging
import re

logger = logging.getLogger(__name__)

BUDGET = 1800          # chars — hard cap on the whole assembled block
ITEM_MAX = 400         # chars — per-item cap before the labelled truncation
MATCH_LIMIT = 4        # tier-4 rows admitted per build
MIN_TOKEN_LEN = 3      # ignore "is", "my", "to" … when scoring

HEADING = "COMPANY KNOWLEDGE"

TIER_ORDER = ("notice", "event", "fact")
MATCHED_KINDS = ("faq", "policy")

_LABELS = {
    "notice": "NOTICE",
    "event": "NOW ON",
    "fact": "FACT",
    "faq": "FAQ",
    "policy": "POLICY",
}

# Words that carry no discriminating signal in a support conversation. Kept
# short and explicit — this is a stop-list, not a language model.
_STOPWORDS = {
    "the", "and", "for", "you", "your", "our", "can", "with", "that", "this",
    "how", "what", "when", "where", "why", "does", "did", "are", "was", "were",
    "have", "has", "had", "will", "would", "there", "here", "from", "about",
    "please", "hello", "hi", "thanks", "thank", "just", "want", "need", "not",
}

_TOKEN_RE = re.compile(r"[a-z0-9]+")


def tokenize(text: str) -> set[str]:
    return {t for t in _TOKEN_RE.findall(str(text or "").lower())
            if len(t) >= MIN_TOKEN_LEN and t not in _STOPWORDS}


# ---- filtering ----------------------------------------------------------------

def _entity_ids(contact) -> set[int]:
    if contact is None:
        return set()
    try:
        return set(contact.entity_links.values_list("entity_id", flat=True))
    except Exception:
        return set()


def live_items(kinds=None, channel: str = "", contact=None, now=None):
    """Every KnowledgeItem that is active, inside its date window, in scope for
    the channel, and in scope for the contact's entity. Returns a list, newest
    high-priority first (the model's own ordering)."""
    from django.db.models import Q
    from django.utils import timezone as dj_tz

    from crm.models import KnowledgeItem

    now = now or dj_tz.now()
    qs = KnowledgeItem.objects.filter(active=True)
    if kinds:
        qs = qs.filter(kind__in=list(kinds))
    qs = qs.filter(Q(starts_at__isnull=True) | Q(starts_at__lte=now))
    qs = qs.filter(Q(ends_at__isnull=True) | Q(ends_at__gt=now))
    entities = _entity_ids(contact)
    if entities:
        qs = qs.filter(Q(entity__isnull=True) | Q(entity_id__in=entities))
    else:
        # A contact with no entity link (or no contact at all) only sees
        # entity-agnostic knowledge — a entity's private notice must not leak.
        qs = qs.filter(entity__isnull=True)
    rows = list(qs)
    if channel:
        rows = [r for r in rows
                if not r.channels or channel in [str(c) for c in r.channels]]
    return rows


# ---- scoring ------------------------------------------------------------------

def score(item, tokens: set[str]) -> int:
    """Keyword overlap, weighted by where the word was found. Deterministic and
    cheap: title/question hits are worth the most, tags next, body least."""
    if not tokens:
        return 0
    total = 0
    total += 5 * len(tokens & tokenize(item.title))
    total += 5 * len(tokens & tokenize(item.question))
    total += 3 * len(tokens & tokenize(" ".join(str(t) for t in (item.tags or []))))
    total += 1 * len(tokens & tokenize(item.content))
    return total


def _render(item) -> str:
    label = _LABELS.get(item.kind, item.kind.upper())
    head = item.question.strip() if (item.kind == "faq" and item.question.strip()) \
        else item.title.strip()
    body = " ".join(item.content.split())
    line = f"- [{label}] {head}: {body}" if body else f"- [{label}] {head}"
    if len(line) > ITEM_MAX:
        line = line[:ITEM_MAX - 13].rstrip() + "…[truncated]"
    return line


# ---- the block ----------------------------------------------------------------

def build_knowledge_context(contact=None, channel: str = "", text: str = "",
                            budget: int = BUDGET) -> str:
    """The `COMPANY KNOWLEDGE` block, or "". Never raises."""
    try:
        return _build(contact, channel, text, budget)
    except Exception:
        logger.exception("aicore.knowledge: build_knowledge_context failed")
        return ""


def _build(contact, channel: str, text: str, budget: int) -> str:
    rows = live_items(channel=channel, contact=contact)
    if not rows:
        return ""
    by_kind: dict[str, list] = {}
    for row in rows:
        by_kind.setdefault(row.kind, []).append(row)

    ordered: list = []
    for kind in TIER_ORDER:
        ordered.extend(sorted(by_kind.get(kind, []),
                              key=lambda r: (-r.priority, -r.updated_at.timestamp())))

    tokens = tokenize(text)
    matched = []
    for kind in MATCHED_KINDS:
        for row in by_kind.get(kind, []):
            s = score(row, tokens)
            if s > 0:
                matched.append((s, row))
    matched.sort(key=lambda pair: (-pair[0], -pair[1].priority,
                                   -pair[1].updated_at.timestamp()))
    ordered.extend(row for _s, row in matched[:MATCH_LIMIT])

    lines: list[str] = []
    used = 0
    for row in ordered:
        line = _render(row)
        if used + len(line) + 1 > budget:
            continue  # a later, shorter item may still fit — urgency already won
        lines.append(line)
        used += len(line) + 1
    if not lines:
        return ""
    return "\n".join(lines)[:budget]


def prompt_block(contact=None, channel: str = "", text: str = "",
                 budget: int = BUDGET) -> str:
    """The block WITH its heading + usage instruction, ready to drop into a
    system prompt. "" when there is nothing to say."""
    body = build_knowledge_context(contact, channel, text, budget)
    if not body:
        return ""
    return (
        f"{HEADING} — facts, policies and current notices the business has "
        "published for you. Treat them as authoritative and answer from them "
        "directly. Anything NOT listed here you do not know: say the team will "
        "confirm it rather than guessing.\n" + body
    )


# ---- skills (crm.AgentSkill) ------------------------------------------------------

SKILLS_LIMIT = 24
SKILLS_BUDGET = 6000


def skills_block() -> str:
    """The operator's editable playbook as a `SKILLS` prompt section —
    `use_when` is the selector, `instructions` the body. Owned by
    Settings → AI Skills (`GET/PUT /api/aicore/skills`), which since Phase 8
    is the SOLE writer of crm.AgentSkill; the flow-builder library sync only
    reads them. Never raises: a non-Django/early-boot caller just gets ""."""
    try:
        from crm.models import AgentSkill
        rows = list(AgentSkill.objects.all()[:SKILLS_LIMIT])
    except Exception:
        return ""
    if not rows:
        return ""
    parts = ["SKILLS — the operator's playbook. Apply whichever one fits the "
             "situation; ignore the rest."]
    for s in rows:
        line = f"• {s.name}"
        if s.use_when:
            line += f" [when: {s.use_when.strip()}]"
        if s.instructions:
            line += f": {s.instructions.strip()}"
        parts.append(line[:700])
    return "\n".join(parts)[:SKILLS_BUDGET]
