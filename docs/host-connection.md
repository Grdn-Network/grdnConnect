# How the mod and the bot talk

Short version. The bot reaches the host through a Cloudflare tunnel. The host
passes what it gets to clients over Derail Valley multiplayer. Clients never talk
to the bot or to the host's HTTP listener.

## The host

The mod runs a small HTTP listener on port 7230. It only accepts local
connections, so nothing on the network can reach it. `cloudflared` runs on the
same PC and dials it locally, which makes the tunnel the only way in from
outside. Put Cloudflare Access in front of the tunnel so only the bot gets
through. See [cloudflare-access.md](cloudflare-access.md).

When you run `/session start`, the bot posts the session config to
`https://<host>-connect.<domain>/session-config`. That's the bot URL, two tokens
minted for this session, and the crew voice channels.

The host token stays on the host and is the only credential in play. Clients are
given no token and no bot URL: when a client needs the bot, it asks the host over
the multiplayer packet channel and the host makes the call. The host only accepts
a short list of routes for that, in `BotCall.cs`.

The bot also sends a weaker `clientSecret`, which current builds ignore. It
exists for any client that still calls the bot directly.

## Clients

Clients get the session config from the host over the multiplayer packet
channel, in `ConnectMpChannel.cs`. A client asks when it connects, and the host
also pushes to everyone the moment `/session start` lands, so joining early
works too. What a client receives is the crew channel list and whether a session
is open. Nothing else: a client never contacts the bot at all.

Both sides exchange a protocol version with that config. dv-mp checks mod Ids at
login but not versions, so a client on a different GRDNConnect build joins
looking fine. On a protocol mismatch the host refuses the config and both logs
say which versions are involved.

This used to be an HTTP fetch from the client to the host's port 7230. That's
gone. It needed the client to work out the host's address and reach that port,
and neither held up. Most players don't forward it, and the address lookup
didn't work either. The packet channel needs neither, since the game already has
a working connection between host and client.

## What a host has to set up

Nothing, if you're on the GRDN bot. Install the mod and the bot fills in the
rest at `/session start`.

If you run your own bot, put its URL and secret in `connect.cfg` next to the
mod. Radio channels go there too. Everything else stays in the UMM settings
screen, because those apply straight away and you change them mid-session.

Run `connect.reload` in the in-game console after editing `connect.cfg`.

## Ports

Only 7230, and only on localhost. Don't forward it. If you did before, undo it.

## What polls what

The bot polls the host every 30 s for `/locos`, which feeds the train board.
Nothing else runs on a timer against a host except a 10-minute config re-push,
which exists to recover a host that restarted mid-session. Channel changes push
on the Discord event instead of being polled for.
