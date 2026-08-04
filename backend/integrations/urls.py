from django.urls import path

from . import mcp_views, views

urlpatterns = [
    path("providers", views.providers),
    path("settings", views.settings_view),
    path("accounts", views.accounts),
    path("accounts/<int:account_id>", views.account_detail),
    path("accounts/<int:account_id>/test", views.account_test),
    path("accounts/<int:account_id>/disconnect", views.account_disconnect),
    path("mcp", mcp_views.servers),
    path("mcp/<int:server_id>", mcp_views.server_detail),
    path("mcp/<int:server_id>/test", mcp_views.server_test),
    path("oauth/<str:provider>/start", views.oauth_start),
    path("oauth/<str:provider>/callback", views.oauth_callback),
    path("webhook/shopify", views.shopify_webhook),
    path("webhook/stripe", views.stripe_webhook),
]
