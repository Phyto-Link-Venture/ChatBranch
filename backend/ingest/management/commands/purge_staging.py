"""Retention: delete old staging rows (and their empty batches).

SQLite has no partitions, so retention is a scheduled delete. Only processed
rows are removed by default; unprocessed / dead-lettered rows are kept so
nothing is silently lost. The raw payload is discarded here, so run this only
past your audit-retention window.

  python manage.py purge_staging --older-than-days 90
  python manage.py purge_staging --older-than-days 90 --include-unprocessed --yes
"""
from datetime import timedelta

from django.core.management.base import BaseCommand
from django.utils import timezone

from ingest.models import RecordSyncStaging, SyncBatch


class Command(BaseCommand):
    help = "Delete staging rows older than N days (retention)."

    def add_arguments(self, parser):
        parser.add_argument("--older-than-days", type=int, required=True)
        parser.add_argument("--include-unprocessed", action="store_true",
                            help="Also delete rows that never processed (dangerous).")
        parser.add_argument("--yes", action="store_true", help="Skip confirmation.")

    def handle(self, *args, **opts):
        cutoff = timezone.now() - timedelta(days=opts["older_than_days"])
        rows = RecordSyncStaging.objects.filter(created_at__lt=cutoff)
        if not opts["include_unprocessed"]:
            rows = rows.filter(processed=True)
        count = rows.count()
        if count == 0:
            self.stdout.write("Nothing to purge.")
            return
        if not opts["yes"]:
            self.stdout.write(self.style.WARNING(
                f"Would delete {count} staging row(s) older than {cutoff:%Y-%m-%d}. "
                f"Re-run with --yes to proceed."))
            return
        deleted, _ = rows.delete()
        # Drop batches left with no rows.
        empty = SyncBatch.objects.filter(rows__isnull=True)
        empty_count = empty.count()
        empty.delete()
        self.stdout.write(self.style.SUCCESS(
            f"Deleted {deleted} staging row(s); removed {empty_count} empty batch(es)."))
