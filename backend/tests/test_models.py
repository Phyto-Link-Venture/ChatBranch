import pytest
from django.contrib.auth.models import User


@pytest.mark.django_db
class TestUser:
    def test_user_creation(self):
        user = User.objects.create_user(
            username='testuser',
            email='test@example.com',
            password='testpass123'
        )
        assert user.username == 'testuser'
        assert user.email == 'test@example.com'

    def test_user_check_password(self, user):
        assert user.check_password('testpass123')
        assert not user.check_password('wrongpassword')
