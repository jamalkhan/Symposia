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

## Options

| Option | Default | |
|---|---|---|
| `--data-dir DIR` | `~/.symposia/node` | Holds the identity key, settings, secrets, chain state and role data |
| `--port-offset N` | `0` | Adds N to every default port (UI 5170, gateway 8080, anvil 8545, storage 5180, compute 5190) |
| `--ui-port P` | `5170 + offset` | |

Any setting can be fixed with an environment variable, for example `SYMPOSIA__Storage__Enabled=true` or `SYMPOSIA__Network__Mode=Join`. Settings fixed this way are shown as locked in the UI.

## Files in the data dir

| File | |
|---|---|
| `node.key` (+ `.initialized`) | Node identity. **Back this up.** A host that loses it after initialization refuses to start rather than silently becoming a new node |
| `settings.json` | Everything set in the UI |
| `secrets.json` (0600) | Admin password hash and relayer key |
| `anvil-state.json`, `network.json` | Local dev chain state and deployed contract addresses |
| `storage/`, `compute/` | Role data, kept in a separate folder per network (`<chainId>-<registry>`) |
