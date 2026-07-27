# RelicableKeygen — Early Alpha access codes

Maintainer-only tool. It holds the private key that signs access codes, so it is never
shipped to users and `keys/` is gitignored.

## How the scheme works

An access code is an **ECDSA (P-256 / SHA-256) signature** over a tiny plaintext payload:

```
RLC1.<base64url(payload)>.<base64url(signature)>

payload = "<owner>|<expiry-days-since-epoch>|<serial>"
signed  = "RLC1|" + payload
```

The plugin ships **only the public key** (`AlphaCode.PublicKeyBase64`). You hold the private
key. That gives three properties:

1. **Codes cannot be forged.** Anyone can decompile the DLL and read the public key; it does
   not help them mint a code. Editing the owner name or pushing the expiry out invalidates
   the signature — both cases are covered by the round-trip tests below.
2. **Codes name their owner.** The owner string is displayed permanently in the plugin window
   (`Early Alpha — access: Someone#1234`). A code passed to a friend keeps showing the name
   of the person it was issued to, on the friend's screen. That is the anti-sharing mechanism:
   not prevention, **attribution**.
3. **Codes expire on their own.** No action needed to end an alpha wave.

The format is defined in exactly one file — `Relicable/Licensing/AlphaCode.cs` — which this
tool **links by source** rather than copying. The minting side and the verifying side
therefore cannot drift apart; a change to either breaks both builds at once.

### What this is not

Any client-side gate can be removed by patching the assembly, and this one is no exception.
It stops an unfinished alpha spreading casually and makes a leak attributable. It is not
security, and nothing should be built on top of it that assumes secrecy.

## First-time setup

Run once, from the repository root:

```bash
dotnet run --project tools/RelicableKeygen -- init
```

This generates the keypair, writes the private key to `keys/relicable-alpha.key`, and patches
the matching public key straight into `AlphaCode.cs` so it can never be mistyped in transit.

> **Back `keys/relicable-alpha.key` up somewhere off this machine.** If you lose it you cannot
> issue another code without shipping a plugin update that invalidates every existing one.
> `init` refuses to overwrite an existing key unless you pass `--force`, for the same reason.

Then rebuild the plugin so the new public key is compiled in.

## Issuing a code

```bash
dotnet run --project tools/RelicableKeygen -- mint --owner "Someone#1234" --days 90
```

Use whatever identifier the tester will recognise as theirs and you can trace back — a Discord
handle is the usual choice. Options:

| Flag | Meaning |
| --- | --- |
| `--owner` | Required. Shown in their plugin window. Cannot contain `\|`. |
| `--days N` | Expiry in N days from today. Default 90. |
| `--until YYYY-MM-DD` | Explicit expiry date instead of `--days`. |
| `--serial S` | Override the random serial (normally leave it alone). |
| `--key PATH` | Use a different signing key. |

`mint` verifies the code it just produced before printing it, and warns loudly if the key you
signed with does not match the public key currently compiled into `AlphaCode.cs` — that mismatch
is the one way to hand out a code that silently does not work.

## Checking and revoking

```bash
dotnet run --project tools/RelicableKeygen -- verify RLC1.xxxx.yyyy    # exactly as the plugin will
dotnet run --project tools/RelicableKeygen -- inspect RLC1.xxxx.yyyy   # read contents, skip the signature
```

To kill one specific code before its expiry, add its serial to `RevokedSerials` in
`AlphaCode.cs` and ship a plugin update:

```csharp
private static readonly string[] RevokedSerials =
{
    "d16b22e1",
};
```

Revocation only reaches users who update. For anything urgent, run `init --force` to rotate the
keypair — that invalidates **every** code at once and requires reissuing all of them.

## If sharing becomes a problem

The design above is deliberately the lightest thing that works. Escalate only if you actually
see leaks, because every step costs your testers friction:

**1. Shorten the window.** Drop from 90 days to 14–30. A leaked code dies fast and you find
out who leaked it at renewal time, since the name is right there in the code.

**2. Bind codes to a machine or character.** Have the plugin show a fingerprint (a hash of the
character's ContentId, or a machine GUID), have the tester send it to you, and sign
`fingerprint|expiry` instead of `owner|expiry`. The plugin then checks the signature *and* that
the fingerprint matches locally, so a shared code simply fails on anyone else's PC. This is the
strongest offline option. Costs: a two-step handshake per tester, and it breaks whenever someone
reinstalls Windows or switches characters — expect to reissue often. Everything needed for this
is already in place; only the payload layout and one local comparison change (bump the prefix to
`RLC2` so old codes fail with a clear message).

**3. Automate issuance from Discord.** A bot that mints a code keyed to the requester's Discord
ID and DMs it to them. Removes your manual step and makes the owner field authoritative rather
than typed by hand. Pairs well with either of the above.

**4. Online activation.** A server you host validates the code and enforces one active session
per key, with instant revocation. Strongest and the only option that catches sharing in real
time — but you have to host and maintain it, it breaks offline, and a game-automation plugin
phoning home is a genuine trust and privacy concern for the people running it. Treat this as a
last resort, not a goal.

A **rotating time-based code** (one shared secret in the DLL, a new code each day, posted to
your alpha channel) is worth naming only to warn against it: the secret sits in the shipped
assembly, so anyone who extracts it generates valid codes forever, and unlike the scheme above
there is no owner name to trace. It is strictly weaker than what is here.

## Do not commit

- `keys/` — the private signing key. Already in `.gitignore`; check before every push.
- Any minted code. They are bearer tokens. Do not paste them into issues, commits, or logs.
