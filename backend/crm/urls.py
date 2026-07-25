from django.urls import path

from . import views

urlpatterns = [
    path("login", views.login_view),
    path("logout", views.logout_view),
    path("me", views.me),
    path("notifications", views.notifications),
    path("notification-prefs", views.notification_prefs),
    path("audit", views.audit_log),
    path("users", views.users),
    path("users/<int:user_id>", views.user_detail),
]
