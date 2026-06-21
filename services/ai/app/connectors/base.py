"""Base connector interface. All connectors implement this."""
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from typing import Any


class ConnectorStatus(str, Enum):
    DISCONNECTED = "disconnected"
    CONNECTING = "connecting"
    CONNECTED = "connected"
    ERROR = "error"
    SYNCING = "syncing"


@dataclass
class ConnectorMeta:
    id: str                   # e.g. "gmail", "whatsapp_web"
    name: str                 # e.g. "Gmail"
    description: str
    icon: str                 # emoji or icon name
    requires_browser: bool = False   # True for Playwright-based connectors
    platform: str = "all"    # "all" | "macos" | "windows"


@dataclass
class SyncResult:
    connector_id: str
    items_synced: int
    cursor: str               # opaque cursor for next incremental sync
    synced_at: datetime = field(default_factory=datetime.utcnow)
    error: str | None = None


class BaseConnector(ABC):
    """Every connector inherits from this."""

    meta: ConnectorMeta

    @abstractmethod
    async def status(self) -> ConnectorStatus:
        """Current connection state."""
        ...

    @abstractmethod
    async def connect(self, **kwargs) -> dict[str, Any]:
        """
        Initiate connection. May return:
          - {"type": "oauth_url", "url": "..."} — redirect user to browser
          - {"type": "qr", "data_url": "..."} — show QR code in UI
          - {"type": "ready"} — no auth needed (e.g. iMessage on macOS)
        """
        ...

    @abstractmethod
    async def disconnect(self) -> None:
        """Revoke tokens, close browser sessions, clean up."""
        ...

    @abstractmethod
    async def sync(self, cursor: str | None = None) -> SyncResult:
        """Pull new data since cursor. Upsert into local DB. Return new cursor."""
        ...

    async def search(self, query: str, limit: int = 10) -> list[dict]:
        """Optional: connector-specific search (e.g. Gmail search syntax)."""
        return []

    async def context_for_buddy(self, query: str) -> str:
        """
        Return a short text block Buddy can use as real-time context.
        Called just before sending a chat message if the connector is relevant.
        e.g. "Recent Gmail (last 24h): 3 unread from Priya about the project..."
        """
        return ""
