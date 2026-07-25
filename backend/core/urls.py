from django.contrib import admin
from django.urls import path, include
from django.conf import settings
from django.conf.urls.static import static

from core import settings_api

urlpatterns = [
    path('admin/', admin.site.urls),
    path('api/settings/ai-providers', settings_api.ai_providers),
    path('api/', include('calls.urls')),
    path('api/v1/sync/', include('ingest.urls')),
    path('api/v1/', include('api.urls')),
    path('api/whatsapp/', include('whatsapp.urls')),
    path('api/messaging/', include('messaging.urls')),
    path('api/flows/', include('flows.urls')),
    path('api/aicore/', include('aicore.urls')),
    path('api/integrations/', include('integrations.urls')),
    path('api/auth/', include('crm.urls')),
    path('api-auth/', include('rest_framework.urls')),
]

if settings.DEBUG:
    urlpatterns += static(settings.MEDIA_URL, document_root=settings.MEDIA_ROOT)
    urlpatterns += static(settings.STATIC_URL, document_root=settings.STATIC_ROOT)
