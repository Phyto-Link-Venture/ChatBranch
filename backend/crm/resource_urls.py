from django.urls import path

from . import import_views, resource_views

urlpatterns = [
    path("resources", resource_views.resources),
    path("resources/<int:resource_id>", resource_views.resource_detail),
    path("resources/<int:resource_id>/links", resource_views.resource_links),
    path("resources/<int:resource_id>/links/<int:link_id>",
         resource_views.resource_link_delete),
    path("contacts/import", import_views.contact_import),
]
