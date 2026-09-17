"""Relay live Meta Avatar packets. Python 3.10+, standard library only.

Each TCP message is a big-endian uint32 length followed by that many bytes.
The first message is UTF-8 JSON: version=1, role=publisher|subscriber, channel.
The server acknowledges with a framed JSON {"ok": true}. Remaining messages
are opaque MAV1 pose packets; see docs/AVATAR_NETWORKING.md for the layout.
One publisher per channel, many subscribers. Slow clients get the latest pose.
"""

import argparse
import asyncio
import json
import logging
import math
import re
import struct
from dataclasses import dataclass, field

MAX_PACKET = 1024 * 1024
MAX_HELLO = 1024
POSE_HEADER = struct.Struct("<4sId7f")
LOG = logging.getLogger("avatar-relay")


async def read_frame(reader, maximum=MAX_PACKET):
    size = struct.unpack("!I", await reader.readexactly(4))[0]
    if not 0 < size <= maximum:
        raise ValueError(f"Invalid frame size: {size}")
    return await reader.readexactly(size)


async def write_frame(writer, payload):
    writer.write(struct.pack("!I", len(payload)) + payload)
    await asyncio.wait_for(writer.drain(), timeout=5)


def validate_pose(payload):
    if not POSE_HEADER.size < len(payload) <= MAX_PACKET:
        raise ValueError("Invalid pose packet size")
    magic, _, timestamp, *pose = POSE_HEADER.unpack_from(payload)
    if magic != b"MAV1" or not all(math.isfinite(x) for x in [timestamp, *pose]):
        raise ValueError("Invalid pose header")
    if not 0.5 < sum(x * x for x in pose[3:]) < 1.5:
        raise ValueError("Invalid root quaternion")


@dataclass
class Channel:
    publisher: object = None
    subscribers: set = field(default_factory=set)


class AvatarRelay:
    def __init__(self):
        self.channels = {}

    async def handle_client(self, reader, writer):
        channel = None
        channel_name = None
        queue = None
        role = None
        acknowledged = False
        peer = writer.get_extra_info("peername")
        try:
            raw = await asyncio.wait_for(read_frame(reader, MAX_HELLO), timeout=5)
            hello = json.loads(raw)
            if not isinstance(hello, dict):
                raise ValueError("Expected a JSON handshake object")
            role = hello.get("role")
            channel_name = hello.get("channel")
            if hello.get("version") != 1 or role not in ("publisher", "subscriber"):
                raise ValueError("Unsupported version or role")
            if not isinstance(channel_name, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", channel_name):
                raise ValueError("Channel must be 1-64 ASCII letters, digits, _ or -")
            channel = self.channels.setdefault(channel_name, Channel())
            if role == "publisher":
                if channel.publisher is not None:
                    raise ValueError("This channel already has a publisher")
                channel.publisher = writer
            else:
                queue = asyncio.Queue(maxsize=1)
                channel.subscribers.add(queue)

            await write_frame(writer, b'{"ok":true}')
            acknowledged = True
            LOG.info("%s connected: channel=%s peer=%s", role, channel_name, peer)
            if role == "publisher":
                while True:
                    payload = await read_frame(reader)
                    validate_pose(payload)
                    for subscriber in tuple(channel.subscribers):
                        if subscriber.full():
                            subscriber.get_nowait()
                        subscriber.put_nowait(payload)
            else:
                # Watch for disconnect even while no publisher is sending.
                closed = asyncio.create_task(reader.read(1))
                sender = asyncio.create_task(self._send_poses(queue, writer))
                try:
                    done, _ = await asyncio.wait((closed, sender), return_when=asyncio.FIRST_COMPLETED)
                    for task in done:
                        task.result()
                finally:
                    closed.cancel()
                    sender.cancel()
                    await asyncio.gather(closed, sender, return_exceptions=True)
        except (ValueError, UnicodeError) as exc:
            LOG.warning("Rejected %s: %s", peer, exc)
            if not acknowledged:
                try:
                    await write_frame(writer, json.dumps({"ok": False, "error": str(exc)}).encode())
                except (ConnectionError, OSError, asyncio.TimeoutError):
                    pass
        except (asyncio.IncompleteReadError, ConnectionError, OSError, asyncio.TimeoutError):
            pass
        finally:
            if channel is not None:
                if channel.publisher is writer:
                    channel.publisher = None
                    # Do not replay queued poses from a previous publisher session.
                    for subscriber in channel.subscribers:
                        while not subscriber.empty():
                            subscriber.get_nowait()
                if queue is not None:
                    channel.subscribers.discard(queue)
                if channel.publisher is None and not channel.subscribers:
                    self.channels.pop(channel_name, None)
            writer.close()
            try:
                await writer.wait_closed()
            except (ConnectionError, OSError):
                pass
            if acknowledged:
                LOG.info("%s disconnected: channel=%s peer=%s", role, channel_name, peer)

    @staticmethod
    async def _send_poses(queue, writer):
        while True:
            await write_frame(writer, await queue.get())


async def serve(host, port):
    relay = AvatarRelay()
    server = await asyncio.start_server(relay.handle_client, host, port)
    LOG.info("Listening on %s:%s (trusted classroom LAN; no authentication)", host, port)
    async with server:
        await server.serve_forever()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=5060)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("port must be between 1 and 65535")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    try:
        asyncio.run(serve(args.host, args.port))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
