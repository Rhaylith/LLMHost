# Linked devices: threat model

Linking lets one of your computers use the models of another. It is **off by default**. This page says what it protects against, and what it does not.

## What is built

* Every device has an ECDSA P-256 key pair (`~/.llmhost/link/device.key`, the private key encrypted with the Windows user's DPAPI). The device id is the first 16 hex characters of the SHA-256 of the public key.
* **Pairing** uses a six-digit code that one device shows and the other types. The code is valid for five minutes and one successful pairing; five wrong tries end it. Both sides prove they know the code (an HMAC over the public keys), so someone who sits between the two devices and does not know the code cannot swap in a key of their own.
* **Every request between devices is signed**: method, path and query, a timestamp (accepted within a minute), a one-time nonce and the SHA-256 of the body. A request is accepted once; a copy replayed later is refused. The receiving device checks the signature against the key it stored at pairing.
* **Permissions per device**: `chat` (use the models: `/v1/*`, the model list, device info), `load` (load and unload), `manage` (downloads and other model management). A new device gets `chat` only. Nothing else (settings, tokens, chats, presets, files) is reachable by a linked device.
* Requests from other devices appear in the request log with the caller `link:<device>`; refused requests are written to the log.
* The allow-list, request size limits and rate limits (settings → Network and limits) apply to linked requests like to any other.

## What it assumes

* **The network is yours.** The traffic between devices is as private as the network you run it over (Tailscale, ZeroTier and WireGuard encrypt it; a plain LAN does not). Signed requests cannot be forged or altered, but over plain HTTP they can be read. Use HTTPS (settings → Network and limits) or an encrypted network when that matters. Devices accept self-signed HTTPS certificates of each other, because identity comes from the signatures, not from the certificate.
* **Pairing happens between people who can see both screens.** The code is the secret; show it only to the device you mean to pair. Pairing needs a server that is switched on and linking enabled on the device that shows the code, so nothing listens for pairing otherwise.
* **Clocks are roughly right** (within a minute).
* A paired device with `load` or `manage` can make this device load models or download files, and so use its memory, disk and bandwidth. Grant these only to devices you control.

## What it does not do

* No relay and no tunnelling: both devices must be able to reach each other's address. No automatic discovery on the LAN (addresses are entered once when pairing).
* No revocation list beyond removing the device: remove a lost device on every device that knew it.
* A linked device sees the prompts it sends and the answers, and the model list of the other device; it does not see anything else.
