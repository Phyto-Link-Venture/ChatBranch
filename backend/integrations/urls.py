from django.urls import path

from . import views

urlpatterns = [
    path("providers", views.providers),
    path("settings", views.settings_view),
    path("accounts", views.accounts),
    path("accounts/<int:account_id>", views.account_detail),
    path("accounts/<int:account_id>/test", views.account_test),
    path("accounts/<int:account_id>/disconnect", views.account_disconnect),
    path("oauth/<str:provider>/start", views.oauth_start),
    path("oauth/<str:provider>/callback", views.oauth_callback),
    path("webhook/shopify", views.shopify_webhook),
    path("webhook/stripe", views.stripe_webhook),
]
