"""Recompute `Thread.entity` for existing conversations.

Run this after binding channel accounts to entities (Settings → Channels →
the Entity column). Threads created before the binding carry no entity, so
they sit in the "unassigned" bucket until something touches them; this walks
every thread and applies the same rule the live write paths use
(crm/scoping.py `thread_entity`): receiving account's entity, else the linked
contact's entity, else unassigned.

Safe to re-run — it is a pure recompute, and it only writes rows whose
resolved entity actually changed.

    venv/bin/python manage.py backfill_entity_scope [--dry-run]
"""
from django.core.management.base import BaseCommand

from crm import scoping
from crm.models import Thread


class Command(BaseCommand):
    help = "Recompute Thread.entity from the account/contact attribution rule."

    def add_arguments(self, parser):
        parser.add_argument("--dry-run", action="store_true",
                            help="Report what would change without writing.")

    def handle(self, *args, **options):
        dry = options["dry_run"]
        changed = unassigned = 0
        qs = Thread.objects.select_related("account", "account__entity", "contact")
        total = qs.count()

        for thread in qs.iterator():
            resolved = scoping.thread_entity(account=thread.account,
                                             contact=thread.contact)
            new_id = resolved.pk if resolved else None
            if new_id is None:
                unassigned += 1
            if thread.entity_id == new_id:
                continue
            changed += 1
            label = resolved.slug if resolved else "unassigned"
            self.stdout.write(f"  [{thread.channel}] {thread.key} -> {label}")
            if not dry:
                Thread.objects.filter(pk=thread.pk).update(entity=resolved)

        verb = "would change" if dry else "updated"
        self.stdout.write(self.style.SUCCESS(
            f"{total} thread(s) scanned; {changed} {verb}; "
            f"{unassigned} resolve to unassigned "
            f"(shared number, contact not linked to a single entity)."))
        if unassigned and not dry:
            self.stdout.write(
                "Unassigned chats stay visible in every entity view and in "
                "All entities — bind the receiving number to an entity, or link "
                "the contact, to file them under an outlet.")
