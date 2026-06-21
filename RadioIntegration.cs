// RadioIntegration.cs
// Soft dependency on CommsRadioAPI (https://github.com/fauxnik/dv-comms-radio-api).
// Compiled only when CommsRadioAPI.dll is present in lib/ (COMMS_RADIO_API defined).
// Without it, TryInit() is a no-op stub and the mod builds/runs without radio support.
#if COMMS_RADIO_API

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CommsRadioAPI;
using DV;
using UnityEngine;
using UnityEngine.Networking;

// ── Bootstrap ─────────────────────────────────────────────────────────────────

/// <summary>
/// Call TryInit() from GRDNConnectBehaviour.Start() after settings are loaded.
/// </summary>
public static class RadioIntegration
{
    private static MonoBehaviour _host;

    // ── Dynamic channel list — pushed by the bot on /session start + every 90s ─
    private static List<(string name, string vcId)> _sessionChannels
        = new List<(string, string)>();

    /// <summary>
    /// Live channel list for GRDN RADIO.
    /// Prefers channels pushed via /session-config; falls back to UMM settings.
    /// </summary>
    internal static List<(string name, string vcId)> ActiveChannels =>
        _sessionChannels.Count > 0
            ? _sessionChannels
            : ParseChannels(Main.Settings.RadioChannelsJson);

    /// <summary>
    /// Parses the "channels" array from a /session-config JSON body and stores it.
    /// Called by GRDNConnectBehaviour.HandleSessionConfig on every push.
    /// </summary>
    internal static void UpdateChannelsFromJson(string sessionConfigJson)
    {
        string arr = ExtractJsonArray(sessionConfigJson, "channels");
        if (arr == null) return;
        var channels = ParseChannels(arr);
        _sessionChannels = channels;
        Main.ModEntry.Logger.Log($"[GRDNConnect] Radio channels updated: {channels.Count} channel(s).");
    }

    private static string ExtractJsonArray(string json, string key)
    {
        int ki = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (ki < 0) return null;
        int ci = json.IndexOf('[', ki);
        if (ci < 0) return null;
        int depth = 0;
        for (int i = ci; i < json.Length; i++)
        {
            if      (json[i] == '[') depth++;
            else if (json[i] == ']') { if (--depth == 0) return json.Substring(ci, i - ci + 1); }
        }
        return null;
    }

    public static void TryInit(MonoBehaviour coroutineHost)
    {
        _host = coroutineHost;

        // 1. Is CommsRadioAPI installed and active at runtime?
        // UMM mod ID is "CommsRadioAPI" (from its Info.json) — not "dv-comms-radio-api".
        var apiMod = UnityModManagerNet.UnityModManager.FindMod("CommsRadioAPI");
        if (apiMod == null || !apiMod.Active)
        {
            Main.ModEntry.Logger.Log("[GRDNConnect] CommsRadioAPI not installed — radio integration disabled.");
            return;
        }

        // 2. Seed initial channel list (bot will push a live update on /session start)
        var channels = ActiveChannels;

        // 3. Register modes when the CommsRadio controller is ready.
        //    Each mode has its own try/catch so a failure in one doesn't
        //    prevent the other from registering.
        ControllerAPI.Ready += () =>
        {
            // GRDN Radio — always registered. Idle until player presses BROWSE.
            // ActiveChannels is read live on each open so updates from the bot
            // or from the polling coroutine appear without a game restart.
            try
            {
                CommsRadioMode.Create(new GRDNRadioState(channels, OnChannelSelected), Color.white, null);
                Main.ModEntry.Logger.Log($"[GRDNConnect] GRDN RADIO registered — {channels.Count} channel(s) at load time.");
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Error("[GRDNConnect] GRDN RADIO init error: " + ex.Message);
            }

            // GRDN Crew — in-game loco assignment (independent of radio channels)
            try
            {
                CommsRadioMode.Create(new GRDNCrewState(_host), Color.yellow, null);
                Main.ModEntry.Logger.Log("[GRDNConnect] GRDN CREW registered.");
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Error("[GRDNConnect] GRDN CREW init error: " + ex.Message);
            }
        };
    }

    // ── Push callback ─────────────────────────────────────────────────────────

    // ── Radio push result — checked by GRDNRadioState.OnUpdate while in Sent state ─
    // 0 = pending, 1 = success, 2 = failure
    internal static int RadioResult = 0;

    // Specific outcome message for the LCD (e.g. "NO LINK", "Join a VC"), derived
    // from the bot's /radio-change "status" field. Empty → fall back to generic text.
    internal static string RadioResultMessage = "";

    internal static void OnChannelSelected(string vcId)
    {
        if (_host != null)
            _host.StartCoroutine(PushRadioChange(vcId));
    }

    private static IEnumerator PushRadioChange(string vcId)
    {
        string pushUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/') ?? "";
        string secret  = GRDNConnectBehaviour.ActiveBotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] Radio push skipped — BotPushUrl not set.");
            yield break;
        }

        // RealisticRadio: require player to be in a loco before switching channels.
        // Default off — anyone can switch their Discord VC from anywhere.
        string trainNumber = GetPlayerTrainNumber();
        if (Main.Settings.RealisticRadio && string.IsNullOrEmpty(trainNumber))
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] Radio push skipped — not in a loco (RealisticRadio on).");
            yield break;
        }

        // Always include Steam ID so the bot can resolve the player without a
        // train-number lookup (works even for clients not assigned a train yet).
        var (steamId, _) = GetLocalSteamInfo();

        // Need at least one identifier to route the push
        if (steamId == 0 && string.IsNullOrEmpty(trainNumber))
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] Radio push skipped — no Steam ID and no train number.");
            yield break;
        }

        var fields = new System.Text.StringBuilder();
        fields.Append($"\"vcId\":\"{Esc(vcId)}\"");
        if (steamId > 0)
            fields.Append($",\"steamId\":\"{steamId}\"");
        if (!string.IsNullOrEmpty(trainNumber))
            fields.Append($",\"trainNumber\":\"{Esc(trainNumber)}\"");

        string url  = pushUrl + "/radio-change";
        string body = "{" + fields.ToString() + "}";
        byte[] raw  = Encoding.UTF8.GetBytes(body);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(secret))
                req.SetRequestHeader("x-secret", secret);

            yield return req.SendWebRequest();

            if (req.error == null)
            {
                // Bot replies 200 with a "status" telling us what actually happened
                // (moved / not_linked / not_in_voice / ...). A 200 alone does NOT mean
                // someone was moved — read the status. An older bot that doesn't send a
                // status is treated as success (HTTP 200 = request accepted) so the two
                // can be deployed in any order without a false "Switch failed".
                string status = ExtractStatus(req.downloadHandler.text);
                if (string.IsNullOrEmpty(status))
                {
                    RadioResult = 1;
                    RadioResultMessage = "Switched!";
                    Main.LogVerbose($"[GRDNConnect] Radio push OK (legacy bot, no status) → vcId={vcId}");
                }
                else
                {
                    bool moved = status == "moved" || status == "already_there";
                    RadioResultMessage = MapStatusMessage(status);
                    RadioResult = moved ? 1 : 2;
                    if (moved)
                        Main.LogVerbose($"[GRDNConnect] Radio push OK ({status}) → vcId={vcId}");
                    else
                        Main.ModEntry.Logger.Log($"[GRDNConnect] Radio push: bot reported '{status}' for vcId={vcId}");
                }
            }
            else
            {
                RadioResult = 2;
                RadioResultMessage = req.responseCode == 0 ? "Bot offline" : "Switch error";
                Main.ModEntry.Logger.Warning($"[GRDNConnect] Radio push failed ({req.responseCode}): {req.error}");
            }
        }
    }

    // ── Player loco detection ─────────────────────────────────────────────────

    internal static string GetPlayerTrainNumber()
    {
        try
        {
            var car = PlayerManager.Car;
            if (car == null) return null;

            TrainCar loco = null;
            if (car.IsLoco)
            {
                loco = car;
            }
            else if (car.trainset?.cars != null)
            {
                foreach (var c in car.trainset.cars)
                    if (c != null && c.IsLoco) { loco = c; break; }
            }

            if (loco == null) return null;

            var match = System.Text.RegularExpressions.Regex.Match(loco.ID ?? "", @"(\d+)$");
            return match.Success ? match.Groups[1].Value : loco.ID;
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] GetPlayerTrainNumber: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the GRDN loco type code for the player's current loco (e.g. "DE6", "S282").
    /// Returns null if the player is not in a loco or the type is unrecognised.
    /// </summary>
    internal static string GetPlayerLocoTypeCode()
    {
        try
        {
            var car = PlayerManager.Car;
            if (car == null) return null;

            TrainCar loco = null;
            if (car.IsLoco)
                loco = car;
            else if (car.trainset?.cars != null)
                foreach (var c in car.trainset.cars)
                    if (c != null && c.IsLoco) { loco = c; break; }

            if (loco == null) return null;
            return MapCarType(((object)loco.carType).ToString());
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] GetPlayerLocoTypeCode: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps a DV TrainCarType enum name to the GRDN loco type code used in /setcrew.
    /// Confirmed: LocoDiesel=DE6, LocoSteamHeavy=S282, LocoMicroshunter=BE2.
    /// Others are best-guess based on DV naming conventions — verify in-game if needed.
    /// </summary>
    internal static string MapCarType(string carType)
    {
        switch (carType)
        {
            // ── Confirmed ────────────────────────────────────────────────────
            case "LocoDiesel":        return "DE6";
            case "LocoSteamHeavy":    return "S282";
            case "LocoMicroshunter":  return "BE2";
            // ── Best-guess (verify against actual TrainCarType enum) ─────────
            case "LocoShunter":       return "DE2";
            case "LocoDH4":           return "DH4";
            case "LocoDM3":           return "DM3";
            case "LocoS060":          return "S060";
            case "HandCar":           return "Handcar";
            default:                  return null;  // unknown — locoType omitted from push
        }
    }

    // ── JSON channel parser ───────────────────────────────────────────────────

    internal static List<(string name, string vcId)> ParseChannels(string json)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            int pos = 0;
            while (pos < json.Length)
            {
                int ni = IndexOf(json, "\"name\"",  pos);
                int vi = IndexOf(json, "\"vcId\"",  pos);
                if (ni < 0 || vi < 0) break;

                string name = ExtractString(json, ni + 6);
                string vcId = ExtractString(json, vi + 6);

                if (name != null && vcId != null)
                    result.Add((name, vcId));

                pos = Math.Max(ni, vi) + 1;
            }
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] ParseChannels error: " + ex.Message);
        }
        return result;
    }

    private static int IndexOf(string src, string value, int start) =>
        src.IndexOf(value, start, StringComparison.Ordinal);

    private static string ExtractString(string json, int afterKey)
    {
        int c = json.IndexOf(':',  afterKey); if (c < 0) return null;
        int o = json.IndexOf('"', c);         if (o < 0) return null;
        int e = json.IndexOf('"', o + 1);     if (e < 0) return null;
        return json.Substring(o + 1, e - o - 1);
    }

    // Pulls the "status" field out of the bot's /radio-change reply.
    private static string ExtractStatus(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        int i = json.IndexOf("\"status\"", StringComparison.Ordinal);
        return i < 0 ? "" : (ExtractString(json, i + 8) ?? "");
    }

    // Maps a bot status code to a short LCD message. Keep in sync with the bot's
    // /radio-change handler and the message reference in the docs.
    private static string MapStatusMessage(string status)
    {
        switch (status)
        {
            case "moved":         return "Switched!";
            case "already_there": return "Already set";
            case "not_linked":    return "NO LINK";
            case "not_in_voice":  return "Join a VC";
            case "no_member":     return "Not in guild";
            case "no_match":      return "No crew match";
            default:              return "Switch failed";
        }
    }

    internal static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

    // ── Channel polling — self-serve for clients joining after session start ──
    // Clients never receive the bot's /session-config push (that goes to the host).
    // This coroutine GETs /radio-channels from the bot URL every 60 seconds so any
    // client with BotPushUrl set always has a live channel list in GRDN RADIO.
    internal static void StartChannelPolling(MonoBehaviour host)
    {
        host.StartCoroutine(PollChannelsLoop());
    }

    private static IEnumerator PollChannelsLoop()
    {
        // Short delay on startup — let the game settle and session config arrive first
        yield return new WaitForSeconds(15f);

        while (true)
        {
            string url = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
            if (!string.IsNullOrEmpty(url))
            {
                using (var req = new UnityWebRequest(url + "/radio-channels", "GET"))
                {
                    req.downloadHandler = new DownloadHandlerBuffer();
                    string secret = GRDNConnectBehaviour.ActiveBotSecret;
                    if (!string.IsNullOrEmpty(secret))
                        req.SetRequestHeader("x-secret", secret);

                    yield return req.SendWebRequest();

                    if (req.error == null)
                    {
                        string json = req.downloadHandler.text;
                        if (!string.IsNullOrEmpty(json))
                        {
                            int before = _sessionChannels.Count;
                            UpdateChannelsFromJson(json);
                            if (_sessionChannels.Count != before)
                                Main.ModEntry.Logger.Log(
                                    $"[GRDNConnect] Channel poll: {_sessionChannels.Count} channel(s) (was {before})");
                        }
                    }
                }
            }
            yield return new WaitForSeconds(60f);
        }
    }

    // ── Steam identity ────────────────────────────────────────────────────────
    // Returns the local player's Steam ID64 and display name.
    // Uses reflection so no compile-time reference to Facepunch.Steamworks is
    // needed — the DLL is always loaded by Derail Valley itself at runtime.
    // Returns (0, "") gracefully if Steam is not initialised.
    //
    // KEY: In Facepunch.Steamworks, SteamId.Value is a PUBLIC FIELD, not a
    // property. GetProperty("Value") silently returns null. We try GetField first.
    internal static (ulong id, string name) GetLocalSteamInfo()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name.IndexOf("Steamworks", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var clientType = asm.GetType("Steamworks.SteamClient");
                if (clientType == null) continue;

                // IsValid — only bail if it's explicitly false.
                // If the property doesn't exist in this version, continue anyway.
                var isValidProp = clientType.GetProperty("IsValid",
                    BindingFlags.Public | BindingFlags.Static);
                if (isValidProp != null && !(bool)isValidProp.GetValue(null))
                    return (0, "");

                // SteamId — try both common property names used across versions
                ulong id = 0;
                foreach (var idPropName in new[] { "SteamId", "SteamID" })
                {
                    var steamIdProp = clientType.GetProperty(idPropName,
                        BindingFlags.Public | BindingFlags.Static);
                    if (steamIdProp == null) continue;

                    var steamIdObj = steamIdProp.GetValue(null);
                    if (steamIdObj == null) continue;

                    var t = steamIdObj.GetType();

                    // Facepunch.Steamworks declares SteamId.Value as a public field,
                    // not a property — GetProperty("Value") will silently return null.
                    // Try field first, then property as a fallback for other bindings.
                    var valueField = t.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (valueField != null)
                        id = (ulong)valueField.GetValue(steamIdObj);

                    if (id == 0)
                    {
                        var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (valueProp != null)
                            id = (ulong)valueProp.GetValue(steamIdObj);
                    }

                    if (id > 0) break;
                }

                // Name
                var nameProp = clientType.GetProperty("Name",
                    BindingFlags.Public | BindingFlags.Static);
                string name = nameProp?.GetValue(null) as string ?? "";

                if (id == 0)
                    Main.ModEntry.Logger.Warning(
                        "[GRDNConnect] GetLocalSteamInfo: Steamworks found but Steam ID is 0 — " +
                        "Steam may not be fully initialised yet. Radio push will require a train number.");

                return (id, name);
            }
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] GetLocalSteamInfo: " + ex.Message);
        }

        Main.ModEntry.Logger.Warning("[GRDNConnect] GetLocalSteamInfo: no Steamworks assembly found in AppDomain.");
        return (0, "");
    }
}

// ── GRDN Radio state ──────────────────────────────────────────────────────────
//
// TWO-STAGE DESIGN
// ─────────────────
// IDLE    (default) — shown when scrolling through radio modes.
//         LCD is blank. Up/Down return a new idle state (no channel scroll),
//         so the player can navigate past GRDN RADIO to other modes normally.
//         Button "BROWSE" (Override) → enters Browse on Activate.
//
// BROWSE  — entered by pressing BROWSE. Shows live channel list with Up/Down
//         arrows. Up/Down scrolls channels. Activate sends the push → Sent.
//
// SENT    — LCD shows "Switching..." (Regular). Any button press → Idle.
//
// WHY: if Up/Down scrolled channels in Idle, pressing Up/Down while passing
// through GRDN RADIO in the mode list would scroll channels instead of
// advancing to the next mode, locking the player.

public class GRDNRadioState : AStateBehaviour
{
    private readonly List<(string name, string vcId)> _channels;
    private readonly Action<string> _onSelected;
    private readonly int  _index;
    private readonly bool _sent;
    private readonly bool _browsing;  // true = channel-select stage

    public GRDNRadioState(
        List<(string name, string vcId)> channels,
        Action<string> onSelected,
        int  index    = 0,
        bool sent     = false,
        bool browsing = false)
        : base(new CommsRadioState(
            "GRDN RADIO",
            MakeContent(RadioIntegration.ActiveChannels, index, sent, browsing),
            GetLabel(sent, browsing, RadioIntegration.ActiveChannels.Count),
            browsing ? LCDArrowState.Right : LCDArrowState.Off,
            LEDState.Off,
            (browsing && !sent) ? ButtonBehaviourType.Override : ButtonBehaviourType.Regular))
    {
        _channels   = RadioIntegration.ActiveChannels;
        _onSelected = onSelected;
        _index      = index;
        _sent       = sent;
        _browsing   = browsing;
    }

    private static string GetLabel(bool sent, bool browsing, int count)
    {
        if (sent)      return "";
        if (!browsing) return count > 0 ? "BROWSE" : "";
        return "SWITCH";
    }

    private static string MakeContent(
        List<(string name, string vcId)> channels, int idx, bool sent, bool browsing)
    {
        if (sent)
        {
            // Result not yet known — show pending; OnUpdate transitions once it arrives.
            // Prefer the specific message from the bot ("NO LINK", etc.) when present.
            int r = RadioIntegration.RadioResult;
            if (r != 0)
                return string.IsNullOrEmpty(RadioIntegration.RadioResultMessage)
                    ? (r == 1 ? "Switched!" : "Switch failed!")
                    : RadioIntegration.RadioResultMessage;
            return "Switching...";
        }
        if (!browsing)  return channels.Count > 0 ? $"{channels.Count} ch. ready" : "No channels";
        if (channels.Count == 0) return "No channels";
        return $"{idx + 1}/{channels.Count}  {channels[idx].name}";
    }

    // ── Real-time refresh ─────────────────────────────────────────────────────
    public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
    {
        // While sent, poll RadioResult until a result arrives, then re-enter sent
        // state so MakeContent picks up the new content string.
        if (_sent)
        {
            int r = RadioIntegration.RadioResult;
            if (r != 0)
                return new GRDNRadioState(_channels, _onSelected, _index, sent: true, browsing: false);
            return this;
        }

        if (_browsing) return this;

        var live = RadioIntegration.ActiveChannels;
        if (live.Count == _channels.Count) return this;
        return new GRDNRadioState(live, _onSelected, 0);
    }

    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        var ch = RadioIntegration.ActiveChannels;

        switch (action)
        {
            case InputAction.Up:
            case InputAction.Down:
                if (_sent || !_browsing)
                    return new GRDNRadioState(ch, _onSelected, _index, false, false);

                if (ch.Count == 0)
                    return new GRDNRadioState(ch, _onSelected, 0, false, true);
                int next = action == InputAction.Up
                    ? (_index - 1 + ch.Count) % ch.Count
                    : (_index + 1) % ch.Count;
                return new GRDNRadioState(ch, _onSelected, next, false, true);

            case InputAction.Activate:
                // Sent → back to idle (clears result display)
                if (_sent)
                    return new GRDNRadioState(ch, _onSelected, _index, false, false);

                if (!_browsing)
                    return new GRDNRadioState(ch, _onSelected, _index, false, true);

                if (ch.Count == 0)
                    return new GRDNRadioState(ch, _onSelected, 0, false, true);

                int idx = Math.Min(_index, ch.Count - 1);
                RadioIntegration.RadioResult = 0;          // clear before firing
                RadioIntegration.RadioResultMessage = "";
                _onSelected?.Invoke(ch[idx].vcId);
                return new GRDNRadioState(ch, _onSelected, idx, sent: true, browsing: false);

            default:
                return new GRDNRadioState(ch, _onSelected, _index, _sent, _browsing);
        }
    }
}

#else
// ── Stub — compiled when CommsRadioAPI.dll is absent from lib/ ────────────────
public static class RadioIntegration
{
    public static void TryInit(UnityEngine.MonoBehaviour host) { }
    internal static void UpdateChannelsFromJson(string json) { }
    internal static string GetPlayerTrainNumber() => null;
    internal static string Esc(string s) => s ?? "";
    internal static (ulong id, string name) GetLocalSteamInfo() => (0, "");
    internal static void StartChannelPolling(UnityEngine.MonoBehaviour host) { }
}
#endif
