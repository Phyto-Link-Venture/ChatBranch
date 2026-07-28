from django.urls import path

from . import views

urlpatterns = [
    path("contacts", views.contacts),
    path("contacts/<int:contact_id>/channels", views.contact_channels),
    path("contacts/<int:contact_id>/calls", views.contact_calls),
    path("calls/recording/<str:call_id>", views.call_recording),
    path("voices", views.voices_list),
    path("call-logs", views.call_logs),
    path("voice/preview", views.voice_preview),
    path("voice/clone", views.voice_clone),
    path("voice/import", views.voice_import),
    path("voice/playground", views.voice_playground),
    path("voice/playground/end", views.voice_playground_end),
    path("voice/stt-settings", views.stt_settings),
    path("voice/llm-settings", views.llm_settings),
    path("voice/<str:voice_id>", views.voice_detail),
    path("pronunciation", views.pronunciation_settings),
    path("calls/dial", views.dial),
    # Must precede calls/<str:room> below — that single-segment wildcard would
    # otherwise swallow "calls/script-templates" as room="script-templates".
    path("calls/script-templates", views.script_templates),
    path("calls/script-templates/<int:template_id>", views.script_template_detail),
    path("calls/<str:room>/live", views.live),
    path("calls/<str:room>/say", views.say),
    path("calls/<str:room>/hangup", views.hangup),
    path("calls/<str:room>/escalate", views.escalate),
    path("calls/<str:room>", views.summary),
    path("analytics/summary", views.analytics_summary),
    path("analytics/usage", views.analytics_usage),
]
