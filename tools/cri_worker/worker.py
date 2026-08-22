# /// script
# requires-python = ">=3.13"
# dependencies = [
#     "msgpack>=1.1.2",
#     "pycricodecsex[usm]>=0.0.5",
#     "pycryptodome>=3.23.0",
# ]
# ///
"""
Persistent CRI codec worker for NuggetDl. Talks msgpack over stdin/stdout,
one request-map in, one response-map out, self-delimited by the msgpack
stream (no extra framing needed - both msgpack.Unpacker and MessagePack-CSharp
read exactly one packed value at a time off a stream).

Kept as an isolated Python process because PyCriCodecsEx
has no maintained C# equivalent - see tools/cri_worker's role in the wider
port plan. Only decoded/demuxed track bytes cross the boundary; C# owns all
file I/O, bundle resolution, and CLI concerns.

Run directly: `uv run worker.py` (uv resolves the deps above into a cached,
ephemeral venv on first run). stdin/stdout carry the protocol; all logging
goes to stderr so it never corrupts the msgpack stream.
"""

import logging
import sys

import msgpack

from acb import ACB
from usm import GameUsmDemuxer

logging.basicConfig(stream=sys.stderr, level=logging.INFO)
logger = logging.getLogger("cri_worker")


def _decode_acb(req: dict) -> dict:
    acb = ACB(req["acb"], req.get("awb") or b"")
    tracks = acb.extract_tracks(req["key"])
    return {"tracks": [{"name": name, "wav": wav} for name, wav in tracks]}


def _demux_usm(req: dict) -> dict:
    usm = GameUsmDemuxer(req["usm"], req["key"])
    return {"tracks": usm.tracks()}


_OPS = {
    "decode_acb": _decode_acb,
    "demux_usm": _demux_usm,
}


def handle(req: dict) -> dict:
    op = req.get("op")
    handler = _OPS.get(op)
    if handler is None:
        raise ValueError(f"Unknown op: {op!r}")
    return handler(req)


def main() -> None:
    # Fed manually via read1() + feed(), NOT `Unpacker(sys.stdin.buffer)`:
    # that constructor form has the Unpacker call the file-like's plain
    # .read(n), which on a BufferedReader blocks trying to accumulate the
    # full n bytes rather than returning as soon as something is available -
    # fine for a bounded file, but it deadlocks a live request/response pipe
    # (the client is waiting on our reply and will never send more than one
    # request's worth of bytes up front). read1() returns after a single
    # underlying read, so a short request unblocks it immediately.
    stdin = sys.stdin.buffer
    out = sys.stdout.buffer
    packer = msgpack.Packer(use_bin_type=True)
    unpacker = msgpack.Unpacker(raw=False, max_buffer_size=0)

    logger.info("cri_worker ready")

    while True:
        chunk = stdin.read1(65536)
        if not chunk:
            break  # stdin closed

        unpacker.feed(chunk)
        for req in unpacker:
            req_id = req.get("id") if isinstance(req, dict) else None
            try:
                result = handle(req)
                response = {"id": req_id, "ok": True, "error": None, **result}
            except Exception as e:
                logger.exception(
                    "Request %s (%s) failed", req_id, req.get("op") if isinstance(req, dict) else None
                )
                response = {"id": req_id, "ok": False, "error": str(e), "tracks": None}

            out.write(packer.pack(response))
            out.flush()

    logger.info("stdin closed, exiting")


if __name__ == "__main__":
    main()
