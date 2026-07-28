from django.db import migrations


class Migration(migrations.Migration):
    """Rename Brand -> Entity (table ingest_brand -> ingest_entity) and the
    DataSource.brand FK -> entity. Data-preserving RenameModel/RenameField."""

    dependencies = [
        ("ingest", "0001_initial"),
    ]

    operations = [
        migrations.RenameModel(old_name="Brand", new_name="Entity"),
        migrations.RenameField(model_name="datasource", old_name="brand", new_name="entity"),
    ]
