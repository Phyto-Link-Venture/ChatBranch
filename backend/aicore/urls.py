from django.urls import path

from . import views

urlpatterns = [
    path("persona", views.persona_view),
    path("knowledge", views.knowledge_list),
    path("knowledge/preview", views.knowledge_preview),
    path("knowledge/<int:item_id>", views.knowledge_detail),
    path("skills", views.skills_view),
    path("booking-hours", views.booking_hours_view),
]
