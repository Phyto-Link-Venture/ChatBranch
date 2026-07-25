"""Drain unprocessed staging rows into crm.Contact + crm.Record.

Run on a schedule (cron / Celery beat) when intake is configured to defer
normalization (INGEST_PROCESS_INLINE=False), or manually any time.

  python manage.py process_sync
  python manage.py process_sync --source 3 --limit 5
"""
from django.core.management.base import BaseCommand

from ingest.models import DataSource
from ingest.normalizer import process_pending


class Command(BaseCommand):
    help = "Normalize pending sync staging rows into contacts + records."

    def add_arguments(self, parser):
        parser.add_argument("--source", type=int, default=None, help="Only this data source id.")
        parser.add_argument("--limit", type=int, default=None, help="Max batches to process.")

    def handle(self, *args, **opts):
        source = None
        if opts["source"]:
            source = DataSource.objects.filter(pk=opts["source"]).first()
            if source is None:
                self.stderr.write(self.style.ERROR(f"Data source {opts['source']} not found."))
                return
        totals = process_pending(data_source=source, limit_batches=opts["limit"])
        self.stdout.write(self.style.SUCCESS(
            f"Processed {totals['batches']} batch(es): "
            f"{totals['upserted']} upserted, {totals['failed']} failed."
        ))
