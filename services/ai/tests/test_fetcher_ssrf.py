import unittest
from unittest.mock import AsyncMock, patch

import httpx

from app.search import fetcher


class FakeStream:
    def __init__(self, address):
        self.address = address

    def get_extra_info(self, name):
        return (self.address, 443) if name == "server_addr" else None


def response(address):
    return httpx.Response(200, headers={"content-type": "text/html"}, content=b"<p>ok</p>",
                          extensions={"network_stream": FakeStream(address)})


class ConnectedPeerTests(unittest.IsolatedAsyncioTestCase):
    def test_public_peer_is_allowed_and_private_peer_is_blocked(self):
        fetcher._check_connected_peer(response("93.184.216.34"))
        for address in ("127.0.0.1", "10.1.2.3", "169.254.169.254"):
            with self.subTest(address=address), self.assertRaises(fetcher.SSRFError):
                fetcher._check_connected_peer(response(address))

    async def test_name_that_resolves_publicly_but_connects_privately_is_refused(self):
        # DNS rebinding: the first lookup looks fine, the connection goes to localhost.
        with patch.object(fetcher, "_check_url"), \
             patch.object(httpx.AsyncClient, "send", AsyncMock(return_value=response("127.0.0.1"))):
            with self.assertRaises(fetcher.SSRFError):
                await fetcher.fetch_evidence("https://rebind.example.com/page")


if __name__ == "__main__":
    unittest.main()
