// RadioIntegration.cs
// Soft dependency on CommsRadioAPI (https://github.com/fauxnik/dv-comms-radio-api).
// Compiled only when CommsRadioAPI.dll is present in lib/ (COMMS_RADIO_API defined).
// Without it, TryInit() is a no-op stub and the mod builds/runs without radio support.
#if COMMS_RADIO_API

using System;
using System.Collections;
using System.Collections.Generic;
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

        // 3. Register modes when the CommsRadio controller is ready
        ControllerAPI.Ready += () =>
        {
            try
            {
                // GRDN Radio — always registered. Shows "No channels" until the bot
                // pushes session-config; OnAction reads ActiveChannels live so channels
                // appear as soon as /session start runs, without a game restart.
                CommsRadioMode.Create(
                    new GRDNRadioState(channels, OnChannelSelected),
                    Color.white,
                    null
                );
                Main.ModEntry.Logger.Log($"[GRDNConnect] Radio mode registered — {channels.Count} channel(s) at load time.");

                // GRDN Crew — in-game loco assignment (always registered, no channels needed)
                CommsRadioMode.Create(
                    new GRDNCrewState(_host),
                    Color.yellow,
                    null
                );
                Main.ModEntry.Logger.Log("[GRDNConnect] Crew mode registered.");
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Error("[GRDNConnect] Radio init error: " + ex.Message);
            }
        };
    }

    // ── Push callback ─────────────────────────────────────────────────────────

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

        string trainNumber = GetPlayerTrainNumber();
        if (string.IsNullOrEmpty(trainNumber))
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] Radio push skipped — player not in a loco.");
            yield break;
        }

        string url  = pushUrl + "/radio-change";
        string body = $"{{\"trainNumber\":\"{Esc(trainNumber)}\",\"vcId\":\"{Esc(vcId)}\"}}";
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
                Main.ModEntry.Logger.Log($"[GRDNConnect] Radio push OK → vcId={vcId}");
            else
                Main.ModEntry.Logger.Warning($"[GRDNConnect] Radio push failed ({req.responseCode}): {req.error}");
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

    internal static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}

// ── GRDN Radio state ──────────────────────────────────────────────────────────
// One state = one LCD screen. OnAction returns a new state with updated index.

public class GRDNRadioState : AStateBehaviour
{
    private readonly List<(string name, string vcId)> _channels;
    private readonly Action<string> _onSelected;
    private readonly int  _index;
    private readonly bool _sent;   // true while showing "Switching..." confirmation

    public GRDNRadioState(
        List<(string name, string vcId)> channels,
        Action<string> onSelected,
        int  index = 0,
        bool sent  = false)
        : base(new CommsRadioState(
            "GRDN RADIO",
            MakeContent(channels, index, sent),
            // Button label: blank while sent/no channels, otherwise "SWITCH"
            (!sent && channels.Count > 0) ? "SWITCH" : "",
            LCDArrowState.Off,
            LEDState.Off,
            (!sent && channels.Count > 0) ? ButtonBehaviourType.Override : ButtonBehaviourType.Regular))
    {
        _channels   = channels;
        _onSelected = onSelected;
        _index      = index;
        _sent       = sent;
    }

    private static string MakeContent(List<(string name, string vcId)> channels, int idx, bool sent)
    {
        if (sent)              return "Switching...";
        if (channels.Count == 0) return "No channels";
        return $"{idx + 1}/{channels.Count}  {channels[idx].name}";
    }

    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        // Always read the live channel list — picks up updates pushed by the bot mid-session
        var ch = RadioIntegration.ActiveChannels;

        switch (action)
        {
            case InputAction.Up:
                if (ch.Count == 0) return this;
                return new GRDNRadioState(ch, _onSelected, (_index - 1 + ch.Count) % ch.Count);

            case InputAction.Down:
                if (ch.Count == 0) return this;
                return new GRDNRadioState(ch, _onSelected, (_index + 1) % ch.Count);

            case InputAction.Activate:
                if (ch.Count == 0) return this;
                int idx = Math.Min(_index, ch.Count - 1);
                _onSelected?.Invoke(ch[idx].vcId);
                return new GRDNRadioState(ch, _onSelected, idx, sent: true);

            default:
                return this;
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
}
#endif
