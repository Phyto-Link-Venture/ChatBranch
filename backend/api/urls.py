"""v1 API URL root.

New/migrated DRF endpoints mount here under /api/v1/. Later PRs add routers for
contacts, playbook, settings, etc. This PR establishes the foundation +
self-describing schema.
"""
from django.urls import path
from drf_spectacular.views import (
    SpectacularAPIView,
    SpectacularSwaggerView,
)

from . import views

app_name = "api_v1"

urlpatterns = [
    path("ping", views.PingView.as_view(), name="ping"),
    path("whoami", views.WhoAmIView.as_view(), name="whoami"),
    path("contacts", views.ContactListView.as_view(), name="contact_list"),
    path("contacts/<int:pk>", views.ContactDetailView.as_view(), name="contact_detail"),
    path("contacts/<int:pk>/entities", views.ContactEntitiesView.as_view(), name="contact_entities"),
    path("entities", views.EntitiesView.as_view(), name="entities"),
    path("entities/<int:entity_id>", views.EntityDetailView.as_view(), name="entity_detail"),
    path("users", views.UsersView.as_view(), name="users"),
    path("users/<int:user_id>", views.UserDetailView.as_view(), name="user_detail"),
    # OpenAPI 3 schema + interactive docs.
    path("schema", SpectacularAPIView.as_view(), name="schema"),
    path("docs", SpectacularSwaggerView.as_view(url_name="api_v1:schema"), name="docs"),
]
