using UnityModManagerNet;

public class Settings : UnityModManager.ModSettings, IDrawable
{
    [Draw("Network Port (1024-65535)")]
    public int Port = 7230;

    [Draw("Live Train Board — pull job/track data from game (disable for hardcore/manual ops)")]
    public bool LiveTrainBoardEnabled = true;

    [Draw("Relaxed Job Completion — /complete works at destination station, not just the exact track")]
    public bool RelaxedJobCompletion = false;

    // ── Radio / Discord VC integration ────────────────────────────────────────
    // Requires CommsRadioAPI mod to be installed for the radio UI to appear.
    // Fill these in via the UMM settings panel (Mods → GRDNConnect).

    [Draw("Your Discord User ID (right-click your name → Copy User ID)")]
    public string DiscordUserId = "";

    [Draw("Bot Push URL (e.g. http://your-vps:3000 or https://your.cloudflare.tunnel)")]
    public string BotPushUrl = "";

    [Draw("Bot Secret (must match HTTP_SECRET in the bot's .env)")]
    public string BotSecret = "";

    // JSON array of radio channels — maps channel name to Discord VC ID.
    // Format: [{"name":"Main Line","vcId":"123456789"},{"name":"Harbor","vcId":"987654321"}]
    // Copy the VC ID by right-clicking the voice channel → Copy Channel ID in Discord.
    [Draw("Radio Channels (JSON — see mod description for format)")]
    public string RadioChannelsJson = "[]";

    public override void Save(UnityModManager.ModEntry modEntry)
    {
        UnityModManager.ModSettings.Save<Settings>(this, modEntry);
    }

    public void OnChange()
    {
    }
}
