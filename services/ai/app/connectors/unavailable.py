"""Visible disabled adapters with no path to provider code or authorization."""
from .base import BaseConnector, ConnectorCapabilities, ConnectorMeta, ConnectorStatus, SyncResult


class UnavailableConnector(BaseConnector):
    capabilities = ConnectorCapabilities(authorization="disabled", read=False, sync=False, revoke=False)

    def __init__(self, connector_id, name, platform="all"):
        self.meta = ConnectorMeta(connector_id, name, "Not enabled in this beta.", "plug", platform=platform)
        self.detail = "Disabled until authorization, revoke and recovery checks pass. Existing local data is unchanged."

    async def status(self):
        return ConnectorStatus.DISABLED

    async def connect(self, **kwargs):
        return {"type": "error", "message": self.detail}

    async def disconnect(self):
        return None

    async def sync(self, cursor=None):
        return SyncResult(self.meta.id, 0, cursor or "", error=self.detail)
