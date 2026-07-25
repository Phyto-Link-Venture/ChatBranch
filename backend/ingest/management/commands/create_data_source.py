"""Onboard an external data source: create (or reuse) a tenant, create the
source, and mint an API key. The raw key is printed ONCE and only its hash is
stored.

  python manage.py create_data_source --tenant-slug acme --tenant-name "Acme" \
      --source-name "Acme Shopify"

  # rotate an existing source's key:
  python manage.py create_data_source --tenant-slug acme --source-name "Acme Shopify" --rotate
"""
import secrets

from django.core.management.base import BaseCommand
from django.utils.text import slugify

from ingest.models import DataSource, Tenant
from ingest.security import hash_api_key


class Command(BaseCommand):
    help = "Create a tenant + data source and mint a sync API key."

    def add_arguments(self, parser):
        parser.add_argument("--tenant-slug", required=True)
        parser.add_argument("--tenant-name", default="")
        parser.add_argument("--source-name", required=True)
        parser.add_argument("--rotate", action="store_true",
                            help="Reuse the existing data source and issue a new key.")

    def handle(self, *args, **opts):
        slug = slugify(opts["tenant_slug"])
        tenant, t_created = Tenant.objects.get_or_create(
            slug=slug, defaults={"name": opts["tenant_name"] or opts["tenant_slug"]},
        )
        source, s_created = DataSource.objects.get_or_create(
            tenant=tenant, name=opts["source_name"],
        )
        if not s_created and not opts["rotate"]:
            self.stderr.write(self.style.WARNING(
                f"Data source '{source.name}' already exists. Pass --rotate to issue a new key."))
            return

        raw_key = "sk_" + secrets.token_hex(24)
        source.api_key_hash = hash_api_key(raw_key)
        source.is_active = True
        source.save(update_fields=["api_key_hash", "is_active"])

        self.stdout.write(self.style.SUCCESS("Data source ready."))
        self.stdout.write(f"  tenant : {tenant.name} ({tenant.slug}){' [new]' if t_created else ''}")
        self.stdout.write(f"  source : {source.name} (id={source.pk})")
        self.stdout.write("")
        self.stdout.write(self.style.WARNING("  API key (shown once — store it now):"))
        self.stdout.write(f"    {raw_key}")
        self.stdout.write("")
        self.stdout.write("  Send it as the  X-API-Key  header to POST /api/v1/sync/records")
