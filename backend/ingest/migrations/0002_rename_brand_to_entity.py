from django.db import migrations


class Migration(migrations.Migration):
    """Rename Brand -> Entity (table ingest_brand -> ingest_entity) and the
    DataSource.brand FK -> entity. Data-preserving RenameModel/RenameField."""

    dependencies = [
        ("ingest", "0001_initial"),
        # The crm tables created at 0001/0002 carry FKs that, at THEIR point in
        # history, still name `ingest.brand` (Profile.brands, ContactBrand,
        # KnowledgeItem.brand). Without this edge Django is free to order the
        # rename first, and building those historical models then fails with
        # "Related model 'ingest.brand' cannot be resolved" — which is exactly
        # what a fresh migrate (and therefore the whole test suite) hit.
        ("crm", "0002_knowledgeitem"),
    ]

    operations = [
        migrations.RenameModel(old_name="Brand", new_name="Entity"),
        migrations.RenameField(model_name="datasource", old_name="brand", new_name="entity"),
    ]
