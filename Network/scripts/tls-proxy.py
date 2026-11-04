#!/usr/bin/env python3
"""Minimal TLS-terminating TCP proxy for local testing (stands in for Traefik).

AWS CLI v2 only uses aws-chunked uploads with trailing checksums over HTTPS, so testing the
gateway's #139 checks needs TLS in front of it. Usage:
    tls-proxy.py LISTEN_PORT TARGET_PORT CERT.pem KEY.pem
"""
import asyncio, ssl, sys

async def pipe(reader, writer):
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.IncompleteReadError):
        pass
    finally:
        writer.close()

async def handle(client_reader, client_writer, target):
    try:
        upstream_reader, upstream_writer = await asyncio.open_connection("127.0.0.1", target)
    except OSError:
        client_writer.close()
        return
    await asyncio.gather(pipe(client_reader, upstream_writer), pipe(upstream_reader, client_writer))

async def main():
    listen, target, cert, key = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4]
    ctx = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
    ctx.load_cert_chain(cert, key)
    server = await asyncio.start_server(lambda r, w: handle(r, w, target), "127.0.0.1", listen, ssl=ctx)
    async with server:
        await server.serve_forever()

asyncio.run(main())
