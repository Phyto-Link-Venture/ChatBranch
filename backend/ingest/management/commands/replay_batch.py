"""Re-normalize a batch from its immutable raw staging rows.

Use after fixing the normalizer, or to reprocess rows that previously errored.
Resets the batch's staging rows to unprocessed, then runs the normalizer again.

  python manage.py replay_batch 12
  python manage.py replay_batch 12 --failed-only
"""
from django.core.management.base import BaseCommand

from ingest.models import SyncBatch
from ingest.normalizer import process_batch


class Command(BaseCommand):
    help = "Reprocess a sync batch from raw staging."

    def add_arguments(self, parser):
        parser.add_argument("batch_id", type=int)
        parser.add_argument("--failed-only", action="store_true",
                            help="Only reset rows that previously errored.")

    def handle(self, *args, **opts):
        batch = SyncBatch.objects.filter(pk=opts["batch_id"]).first()
        if batch is None:
            self.stderr.write(self.style.ERROR(f"Batch {opts['batch_id']} not found."))
            return
        rows = batch.rows.all()
        if opts["failed_only"]:
            rows = rows.exclude(process_error="")
        # Clear dead-letter + attempt counters so the drain retries them.
        reset = rows.update(processed=False, process_error="", processed_at=None,
                            attempts=0, dead_lettered=False)
        # rows_upserted is cumulative; reset the counter for a clean re-run.
        batch.rows_upserted = 0
        batch.save(update_fields=["rows_upserted"])
        self.stdout.write(f"Reset {reset} row(s); reprocessing batch #{batch.pk}...")
        summary = process_batch(batch)
        self.stdout.write(self.style.SUCCESS(
            f"Done: {summary['upserted']} upserted, {summary['failed']} failed, "
            f"status={summary['status']}."
        ))
