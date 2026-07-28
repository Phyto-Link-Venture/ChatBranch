"""LLM usage ledger — records tokens for EVERY LLM call so admins can see and
bill AI consumption (crm.LlmUsage; surfaced at GET /api/analytics/usage and
the Analytics page's "AI usage" card).

Design rules (same contract as memory/knowledge):

* **Exception-safe.** `record()` swallows everything — a ledger failure must
  never break a reply, summary, webhook or call path.
* **Raw tokens in, estimates out.** Rows store token counts only; money is
  computed at READ time from the price table, so price edits in Settings apply
  retroactively and the ledger never holds a stale currency figure.
* **Text and audio split.** Realtime voice models bill audio tokens at a
  different (much higher) rate than text tokens — they get their own columns.

Price table: `PRICES_DEFAULT` below (USD per 1M tokens, (input, output)),
overridable per model via Setting `llm_prices` (a JSON object of
{"model": [in, out]} / {"model:audio": [in, out]}). Estimates only — verify
against the provider's own billing before invoicing anyone.
"""
from __future__ import annotations

import json
import logging

logger = logging.getLogger(__name__)

# USD per 1M tokens: model -> (input, output). ":audio" rows price the audio
# tokens of realtime speech models. Keep this a floor of common defaults —
# operators maintain the live table in Setting `llm_prices`.
PRICES_DEFAULT: dict[str, tuple[float, float]] = {
    "gpt-4o-mini": (0.15, 0.60),
    "openai/gpt-4o-mini": (0.15, 0.60),
    "gpt-4o": (2.50, 10.00),
    "gpt-4.1-mini": (0.40, 1.60),
    "gpt-realtime": (4.00, 16.00),
    "gpt-realtime:audio": (32.00, 64.00),
    "gemini-2.5-flash": (0.30, 2.50),
}


def provider_from_base_url(base_url: str) -> str:
    base = (base_url or "").lower()
    if "openrouter" in base:
        return "openrouter"
    if "ilmu" in base:
        return "ilmu"
    if "googleapis" in base or "generativelanguage" in base:
        return "gemini"
    return "openai"


def record(feature: str, *, provider: str = "", model: str = "", usage=None,
           prompt_tokens: int = 0, completion_tokens: int = 0,
           audio_prompt_tokens: int = 0, audio_completion_tokens: int = 0,
           contact=None, contact_id=None, channel: str = "", ref: str = "",
           detail: dict | None = None) -> None:
    """Write one ledger row. `usage` may be an OpenAI `resp.usage` object
    (prompt_tokens/completion_tokens or input_tokens/output_tokens) and takes
    precedence over the explicit token kwargs. Never raises."""
    try:
        if usage is not None:
            prompt_tokens = int(getattr(usage, "prompt_tokens", None)
                                or getattr(usage, "input_tokens", None) or 0)
            completion_tokens = int(getattr(usage, "completion_tokens", None)
                                    or getattr(usage, "output_tokens", None) or 0)
        total = (prompt_tokens + completion_tokens
                 + audio_prompt_tokens + audio_completion_tokens)
        if total <= 0:
            return  # nothing to bill (sandbox fake, provider without usage)
        from crm.models import Contact, LlmUsage

        if contact is None and contact_id:
            contact = Contact.objects.filter(pk=contact_id).first()
        LlmUsage.objects.create(
            feature=feature, provider=provider, model=model or "",
            channel=channel or "", contact=contact, ref=str(ref or "")[:190],
            prompt_tokens=prompt_tokens, completion_tokens=completion_tokens,
            audio_prompt_tokens=audio_prompt_tokens,
            audio_completion_tokens=audio_completion_tokens,
            detail=detail or {})
    except Exception:
        logger.exception("llm-usage: record failed (feature=%s)", feature)


def sum_usages(usages) -> tuple[int, int]:
    """(prompt, completion) totals over OpenAI usage objects, None-tolerant."""
    pt = ct = 0
    for u in usages or []:
        if u is None:
            continue
        pt += int(getattr(u, "prompt_tokens", None)
                  or getattr(u, "input_tokens", None) or 0)
        ct += int(getattr(u, "completion_tokens", None)
                  or getattr(u, "output_tokens", None) or 0)
    return pt, ct


def price_table() -> dict[str, tuple[float, float]]:
    """Effective per-model prices: Setting `llm_prices` (JSON) merged over the
    shipped defaults. Malformed entries are ignored."""
    merged = dict(PRICES_DEFAULT)
    try:
        from crm.models import Setting

        row = Setting.objects.filter(key="llm_prices").first()
        if row and row.value.strip():
            for name, pair in (json.loads(row.value) or {}).items():
                try:
                    merged[str(name)] = (float(pair[0]), float(pair[1]))
                except (TypeError, ValueError, IndexError):
                    continue
    except Exception:
        logger.exception("llm-usage: price table read failed — using defaults")
    return merged


def estimate_cost(model: str, prompt_tokens: int, completion_tokens: int,
                  audio_prompt_tokens: int = 0, audio_completion_tokens: int = 0,
                  prices: dict | None = None) -> float:
    """Estimated USD for one row/aggregate. Unknown models cost 0 (shown as
    'unpriced' in the report rather than silently guessed)."""
    prices = prices or price_table()
    cost = 0.0
    pair = prices.get(model)
    if pair:
        cost += (prompt_tokens * pair[0] + completion_tokens * pair[1]) / 1_000_000
    audio_pair = prices.get(f"{model}:audio")
    if audio_pair:
        cost += (audio_prompt_tokens * audio_pair[0]
                 + audio_completion_tokens * audio_pair[1]) / 1_000_000
    return round(cost, 6)
