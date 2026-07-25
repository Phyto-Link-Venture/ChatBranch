from rest_framework import serializers

from crm.models import Contact, Record
from ingest.models import Brand


class ContactSerializer(serializers.ModelSerializer):
    class Meta:
        model = Contact
        fields = [
            "id", "name", "phone", "email", "language",
            "company_name", "status", "method",
            "external_id", "tags", "attributes",
            "last_synced_at", "created_at", "updated_at",
        ]
        read_only_fields = fields


class RecordSerializer(serializers.ModelSerializer):
    class Meta:
        model = Record
        fields = [
            "id", "contact", "type", "external_id", "title", "status",
            "amount", "currency", "due_date", "data",
            "created_at", "updated_at",
        ]
        read_only_fields = fields


class BrandSerializer(serializers.ModelSerializer):
    tenant = serializers.SlugRelatedField(slug_field="slug", read_only=True)

    class Meta:
        model = Brand
        fields = ["id", "name", "slug", "tenant", "is_active", "created_at"]
        read_only_fields = fields
