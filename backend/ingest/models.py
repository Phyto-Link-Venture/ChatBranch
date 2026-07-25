"""External-system sync ingestion + multi-tenancy foundation.

Flow:
  source system POST /api/v1/sync/records  (x-api-key)
    -> SyncBatch (one per push, idempotent on data_source + source_timestamp)
    -> RecordSyncStaging (raw JSON per record, immutable landing zone)
    -> normalizer UPSERTs into crm.Contact + crm.Record (see ingest/normalizer.py)

Multi-tenancy: every row carries a Tenant. SQLite has no row-level security,
so isolation is enforced at the application layer (querysets always filter by
the current tenant; the source system's api-key resolves the tenant for
ingestion). When the DB moves to Postgres, add RLS policies on tenant_id as
defense in depth.
"""
from django.db import models


class Tenant(models.Model):
    """An organization using the platform (SaaS contact). Root of isolation."""

    STATUS_CHOICES = [("active", "Active"), ("suspended", "Suspended")]

    name = models.CharField(max_length=160)
    slug = models.SlugField(max_length=80, unique=True)
    status = models.CharField(max_length=12, choices=STATUS_CHOICES, default="active")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    def __str__(self):
        return self.name


class Brand(models.Model):
    """A client brand a tenant operates. A tenant owns many brands; contacts
    are shared across a tenant's brands (see crm.ContactBrand). Access control
    is brand-level: admins see all brands in their tenant, agents only the
    brands assigned to them (crm.Profile.brands)."""

    tenant = models.ForeignKey(Tenant, on_delete=models.CASCADE, related_name="brands")
    name = models.CharField(max_length=160)
    slug = models.SlugField(max_length=80)
    is_active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        unique_together = [("tenant", "slug")]

    def __str__(self):
        return f"{self.name} ({self.tenant.slug})"


class DataSource(models.Model):
    """An external system whose records a tenant works from — a shop, an ERP, a
    ticketing system, a partner API. It is the sync origin and feeds one Brand.

    The source authenticates its sync with an x-api-key; we store only a hash of
    it (`api_key_hash`) and compare in constant time.
    """

    tenant = models.ForeignKey(Tenant, on_delete=models.CASCADE, related_name="data_sources")
    brand = models.ForeignKey(Brand, null=True, blank=True, on_delete=models.SET_NULL,
                              related_name="data_sources")
    name = models.CharField(max_length=160)
    api_key_hash = models.CharField(max_length=64, blank=True, default="", db_index=True)
    is_active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        unique_together = [("tenant", "name")]

    def __str__(self):
        return f"{self.name} ({self.tenant.slug})"


class SyncBatch(models.Model):
    """One row per source-system push. Audit + replay anchor + idempotency key."""

    STATUS_CHOICES = [
        ("received", "Received"),
        ("processing", "Processing"),
        ("completed", "Completed"),
        ("partial", "Partial"),
        ("failed", "Failed"),
    ]

    tenant = models.ForeignKey(Tenant, on_delete=models.CASCADE, related_name="sync_batches")
    data_source = models.ForeignKey(DataSource, on_delete=models.CASCADE,
                                    related_name="sync_batches")
    source_timestamp = models.DateTimeField(null=True, blank=True)  # payload's sync_timestamp
    active_record_count = models.IntegerField(null=True, blank=True)
    status = models.CharField(max_length=12, choices=STATUS_CHOICES, default="received")
    rows_received = models.IntegerField(default=0)
    rows_upserted = models.IntegerField(default=0)
    rows_failed = models.IntegerField(default=0)
    error_detail = models.JSONField(default=dict, blank=True)
    started_at = models.DateTimeField(null=True, blank=True)
    finished_at = models.DateTimeField(null=True, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["-created_at"]
        constraints = [
            # Idempotency: a given source push (identified by its source_timestamp)
            # lands exactly once. Replay of the same payload is a no-op.
            models.UniqueConstraint(
                fields=["data_source", "source_timestamp"],
                condition=~models.Q(source_timestamp=None),
                name="uniq_syncbatch_source_ts",
            ),
        ]

    def __str__(self):
        return f"batch#{self.pk} {self.data_source.name} [{self.status}]"


class RecordSyncStaging(models.Model):
    """Immutable raw landing zone: one row per record object in the payload.
    Never mutated except to flip `processed`; the source of truth for replay."""

    tenant = models.ForeignKey(Tenant, on_delete=models.CASCADE, related_name="staging_rows")
    sync_batch = models.ForeignKey(SyncBatch, on_delete=models.CASCADE, related_name="rows")
    record_external_id = models.CharField(max_length=64, blank=True, default="", db_index=True)
    raw_payload = models.JSONField()
    processed = models.BooleanField(default=False)
    process_error = models.TextField(blank=True, default="")
    processed_at = models.DateTimeField(null=True, blank=True)
    # Retry/dead-letter: each failed normalization attempt bumps this. Once it
    # hits the max, the row is dead-lettered (skipped by the drain) until a
    # human resets it via `replay_batch`.
    attempts = models.IntegerField(default=0)
    dead_lettered = models.BooleanField(default=False)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["id"]
        indexes = [
            models.Index(fields=["tenant", "record_external_id"]),
            # worker pickup of unprocessed rows
            models.Index(fields=["processed"], condition=models.Q(processed=False),
                         name="idx_staging_unprocessed"),
        ]

    def __str__(self):
        return f"staging#{self.pk} {self.record_external_id} processed={self.processed}"
