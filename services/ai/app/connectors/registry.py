"""Connector registry — central lookup for all installed connectors."""
from .base import BaseConnector, ConnectorMeta, ConnectorStatus


class ConnectorRegistry:
    def __init__(self):
        self._connectors: dict[str, BaseConnector] = {}

    def register(self, connector: BaseConnector) -> None:
        self._connectors[connector.meta.id] = connector

    def get(self, connector_id: str) -> BaseConnector | None:
        return self._connectors.get(connector_id)

    def all(self) -> list[BaseConnector]:
        return list(self._connectors.values())

    async def status_all(self) -> list[dict]:
        results = []
        for c in self._connectors.values():
            status = await c.status()
            results.append({
                "id": c.meta.id,
                "name": c.meta.name,
                "description": c.meta.description,
                "icon": c.meta.icon,
                "status": status.value,
                "requires_browser": c.meta.requires_browser,
                "platform": c.meta.platform,
            })
        return results


# Singleton — imported by routes and background sync
registry = ConnectorRegistry()


def setup_registry() -> None:
    """Called at sidecar startup to register all available connectors."""
    from .gmail import GmailConnector
    from .whatsapp_web import WhatsAppWebConnector

    registry.register(GmailConnector())
    registry.register(WhatsAppWebConnector())

    # iMessage only available on macOS
    import sys
    if sys.platform == "darwin":
        from .imessage import IMessageConnector
        registry.register(IMessageConnector())
