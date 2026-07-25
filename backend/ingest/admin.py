from django.contrib import admin

from .models import Tenant, DataSource, SyncBatch, RecordSyncStaging


@admin.register(Tenant)
class TenantAdmin(admin.ModelAdmin):
    list_display = ("name", "slug", "status", "created_at")
    search_fields = ("name", "slug")


@admin.register(DataSource)
class DataSourceAdmin(admin.ModelAdmin):
    list_display = ("name", "tenant", "is_active", "created_at")
    list_filter = ("is_active", "tenant")
    search_fields = ("name",)
    # api_key_hash is intentionally not editable in the UI; mint keys via
    # `manage.py create_data_source`.
    readonly_fields = ("api_key_hash",)


@admin.register(SyncBatch)
class SyncBatchAdmin(admin.ModelAdmin):
    list_display = ("id", "data_source", "status", "rows_received", "rows_upserted",
                    "rows_failed", "source_timestamp", "created_at")
    list_filter = ("status", "data_source")
    readonly_fields = ("error_detail",)
    date_hierarchy = "created_at"


@admin.register(RecordSyncStaging)
class RecordSyncStagingAdmin(admin.ModelAdmin):
    list_display = ("id", "record_external_id", "sync_batch", "processed",
                    "attempts", "dead_lettered", "created_at")
    list_filter = ("processed", "dead_lettered", "tenant")
    search_fields = ("record_external_id",)
    readonly_fields = ("raw_payload",)
