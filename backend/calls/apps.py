import sys

from django.apps import AppConfig


class CallsConfig(AppConfig):
    default_auto_field = "django.db.models.BigAutoField"
    name = "calls"

    def ready(self):
        # R2 persistence of data/ (media, recordings, JSON state) — inert
        # without the R2_* env vars. Skipped for one-off management commands
        # and tests; only server processes need the files restored.
        argv = " ".join(sys.argv)
        if any(k in argv for k in (
            "test", "migrate", "makemigrations", "collectstatic", "shell",
            "loaddata", "dumpdata", "seed_demo", "check",
        )):
            return
        from core import r2sync
        r2sync.start()
