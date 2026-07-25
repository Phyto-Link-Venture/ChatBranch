from django.contrib import admin

from .models import (
    AgentSkill,
    AuditLog,
    CallRecord,
    ChannelAccount,
    Contact,
    FlowRun,
    KnowledgeItem,
    Message,
    Notification,
    Profile,
    Record,
    RecordEvent,
    Setting,
    Thread,
    Workflow,
)


@admin.register(Profile)
class ProfileAdmin(admin.ModelAdmin):
    list_display = ("user", "role")
    list_filter = ("role",)


@admin.register(Notification)
class NotificationAdmin(admin.ModelAdmin):
    list_display = ("user", "kind", "title", "read", "created_at")
    list_filter = ("kind", "read")


@admin.register(AuditLog)
class AuditLogAdmin(admin.ModelAdmin):
    list_display = ("created_at", "actor", "action", "target", "ip")
    list_filter = ("action",)
    search_fields = ("actor", "target", "detail")
    readonly_fields = ("user", "actor", "action", "target", "detail", "ip", "created_at")


class RecordInline(admin.TabularInline):
    model = Record
    extra = 0
    fields = ("type", "external_id", "title", "status", "amount", "due_date")
    show_change_link = True


@admin.register(Contact)
class ContactAdmin(admin.ModelAdmin):
    list_display = ("name", "phone", "email", "company_name", "status", "external_id")
    list_filter = ("status", "method", "language")
    search_fields = ("name", "phone", "email", "external_id")
    inlines = [RecordInline]


class RecordEventInline(admin.TabularInline):
    model = RecordEvent
    extra = 0


@admin.register(Record)
class RecordAdmin(admin.ModelAdmin):
    list_display = ("external_id", "type", "title", "status", "amount",
                    "currency", "due_date", "contact")
    list_filter = ("type", "status")
    search_fields = ("external_id", "title", "contact__name", "contact__phone")
    inlines = [RecordEventInline]


@admin.register(RecordEvent)
class RecordEventAdmin(admin.ModelAdmin):
    list_display = ("record", "kind", "at", "amount")
    list_filter = ("kind",)


@admin.register(ChannelAccount)
class ChannelAccountAdmin(admin.ModelAdmin):
    list_display = ("channel", "name", "external_id", "owner", "created_at")
    list_filter = ("channel",)


class MessageInline(admin.TabularInline):
    model = Message
    extra = 0
    readonly_fields = ("created_at",)


@admin.register(Thread)
class ThreadAdmin(admin.ModelAdmin):
    list_display = ("channel", "name", "key", "ai_enabled", "call_status", "updated_at")
    list_filter = ("channel", "ai_enabled")
    search_fields = ("name", "key", "address")
    inlines = [MessageInline]


@admin.register(Message)
class MessageAdmin(admin.ModelAdmin):
    list_display = ("thread", "direction", "source", "status", "created_at")
    list_filter = ("direction", "source", "status")
    search_fields = ("text",)


@admin.register(CallRecord)
class CallRecordAdmin(admin.ModelAdmin):
    list_display = ("name", "phone", "outcome", "needs_human", "followup_date", "ended_at")
    list_filter = ("outcome", "needs_human")
    search_fields = ("name", "phone", "summary")


@admin.register(Setting)
class SettingAdmin(admin.ModelAdmin):
    list_display = ("key", "updated_at")
    search_fields = ("key",)


@admin.register(Workflow)
class WorkflowAdmin(admin.ModelAdmin):
    list_display = ("name", "flow_id", "active", "updated_by", "updated_at")
    list_filter = ("active",)
    search_fields = ("name", "flow_id")


@admin.register(KnowledgeItem)
class KnowledgeItemAdmin(admin.ModelAdmin):
    list_display = ("title", "kind", "priority", "active", "starts_at", "ends_at",
                    "updated_at")
    list_filter = ("kind", "active")
    search_fields = ("title", "question", "content")


@admin.register(AgentSkill)
class AgentSkillAdmin(admin.ModelAdmin):
    list_display = ("name", "skill_id", "position")
    search_fields = ("name", "skill_id")


@admin.register(FlowRun)
class FlowRunAdmin(admin.ModelAdmin):
    list_display = ("workflow", "contact", "status", "current_node",
                    "wait_for", "wait_until", "started_at", "finished_at")
    list_filter = ("status", "workflow")
    search_fields = ("contact__name", "contact__phone")
    readonly_fields = ("started_at", "updated_at")
