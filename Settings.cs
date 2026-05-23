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
    // The bot matches players to Discord automatically via their in-game train
    // number — no per-player Discord ID needed. Set BotPushUrl/BotSecret once.

    [Draw("Bot Push URL — auto-set by /session start; only needed here as a manual fallback")]
    public string BotPushUrl = "";

    [Draw("Bot Secret — auto-set by /session start; only needed here as a manual fallback")]
    public string BotSecret = "";

    [Draw("Realistic Radio — require being in a loco to switch Discord voice channels (default: off)")]
    public bool RealisticRadio = false;

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
        // Applies live — no game restart required.
        // Port: restarts HTTP server on new port (only if valid and actually changed).
        // RadioChannelsJson: pushes updated channel list into the live radio integration.
        GRDNConnectBehaviour.ApplySettingsChange();
    }
}
