import os
import sys

from django.apps import AppConfig


class AicoreConfig(AppConfig):
    default_auto_field = "django.db.models.BigAutoField"
    name = "aicore"

    def ready(self):
        """Start the idle-chat summarizer ticker inside the serving process
        (aicore/memory.summarize_idle_chats every AICORE_SUMMARY_INTERVAL s).

        Same gates as flows/apps.py: RUN_MAIN=='true' is the runserver
        autoreload CHILD (the process that actually serves — the parent must
        not tick too); AICORE_TICKER=1 opts in for non-runserver processes
        (wsgi/daphne); AICORE_TICKER=0 kills it everywhere. Nothing starts
        under migrate/tests/shell."""
        if os.environ.get("AICORE_TICKER") == "0":
            return
        serving = (
            os.environ.get("RUN_MAIN") == "true"
            or os.environ.get("AICORE_TICKER") == "1"
            or ("runserver" in sys.argv and "--noreload" in sys.argv)
        )
        if not serving:
            return
        from . import memory
        memory.start_ticker()
