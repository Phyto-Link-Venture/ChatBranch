from django.urls import path

from . import views

urlpatterns = [
    path("records", views.sync_records, name="sync_records"),
    path("health", views.sync_health, name="sync_health"),
    path("batches/<int:batch_id>", views.batch_status, name="sync_batch_status"),
]
