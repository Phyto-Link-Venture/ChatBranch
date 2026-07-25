from django.contrib import admin

from .models import Integration


@admin.register(Integration)
class IntegrationAdmin(admin.ModelAdmin):
    list_display = ("name", "provider", "status", "connected", "last_ok_at", "created_at")
    list_filter = ("provider", "status")
    search_fields = ("name", "external_id")
    # Credentials are never editable from the admin — they arrive through the
    # OAuth callback or the masked API, and showing them here would defeat the
    # "secrets never leave the server" rule.
    exclude = ("credentials",)
    readonly_fields = ("status", "status_detail", "status_changed_at", "last_ok_at",
                       "scopes", "created_at", "updated_at")

    @admin.display(boolean=True)
    def connected(self, obj):
        return obj.connected
