"""aicore — channel-agnostic AI logic shared by every text channel.

assistant.py is the reply brain, memory.py the cross-channel contact memory,
persona.py the configurable persona layer. A Django app ONLY so apps.py can
start the idle-chat summarizer ticker — no models, no migrations (memory rows
live in crm.ConversationSummary). It queries the crm models at call time
(CRM enrichment), so callers must be inside a Django process — all current
callers are.
"""
