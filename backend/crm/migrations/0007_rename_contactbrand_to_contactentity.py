from django.db import migrations


class Migration(migrations.Migration):
    """Rename ContactBrand -> ContactEntity and every Brand FK/M2M field to
    entity/entities (ContactEntity.brand, KnowledgeItem.brand, Profile.brands).
    Data-preserving; depends on the ingest rename so the FK target exists."""

    dependencies = [
        ("crm", "0006_llmusage"),
        ("ingest", "0002_rename_brand_to_entity"),
    ]

    operations = [
        migrations.RenameModel(old_name="ContactBrand", new_name="ContactEntity"),
        migrations.RenameField(model_name="contactentity", old_name="brand", new_name="entity"),
        migrations.RenameField(model_name="knowledgeitem", old_name="brand", new_name="entity"),
        migrations.RenameField(model_name="profile", old_name="brands", new_name="entities"),
    ]
