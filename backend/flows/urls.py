from django.urls import path

from . import views

urlpatterns = [
    path("library", views.library),
    path("runs", views.runs),
    path("queue", views.queue_jobs),
    path("tick", views.tick_now),
    path("status", views.status),
]
