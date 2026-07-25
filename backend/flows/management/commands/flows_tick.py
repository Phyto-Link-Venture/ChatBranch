"""Run the flow engine by hand (or from a real cron/scheduled task):

    manage.py flows_tick            one pass: resume due runs + fire triggers
    manage.py flows_tick --loop     keep ticking every engine.TICK_SECONDS

The web process normally ticks by itself (flows/apps.py); this command is the
external seam for demos and deployments that prefer cron over threads."""
import time

from django.core.management.base import BaseCommand

from core import app_settings
from flows import engine


class Command(BaseCommand):
    help = "Run one flow-engine pass (triggers + due resumes); --loop to keep going."

    def add_arguments(self, parser):
        parser.add_argument("--loop", action="store_true",
                            help=f"tick every {engine.TICK_SECONDS}s until Ctrl+C")

    def handle(self, *args, **options):
        while True:
            result = engine.tick()
            self.stdout.write(self.style.SUCCESS(
                f"flows: started={result['started']} resumed={result['resumed']} "
                f"drained={result['drained']}"))
            if not options["loop"]:
                return
            time.sleep(app_settings.flows_tick_seconds())
