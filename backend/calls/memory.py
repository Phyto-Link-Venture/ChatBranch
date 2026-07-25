# backend/calls/memory.py
"""Per-contact MEMORY.md -- carries forward what the CRM can't derive:
language preference (refines crm.Contact.language if a call reveals
otherwise), best contact time, and short factual notes from prior contact.
Structured facts already live on crm.Contact / crm.Record, so this file never
duplicates them.

File-backed like voices.py/pronunciation.py (data/memory/{contact_id}.md,
gitignored). Plain hand-rolled frontmatter, not real YAML -- no new dependency
for a handful of fixed fields.
"""
import os
from pathlib import Path

_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
MEMORY_DIR = _DATA_DIR / "memory"

MAX_NOTES = 20


def _path(contact_id: int) -> Path:
    return MEMORY_DIR / f"{contact_id}.md"


def render(memory: dict) -> str:
    lines = ["---"]
    if memory.get("language"):
        lines.append(f"language: {memory['language']}")
    if memory.get("best_contact"):
        lines.append(f"best_contact: {memory['best_contact']}")
    if memory.get("preferred_voice"):
        lines.append(f"preferred_voice: {memory['preferred_voice']}")
    if memory.get("preferred_voice_provider"):
        lines.append(f"preferred_voice_provider: {memory['preferred_voice_provider']}")
    if memory.get("updated"):
        lines.append(f"updated: {memory['updated']}")
    lines.append("---")
    notes = memory.get("notes") or []
    if notes:
        lines.append("")
        lines.append("## Notes")
        lines.extend(f"- {n}" for n in notes)
    return "\n".join(lines) + "\n"


def _blank() -> dict:
    return {"language": None, "best_contact": None, "preferred_voice": None,
            "preferred_voice_provider": None, "updated": None, "notes": []}


def parse(text: str) -> dict:
    memory = _blank()
    lines = text.splitlines()
    in_frontmatter = False
    in_notes = False
    for line in lines:
        stripped = line.strip()
        if stripped == "---":
            in_frontmatter = not in_frontmatter
            continue
        if in_frontmatter:
            if ":" in stripped:
                key, _, value = stripped.partition(":")
                key = key.strip()
                if key in memory:
                    memory[key] = value.strip() or None
            continue
        if stripped == "## Notes":
            in_notes = True
            continue
        if in_notes and stripped.startswith("- "):
            memory["notes"].append(stripped[2:].strip())
    return memory


def load_memory(contact_id: int) -> dict:
    try:
        with open(_path(contact_id), "r", encoding="utf-8") as f:
            return parse(f.read())
    except FileNotFoundError:
        return _blank()


def save_memory(contact_id: int, *, language: str | None = None, best_contact: str | None = None,
                 new_notes: list[str] | None = None, updated: str | None = None,
                 preferred_voice: str | None = None, preferred_voice_provider: str | None = None) -> dict:
    """Merge-writes: scalar fields overwrite only when given a value;
    new_notes are appended (deduped, capped to the most recent MAX_NOTES)."""
    memory = load_memory(contact_id)
    if language:
        memory["language"] = language
    if best_contact:
        memory["best_contact"] = best_contact
    if preferred_voice:
        memory["preferred_voice"] = preferred_voice
    if preferred_voice_provider:
        memory["preferred_voice_provider"] = preferred_voice_provider
    if updated:
        memory["updated"] = updated
    for note in new_notes or []:
        note = note.strip()
        if note and note not in memory["notes"]:
            memory["notes"].append(note)
    memory["notes"] = memory["notes"][-MAX_NOTES:]

    MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    path = _path(contact_id)
    tmp = str(path) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(render(memory))
    os.replace(tmp, path)
    return memory
