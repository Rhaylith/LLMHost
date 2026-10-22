# Linked devices

Lets one computer use the models of another. Off by default. Design background: [implementation-plan.md](implementation-plan.md) §8.1 and §7.1. What it does and does not protect against: [docs/link-threat-model.md](docs/link-threat-model.md). Back to the [README](README.md).

## Design decision

No VPN is built. Your network (LAN, Tailscale, ZeroTier, WireGuard) carries the traffic; linking adds identity, pairing, permissions and routing on top.

## Using it

1. Enable on both computers: Settings → Linked devices, or `llmhost link enable`. Set a name with `link set-device-name`.
2. On one computer start pairing (`POST /api/link/pair/start`, or `link pair start`). It shows a six-digit code, valid five minutes and for one pairing.
3. On the other computer enter the first computer's address and the code (Settings, or `link pair <address> <code>`). Both sides answer with an HMAC over the keys, so a device in the middle without the code cannot swap a key.
4. Set what the peer may do: `chat` (use models), `load` (load/unload), `manage` (downloads and other model management). CLI `link permissions`.
5. The peer's models appear in the model list and picker, grouped by device. A request for `<device>/<model>`, or the preferred device (`link set-preferred-device`), is proxied. If the preferred device is offline the request falls back to the local model with a notice.

CLI summary: `link enable|disable|status|set-device-name|set-preferred-device|pair|peers|unpair|permissions`. Without a running server these verbs say pairing needs one and exit 2/5.

## How it works

`Core/Link.cs` (`LinkManager`, `LinkSettings`, `LinkPeer`) and `Server/LinkApi.cs`.

* **Identity:** an ECDSA P-256 key pair per device (Ed25519 is not in the .NET class library). The private key is encrypted with the Windows user's DPAPI under `~/.llmhost/link/`; the device id is a hash of the public key.
* **Signed requests:** method, path and query, a timestamp (accepted within a minute), a one-time nonce and the SHA-256 of the body are signed. A replayed or altered request is refused.
* **Routing:** `LinkRouter` proxies streaming requests with cancellation. Remote requests appear in the request log with caller `link:<device>`; the allow-list, size limits and rate limits apply to them.
* **Discovery:** peers are configured by address. mDNS discovery and QR pairing are not built.

## Tests

`tests/LLMHost.Tests/LinkTests.cs` runs two in-process hosts on loopback ports: pairing, wrong codes, signatures, replay, permissions, routing, failover.
