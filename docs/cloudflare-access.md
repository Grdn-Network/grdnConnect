# Locking the host tunnels with Cloudflare Access

The mod's HTTP listener only accepts local connections now. The one way in from
outside is the Cloudflare tunnel at `<host>-connect.<domain>`. Those hostnames
are public and easy to guess, so the tunnel needs a lock on it.

This gives the bot a service token and shuts the door on everyone else. It works
on the free Zero Trust plan and doesn't change the tunnels themselves.

## Order matters

If you turn Access on before the bot can send the token, you lock the bot out
too and Start Session stops reaching hosts. So make the token first, give it to
the bot, restart the bot, and turn Access on last.

## 1. Make the service token

Go to the Cloudflare dashboard and open Zero Trust in the left sidebar.

1. Open Access, then Service Auth.
2. Click Create Service Token.
3. Name it `grdn-bot` and leave the duration alone.
4. Copy the Client ID and Client Secret now. The secret is only shown once. If
   you lose it, delete the token and make a new one.

## 2. Give the token to the bot

On the VPS, add these to the bot's `.env`:

```
CF_ACCESS_CLIENT_ID=<the Client ID>
CF_ACCESS_CLIENT_SECRET=<the Client Secret>
```

Restart the bot. It sends them as `CF-Access-Client-Id` and
`CF-Access-Client-Secret` on every request to a `-connect` host, and skips them
when the variables aren't set.

Don't commit `.env`. Anyone with that secret can reach every host.

## 3. Make the Access application

1. Open Access, then Applications, then Add an application, then Self-hosted.
2. Name it `GRDN Connect hosts`.
3. Under Application domain, add one entry per host tunnel. For `red` that's
   subdomain `red-connect` and your domain. Do the same for every name in
   `host-tunnels.json`.
4. Try a wildcard subdomain of `*-connect` first. If your plan won't take it,
   list them one at a time and add a line whenever a host joins.
5. Carry on to the policy step.

## 4. Make the policy service token only

1. Name the policy `bot service token only`.
2. Set Action to Service Auth. Don't use Allow, because that asks people to log
   in with email instead of requiring the token.
3. Under Configure rules, set Include to Service Token and pick `grdn-bot`.
4. Save and finish creating the application.

Leave that as the only policy. A second policy set to Allow opens the door again.

## 5. Check it works

Open `https://<host>-connect.<domain>/ping` in a private browser window. You
should get a Cloudflare block or login page. If you see `{"ok":true}` instead,
Access isn't covering that hostname, so check the domain list in step 3. The game
has to be running for that route to answer at all.

Then try it with the token from the VPS:

```powershell
curl.exe -s -H "CF-Access-Client-Id: $env:CF_ACCESS_CLIENT_ID" -H "CF-Access-Client-Secret: $env:CF_ACCESS_CLIENT_SECRET" https://<host>-connect.<domain>/ping
```

That should return `{"ok":true}`. If you get HTML back, the headers aren't
reaching Cloudflare or the token's wrong.

Last, run `/session start` in Discord. The host's game log should say
`Session config received`. If it doesn't, check the bot console for a 403 and
make sure step 2 actually took effect.

## If you lock yourself out

Delete the Access application. The tunnels keep working and you're back where you
started with the hostnames open. Fix the policy and add it again.

## What this doesn't cover

Access guards the tunnel, not the machine. Anything already running on the host
PC can still reach the listener on localhost. That's fine, because that's the
host themselves.
