"""No network inside the validation layer: block socket connections and name resolution for this process.

`python -m validation` calls `block_network()` before anything else, and the tests check it. The only child process the
CLI starts is the local .NET build/export, which has its own tripwire (tests/Backtester.Tests/NoNetworkTests.cs).
"""

from __future__ import annotations

import socket


class NetworkBlocked(RuntimeError):
    pass


def _refuse(*_args, **_kwargs):
    raise NetworkBlocked("network access is disabled in the validation layer")


def block_network() -> None:
    socket.socket.connect = _refuse  # type: ignore[method-assign]
    socket.socket.connect_ex = _refuse  # type: ignore[method-assign]
    socket.create_connection = _refuse  # type: ignore[assignment]
    socket.getaddrinfo = _refuse  # type: ignore[assignment]
    socket.gethostbyname = _refuse  # type: ignore[assignment]
