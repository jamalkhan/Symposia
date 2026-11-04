# Symposia Node Host

One process that runs everything a Symposia node needs (issue #133):

- the **Blockchain Gateway**,
- a password-protected **config and diagnostics web UI**,
- the **storage role**, when you turn it on,
- the **compute role**, when you turn it on.

Settings persist, so a restart comes back exactly as it was.

## Run it

```bash
dotnet run --project Network/Symposia.NodeHost
```

1. Open the config UI URL it prints (default <http://localhost:5170>) and set an admin password. If you're setting it up from another machine, you also need the **setup token** printed in the console. This stops anyone else on the network from claiming a fresh node.
2. Under **Settings → Network**, pick one of:
   - **Local dev network**: starts a local chain (anvil) and gateway on this machine and deploys the contracts. Requires [Foundry](https://book.getfoundry.sh/getting-started/installation) and a repo checkout.
   - **Join a network**: enter another network's gateway URL. This is all you need for a testnet, or for a second host on your machine.
   - **Run a gateway for a remote chain**: for foundation hosts (Base Sepolia, the Symposia L3). Needs a relayer key.
3. Turn on **Storage** and/or **Compute** and click **Save and apply**. For compute without the Neon binaries, tick **Networking only**.

The **Diagnostics** tab shows:
- each component's status;
- the chain and the contract addresses;
- this node's on-chain registration;
- registry members;
- the handshake result for every peer;
- recent logs.

## Two nodes on one machine

```bash
dotnet run --project Network/Symposia.NodeHost                                      # host A → http://localhost:5170
dotnet run --project Network/Symposia.NodeHost -- --data-dir /tmp/node-b --port-offset 1   # host B → http://localhost:5171
```

1. On host A, choose **Local dev network** and turn on storage.
2. On host B, click **Join it** (or choose **Join a network** with `http://127.0.0.1:8080`) and turn on storage.
3. After about one discovery interval, each host's Diagnostics page lists the other as **Recognized**.

To do the whole thing automatically: `Network/scripts/node-host-smoke.sh`.

## Two machines on a LAN

Each storage node registers the address other nodes use to reach it. The default is `127.0.0.1`, which only works on one machine. On each host, open **Settings → Storage role** and click **Use <your LAN IP>** next to *Advertised endpoint*, which fills in something like `http://10.0.0.213:5180`. Then save.

The second machine joins with **Join a network** and the first machine's gateway, for example `http://10.0.0.213:8080`. If it starts before that gateway is reachable, it keeps retrying every 5 to 60 seconds; you don't need to restart it.

## Storing files (S3)

Turn on **Settings → Blob gateway** on one host, click **Generate a new key pair**, copy the secret, and save. The gateway finds every storage node on the network through the on-chain registry. An upload succeeds once **one** node has it (*write quorum*), and the gateway copies it to a second node in the background (*minimum copies*). That second copy normally happens during the upload itself, or within seconds of a missing node coming back.

```bash
export AWS_ACCESS_KEY_ID=<key id> AWS_SECRET_ACCESS_KEY=<secret> AWS_DEFAULT_REGION=us-east-1
S3="aws --endpoint-url http://localhost:9000"        # or https://symposia.chateaukhan.com
$S3 s3 mb s3://proof
$S3 s3 cp ./file.bin s3://proof/file.bin          # multipart above 8 MB, handled (#5)
$S3 s3 cp s3://proof/file.bin ./back.bin
```

**What the gateway checks on every upload:**
- the chunk signatures and checksums the AWS CLI and SDKs send (#139);
- for multipart uploads, each part as it arrives, and the assembled object against its full-object checksum when the client sends one.

**Gateway to storage nodes:** the gateway signs every storage call with this host's node key, and storage nodes accept calls only from registered members of the network (#140). The host running the gateway must therefore be registered, which means Storage or Compute is on.

Diagnostics shows how many storage nodes the gateway can reach and how many objects are waiting for their second copy. The Network tab shows a Gateway light for each node.

**Public endpoint:** [`docs/ops/traefik/symposia.yml`](../../docs/ops/traefik/symposia.yml) is the Traefik route for `https://symposia.chateaukhan.com`. Only the gateway port (9000) needs to be reachable from Traefik. Keep the storage ports, including the gRPC ports `6180+`, LAN-only.

To run a two-node S3 check automatically: `Network/scripts/s3-cluster-smoke.sh`.

## Options

| Option | Default | |
|---|---|---|
| `--data-dir DIR` | `~/.symposia/node` | Holds the identity key, settings, secrets, chain state and role data |
| `--port-offset N` | `0` | Adds N to every default port (UI 5170, gateway 8080, anvil 8545, storage 5180, storage gRPC 6180, compute 5190, blob gateway 9000) |
| `--ui-port P` | `5170 + offset` | |

Any setting can be fixed with an environment variable, for example `SYMPOSIA__Storage__Enabled=true` or `SYMPOSIA__Network__Mode=Join`. Settings fixed this way are shown as locked in the UI.

## Files in the data dir

| File | |
|---|---|
| `node.key` (+ `.initialized`) | Node identity. **Back this up.** A host that loses it after initialization refuses to start rather than silently becoming a new node |
| `settings.json` | Everything set in the UI |
| `secrets.json` (0600) | Admin password hash, relayer key and the blob gateway's secret key |
| `anvil-state.json`, `network.json` | Local dev chain state and deployed contract addresses |
| `storage/`, `compute/`, `blobgateway/` | Role data, kept in a separate folder per network (`<chainId>-<registry>`) |
