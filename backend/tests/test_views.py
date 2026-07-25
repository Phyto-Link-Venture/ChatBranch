import pytest
from django.urls import reverse


@pytest.mark.django_db
class TestViews:
    def test_admin_login_page_exists(self, client):
        response = client.get('/admin/login/')
        assert response.status_code == 200

    def test_authenticated_user_can_access_admin(self, authenticated_client):
        response = authenticated_client.get('/admin/')
        assert response.status_code in [200, 302]
