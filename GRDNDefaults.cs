// Public, non-secret build defaults for GRDNConnect.
//
// SECURITY: only public values may live here. This file is committed and is
// shipped inside the distributed DLL, so anyone can read it. Never put a secret,
// token, or shared credential in this file. The bot's shared secret is delivered
// at runtime via the /session-config push (see GRDNConnectBehaviour), never baked in.
internal static class GRDNDefaults
{
	// Public HTTPS front door to the bot (Cloudflare tunnel to the VPS on :3000).
	// Safe to ship: this is a public hostname, not a credential. Hosts running
	// their own bot can override it via the UMM "Bot Push URL" setting.
	internal const string BotUrl = "https://connect.grdnnetwork.com";
}
