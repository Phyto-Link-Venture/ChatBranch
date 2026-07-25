"""Consistent error envelope for the v1 API.

Every error response is shaped:

    {"error": {"code": "<slug>", "message": "<human>", "detail": <optional>}}

matching the envelope the `ingest` intake already returns, so the whole
`/api/v1` surface is uniform.
"""
from __future__ import annotations

from rest_framework.views import exception_handler as drf_exception_handler


# Map common DRF status codes to stable, machine-readable slugs.
_CODE_BY_STATUS = {
    400: "bad_request",
    401: "unauthorized",
    403: "forbidden",
    404: "not_found",
    405: "method_not_allowed",
    406: "not_acceptable",
    409: "conflict",
    415: "unsupported_media_type",
    429: "rate_limited",
    500: "server_error",
}


def envelope_exception_handler(exc, context):
    response = drf_exception_handler(exc, context)
    if response is None:
        return None  # unhandled -> Django 500 (dev shows trace)

    data = response.data
    code = getattr(exc, "default_code", None) or _CODE_BY_STATUS.get(
        response.status_code, "error")

    # Pull a human message; keep the raw DRF payload as `detail`.
    if isinstance(data, dict) and "detail" in data and len(data) == 1:
        message = str(data["detail"])
        detail = None
    else:
        message = "Request failed."
        detail = data

    body = {"error": {"code": str(code), "message": message}}
    if detail is not None:
        body["error"]["detail"] = detail
    response.data = body
    return response
