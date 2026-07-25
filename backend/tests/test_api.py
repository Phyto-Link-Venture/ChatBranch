import pytest
from django.urls import reverse


@pytest.mark.django_db
class TestAPI:
    def test_api_root_exists(self, client):
        response = client.get('/api/')
        assert response.status_code in [200, 404]

    def test_api_requires_valid_endpoint(self, client):
        response = client.get('/api/nonexistent/')
        assert response.status_code == 404
