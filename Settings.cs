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
    // number — no per-player Discord ID needed.
    //
    // Bot URL, secret and the radio channel list are NOT here: they live in
    // connect.cfg next to this mod. The bot pushes all three at /session start and
    // they reach clients over the DVMP packet channel, so they are not things a
    // player sets. Only the settings below get changed mid-session, which is why
    // they stayed in the GUI where they apply live.

    [Draw("Realistic Radio — require being in a loco to switch Discord voice channels (default: off)")]
    public bool RealisticRadio = false;

    // ── Operations mode ───────────────────────────────────────────────────────
    // Enable before /session start. The bot reads this via /server-info and
    // activates hub-and-spoke stats: car-miles, leg classification, role labels.
    [Draw("Interchange Mode — track car-miles and role labels for hub-and-spoke operations")]
    public bool InterchangeMode = false;

    // ── Diagnostics / performance ─────────────────────────────────────────────
    // The Defect Detector toggle was removed here: the monitor is shelved and never
    // attached, so the setting did nothing. DefectMonitor.cs is kept intact for a
    // possible revival, and the toggle comes back with it.

    [Draw("Verbose Logging — extra per-job / per-request diagnostic logging. Leave OFF during ops to avoid log spam and main-thread disk I/O. Errors and warnings are always logged.")]
    public bool VerboseLogging = false;

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
