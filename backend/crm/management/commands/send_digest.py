"""Send (or preview) the daily ops digest to admins — for external cron.

  manage.py send_digest              # deliver in-app to all admins
  manage.py send_digest --email      # also email each admin (needs SMTP)
  manage.py send_digest --print      # print the body, deliver nothing
  manage.py send_digest --hours 24   # window length (default 24h)
"""
from datetime import timedelta

from django.core.management.base import BaseCommand
from django.utils import timezone

from crm import digest


class Command(BaseCommand):
    help = "Send the daily ops digest to admins (in-app + optional email)."

    def add_arguments(self, parser):
        parser.add_argument("--hours", type=int, default=24,
                            help="Window length in hours ending now (default 24).")
        parser.add_argument("--email", action="store_true",
                            help="Also email each admin (requires SMTP configured).")
        parser.add_argument("--print", action="store_true", dest="print_only",
                            help="Print the digest body and deliver nothing.")

    def handle(self, *args, **opts):
        until = timezone.now()
        since = until - timedelta(hours=opts["hours"])
        d = digest.build_digest(since, until)
        if opts["print_only"]:
            self.stdout.write(digest.render_text(d))
            return
        result = digest.deliver(d, email=opts["email"])
        self.stdout.write(self.style.SUCCESS(
            f"Digest delivered — {result['notified']} in-app, {result['emailed']} emailed."))
