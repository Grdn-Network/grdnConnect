# Locking the host tunnels with Cloudflare Access

The mod's HTTP listener only accepts local connections now. The one way in from
outside is the Cloudflare tunnel at `<host>-connect.<domain>`. Those hostnames
are public and easy to guess, so the tunnel needs a lock on it.

The lock belongs in front of the tunnel rather than inside the mod. Any secret
the mod could check would have to ship inside the mod, where it is readable by
anyone (issue #8). Cloudflare already knows both ends, so it is the right place
to put it.

This gives the bot a service token and shuts the door on everyone else. It works
on the free Zero Trust plan and doesn't change the tunnels themselves.

## Lock this hostname, not the others

Three hostnames, and only one of them is in scope:

| Hostname | Goes to | Access? |
|---|---|---|
| `<name>-connect.<domain>` | host PC, port 7230 | **lock it** |
| `connect.<domain>` | VPS, port 3000 | leave open |
| `grdn.<domain>` | host PC, port 7245 (Remote Dispatch) | leave open |

`connect.<domain>` is the direction the mod calls. The mod ships to hosts, so any
credential compiled into it is public. That is issue #8, and a service token in
the DLL would be the same bug in new clothes. That hostname is protected by the
session token the bot mints (every route on it requires one) plus WAF and rate
limiting, not by Access. `grdn.<domain>` is Remote Dispatch, which people open in
a browser.

So never use `*.<domain>` as the application domain. It would match all three and
take the whole network down. `*-connect` does not match `connect`, because the
wildcard needs something before the hyphen, which is why the naming works.

## Order matters

If you turn Access on before the bot can send the token, you lock the bot out
too and Start Session stops reaching hosts. So make the token first, give it to
the bot, restart the bot, and turn Access on last.

The bot's `cfAccessHeaders()` returns nothing when the two variables are unset,
so it sends no token and gets no warning. That silent no-op is what makes the
order the only safeguard.

## 1. Make the service token

In the Cloudflare dashboard, open Zero Trust, then **Access controls**, then
**Service credentials**, then **Service Tokens**.

1. Click **Create Service Token**.
2. Name it `grdn-bot` and leave the duration alone.
3. Click **Generate token**.
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
when the variables aren't set. All seven bot-to-host call sites already carry
them, so nothing else needs changing.

Don't commit `.env`. Anyone with that secret can reach every host.

## 3. Make the Access application

Open **Access controls**, then **Applications**, then **Add an application**.

1. Choose **Self-hosted and private**.
2. If a sub-picker offers Private destinations / Workers / **Public DNS** /
   Service auth, choose **Public DNS**. The host tunnels are public hostnames.
   "Service auth" there is not the app type. Service Auth is the *policy*
   action, set in step 4.
3. Name it `GRDN Connect hosts`.
4. For the application domain, enter subdomain `*-connect` with your domain.
   Re-read the warning above: not `*`.
5. If the wildcard is refused, list the tunnels one at a time (`red-connect`,
   and so on for every name in `host-tunnels.json`) and add a line whenever a
   host joins.

Cloudflare's own docs describe this as "Add public hostname" with a Domain
dropdown. The dashboard wording drifts; match the step that takes a public
hostname.

## 4. Make the policy service token only

1. Name the policy `bot service token only`.
2. Set Action to **Service Auth**. Not Allow, which prompts for an identity
   provider login instead of requiring the token.
3. Under Configure rules, set Include to selector **Service Token** and pick
   `grdn-bot`.
4. Add no identity selector (Emails, Login Methods, Everyone). Don't use Bypass:
   it enforces nothing and logs nothing.
5. Save and finish creating the application.

Leave that as the only policy. A second policy set to Allow opens the door again.

## 5. Turn on the 401 response

In the application's Additional settings, enable **401 Response for Service Auth
policies**. Without it, a request with no token gets a redirect to a login page,
so the bot would see HTML where it expects JSON. With it, it gets a clean 401.

There is also an account-wide **Strict service token authentication** under
Access settings. Newer accounts have it on by default and cannot turn it off; if
yours is older, turning it on is worth doing. Failed service token requests then
get 401 or 403 instead of a login redirect, and only Service Auth policies can
authorize. Cloudflare documents it under Access service tokens:
https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/

## 6. Check it works

The host has to be **in game** for `/ping` to answer at all. Testing with the
game closed looks exactly like a broken tunnel.

Open `https://<host>-connect.<domain>/ping` in a private browser window. You
should get a Cloudflare block, a 401, or a login page. If you see `{"ok":true}`
instead, Access isn't covering that hostname, so check the domain in step 3.

Then try it with the token. Paste the literal values. The `.env` entries are
loaded by the bot process, not by your shell, so `$env:CF_ACCESS_CLIENT_ID`
would be empty and you'd send blank headers and blame the token:

```powershell
curl.exe -s -H "CF-Access-Client-Id: PASTE_ID" -H "CF-Access-Client-Secret: PASTE_SECRET" https://HOST-connect.grdnnetwork.com/ping
```

That should return `{"ok":true}`. If you get HTML back, the headers aren't
reaching Cloudflare or the token's wrong.

Last, run `/session start` in Discord. The host's game log should say
`Session config received`. If it doesn't, check the bot console for a 403 and
make sure step 2 actually took effect.

## The token expires

Service tokens are created with a duration and stop working at the end of it.
Nothing announces the lapse, so the symptom is Start Session no longer reaching
hosts and bot-to-host calls returning 401. If that happens and nothing else
changed, check the token before anything else.

Renewing is the same as step 1: make a new token, put it in `.env`, restart the
bot, then delete the old one. The Access application and policy stay as they
are, because the policy matches the token by name.

## If you lock yourself out

Delete the Access application. The tunnels keep working and you're back where you
started with the hostnames open. Fix the policy and add it again.

## What this doesn't cover

Access guards the tunnel, not the machine. Anything already running on the host
PC can still reach the listener on localhost. That's fine, because that's the
host themselves.
