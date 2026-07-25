import os
import sys

from django.apps import AppConfig


class WhatsappConfig(AppConfig):
    default_auto_field = "django.db.models.BigAutoField"
    name = "whatsapp"

    def ready(self):
        """Start the channel-health checker inside the serving process so
        Cloud/SMTP accounts are probed with no external cron (interval:
        WHATSAPP_HEALTH_INTERVAL seconds, default 300).

        Same gates as the flows ticker (flows/apps.py): RUN_MAIN=='true' is
        the runserver autoreload CHILD (the process that actually serves);
        WHATSAPP_HEALTH_CHECKS=1 opts in for non-runserver processes
        (wsgi/daphne); WHATSAPP_HEALTH_CHECKS=0 kills it everywhere. Nothing
        starts under migrate/tests/shell."""
        if os.environ.get("WHATSAPP_HEALTH_CHECKS") == "0":
            return
        serving = (
            os.environ.get("RUN_MAIN") == "true"
            or os.environ.get("WHATSAPP_HEALTH_CHECKS") == "1"
            or ("runserver" in sys.argv and "--noreload" in sys.argv)
        )
        if not serving:
            return
        from . import health
        health.start_checker()
