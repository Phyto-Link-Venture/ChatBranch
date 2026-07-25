"""Pagination for the v1 API.

Default is page-number (matches the existing DRF PAGE_SIZE). A cursor paginator
is provided for large, append-only lists (activity, transcripts) where deep
page offsets are expensive.
"""
from rest_framework.pagination import CursorPagination, PageNumberPagination
from rest_framework.response import Response


class StandardPagination(PageNumberPagination):
    page_size = 50
    page_size_query_param = "page_size"
    max_page_size = 200

    def get_paginated_response(self, data):
        return Response({
            "count": self.page.paginator.count,
            "page": self.page.number,
            "num_pages": self.page.paginator.num_pages,
            "page_size": self.get_page_size(self.request),
            "next": self.get_next_link(),
            "previous": self.get_previous_link(),
            "results": data,
        })


class TimeCursorPagination(CursorPagination):
    page_size = 50
    max_page_size = 200
    page_size_query_param = "page_size"
    ordering = "-created_at"
