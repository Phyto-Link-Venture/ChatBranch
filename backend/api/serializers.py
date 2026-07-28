from rest_framework import serializers

from crm.models import Contact, Record
from ingest.models import Brand


class ContactSerializer(serializers.ModelSerializer):
    # The brands this (tenant-shared) contact is active on — for the detail
    # page's per-brand membership editor. Managed via /contacts/<id>/brands.
    brands = serializers.SerializerMethodField()

    class Meta:
        model = Contact
        fields = [
            "id", "name", "phone", "email", "language",
            "company_name", "status", "method",
            "external_id", "tags", "attributes",
            "last_synced_at", "created_at", "updated_at", "brands",
        ]
        read_only_fields = [
            "id", "name", "phone", "email", "language",
            "company_name", "status", "method",
            "external_id", "tags", "attributes",
            "last_synced_at", "created_at", "updated_at",
        ]

    def get_brands(self, obj):
        return [{"slug": bl.brand.slug, "name": bl.brand.name}
                for bl in obj.brand_links.all()]


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
