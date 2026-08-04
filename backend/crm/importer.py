"""Bulk contact import from Excel (.xlsx) / CSV.

Parses an uploaded spreadsheet, auto-detects which column maps to which Contact
field (overridable), and upserts `crm.Contact` rows using the SAME match order
as the sync normalizer: (tenant, external_id) → (tenant, phone). A row without a
name or phone is skipped with a reason (Contact.phone is effectively required —
it is unique per tenant).

`run_import(..., dry_run=True)` computes the same plan without writing, so the UI
can preview created/updated/skipped counts before committing.
"""
from __future__ import annotations

import csv
import io
import logging

logger = logging.getLogger(__name__)

# field -> accepted header names (lower-cased, EN + a little BM)
FIELD_ALIASES: dict[str, list[str]] = {
    "name": ["name", "full name", "contact name", "student name", "customer name", "nama"],
    "phone": ["phone", "mobile", "phone number", "mobile number", "contact number",
              "whatsapp", "hp", "no telefon", "telefon"],
    "email": ["email", "e-mail", "email address", "emel"],
    "company_name": ["company", "company name", "organization", "organisation", "syarikat"],
    "language": ["language", "lang", "bahasa"],
    "external_id": ["external id", "id", "member id", "student id", "customer id",
                    "ref", "reference"],
    "notes": ["notes", "note", "remarks", "catatan"],
    "tags": ["tags", "tag", "labels"],
}
FIELDS = list(FIELD_ALIASES)


def _norm(h) -> str:
    return str(h or "").strip().lower()


def detect_mapping(headers: list[str]) -> dict:
    """{field: header} auto-detected from the sheet's header names."""
    lower = {_norm(h): h for h in headers if str(h or "").strip()}
    mapping: dict[str, str] = {}
    for field, aliases in FIELD_ALIASES.items():
        for alias in aliases:
            if alias in lower:
                mapping[field] = lower[alias]
                break
    return mapping


def parse(file_bytes: bytes, filename: str) -> tuple[list[str], list[dict]]:
    """(headers, rows) — rows are dicts keyed by header. Raises ValueError on an
    unsupported extension."""
    name = (filename or "").lower()
    if name.endswith(".csv"):
        return _parse_csv(file_bytes)
    if name.endswith(".xlsx"):
        return _parse_xlsx(file_bytes)
    raise ValueError("Unsupported file type — upload a .xlsx or .csv file.")


def _rows_from(headers: list[str], raw_rows) -> list[dict]:
    out: list[dict] = []
    for r in raw_rows:
        cells = ["" if c is None else str(c).strip() for c in r]
        if not any(cells):
            continue
        out.append({headers[i]: (cells[i] if i < len(cells) else "")
                    for i in range(len(headers)) if headers[i]})
    return out


def _parse_csv(file_bytes: bytes) -> tuple[list[str], list[dict]]:
    text = file_bytes.decode("utf-8-sig", errors="replace")
    reader = list(csv.reader(io.StringIO(text)))
    if not reader:
        return [], []
    headers = [str(h).strip() for h in reader[0]]
    return headers, _rows_from(headers, reader[1:])


def _parse_xlsx(file_bytes: bytes) -> tuple[list[str], list[dict]]:
    from openpyxl import load_workbook

    wb = load_workbook(io.BytesIO(file_bytes), read_only=True, data_only=True)
    try:
        ws = wb.active
        rows_iter = ws.iter_rows(values_only=True)
        try:
            header_row = next(rows_iter)
        except StopIteration:
            return [], []
        headers = [str(h).strip() if h is not None else "" for h in header_row]
        return headers, _rows_from(headers, rows_iter)
    finally:
        wb.close()


def _split_tags(raw: str) -> list[str]:
    return [t.strip() for t in raw.replace(";", ",").split(",") if t.strip()]


def run_import(rows: list[dict], mapping: dict, *, tenant, dry_run: bool = False) -> dict:
    """Upsert contacts from parsed rows. Returns
    {total, created, updated, skipped, errors:[{row, reason}]}."""
    from crm.models import Contact

    created = updated = skipped = 0
    errors: list[dict] = []

    def val(row: dict, field: str) -> str:
        header = mapping.get(field)
        return str(row.get(header, "") or "").strip() if header else ""

    for idx, row in enumerate(rows, start=2):  # +2: row 1 is the header
        name = val(row, "name")
        phone = val(row, "phone")
        if not name or not phone:
            skipped += 1
            errors.append({"row": idx, "reason": "missing name or phone"})
            continue
        ext = val(row, "external_id")
        existing = None
        if ext:
            existing = Contact.objects.filter(tenant=tenant, external_id=ext).first()
        if existing is None:
            existing = Contact.objects.filter(tenant=tenant, phone=phone).first()

        if dry_run:
            updated += 1 if existing else 0
            created += 0 if existing else 1
            continue

        email = val(row, "email")
        company = val(row, "company_name")
        language = val(row, "language") or "en"
        notes = val(row, "notes")
        tags = _split_tags(val(row, "tags"))
        try:
            if existing:
                existing.name = name
                if email:
                    existing.email = email
                if company:
                    existing.company_name = company
                if language:
                    existing.language = language
                if ext:
                    existing.external_id = ext
                if notes:
                    existing.notes = notes
                if tags:
                    existing.tags = tags
                existing.save()
                updated += 1
            else:
                Contact.objects.create(
                    tenant=tenant, name=name, phone=phone, email=email,
                    company_name=company, language=language, external_id=ext,
                    notes=notes, tags=tags, status="active")
                created += 1
        except Exception as e:
            logger.warning("crm.importer: row %d failed: %s", idx, type(e).__name__)
            skipped += 1
            errors.append({"row": idx, "reason": type(e).__name__})

    return {"total": len(rows), "created": created, "updated": updated,
            "skipped": skipped, "errors": errors[:50]}
