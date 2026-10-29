"""Profile-scoped OS credentials. Never select third-party or plaintext backends."""

import hashlib
import json
import sys

from app.storage import data_dir


class CredentialUnavailable(RuntimeError):
    def __init__(self):
        super().__init__(
            "OS credential storage is unavailable or locked. Unlock your login keychain and retry."
        )


class CredentialStore:
    KEYS = frozenset({"gmail-client", "gmail-token", "credential-test"})

    def __init__(self, backend=None):
        self._backend = backend
        profile = (
            str(data_dir().resolve()).casefold()
            if sys.platform == "win32"
            else str(data_dir().resolve())
        )
        self.service = "LocalMind/" + hashlib.sha256(profile.encode()).hexdigest()[:24]

    def _native(self):
        if self._backend is None:
            # Explicit imports also let PyInstaller collect exactly these backends.
            if sys.platform == "win32":
                from keyring.backends.Windows import WinVaultKeyring

                self._backend = WinVaultKeyring()
            elif sys.platform == "darwin":
                from keyring.backends.macOS import Keyring

                self._backend = Keyring()
            elif sys.platform == "linux":
                from keyring.backends.SecretService import Keyring

                self._backend = Keyring()
            else:
                raise CredentialUnavailable()
        return self._backend

    def _check(self, key):
        if key not in self.KEYS:
            raise ValueError("Unknown credential provider.")

    def read(self, key):
        self._check(key)
        try:
            raw = self._native().get_password(self.service, key)
            value = json.loads(raw) if raw else None
            if value is not None and not isinstance(value, dict):
                raise ValueError()
            return value
        except Exception:
            raise CredentialUnavailable() from None

    def write(self, key, value):
        self._check(key)
        try:
            raw = json.dumps(value, separators=(",", ":"))
            if not isinstance(value, dict) or len(raw.encode("utf-16-le")) > 2400:
                raise ValueError()
            self._native().set_password(self.service, key, raw)
            if self.read(key) != value:
                raise ValueError()
        except Exception:
            raise CredentialUnavailable() from None

    def delete(self, key):
        self._check(key)
        try:
            backend = self._native()
            if backend.get_password(self.service, key) is not None:
                backend.delete_password(self.service, key)
        except Exception:
            raise CredentialUnavailable() from None
