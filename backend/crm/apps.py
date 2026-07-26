from django.apps import AppConfig


class CrmConfig(AppConfig):
    default_auto_field = "django.db.models.BigAutoField"
    name = "crm"

    def ready(self):
        # Seed the RBAC capability matrix after this app's migrations run, so a
        # bare `migrate` (no seed_demo) still leaves every DB — including the
        # pytest test DB — with the default matrix present. Idempotent
        # (get_or_create), never clobbers operator edits.
        from django.db.models.signals import post_migrate

        post_migrate.connect(_seed_permissions, sender=self)


def _seed_permissions(sender, **kwargs):
    try:
        from .permissions_store import ensure_default_permissions

        ensure_default_permissions()
    except Exception:  # pragma: no cover - never break migrate on a seed hiccup
        pass
