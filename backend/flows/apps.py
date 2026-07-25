import os
import sys

from django.apps import AppConfig


class FlowsConfig(AppConfig):
    default_auto_field = "django.db.models.BigAutoField"
    name = "flows"

    def ready(self):
        """Start the engine ticker inside the serving process so time-based
        triggers fire with no external cron.

        Gates: RUN_MAIN=='true' is the runserver autoreload CHILD (the process
        that actually serves — the parent must not tick too); FLOWS_TICKER=1
        opts in for non-runserver processes (wsgi/daphne); FLOWS_TICKER=0
        kills it everywhere. Nothing starts under migrate/tests/shell."""
        if os.environ.get("FLOWS_TICKER") == "0":
            return
        serving = (
            os.environ.get("RUN_MAIN") == "true"
            or os.environ.get("FLOWS_TICKER") == "1"
            or ("runserver" in sys.argv and "--noreload" in sys.argv)
        )
        if not serving:
            return
        from . import engine
        engine.start_ticker()
