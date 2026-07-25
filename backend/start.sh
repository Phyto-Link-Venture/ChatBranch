#!/bin/sh
# Railway/production entrypoint. Local dev keeps `manage.py runserver` —
# see CLAUDE.md "Run". Migrations run here so every deploy is schema-current.
set -e

python manage.py migrate --noinput
python manage.py collectstatic --noinput

# Voice agent worker shares this container so the file-based IPC under
# data/ (live transcripts, operator inbox) stays on one filesystem.
# download-files fetches the turn-detector/VAD models the plugins need —
# the container image doesn't ship them.
if [ "$RUN_VOICE_AGENT" = "1" ]; then
  (cd voice_agent && { python agent.py download-files || true; } && python agent.py start &)
fi

exec gunicorn core.wsgi:application \
  --bind "0.0.0.0:${PORT:-8000}" \
  --worker-class gthread --workers "${WEB_CONCURRENCY:-2}" --threads 8 \
  --timeout 120 --access-logfile -
