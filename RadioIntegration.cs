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

    public static void TryInit(MonoBehaviour coroutineHost)
    {
        _host = coroutineHost;

        // 1. Is CommsRadioAPI installed and active at runtime?
        var apiMod = UnityModManagerNet.UnityModManager.FindMod("dv-comms-radio-api");
        if (apiMod == null || !apiMod.Active)
        {
            Main.ModEntry.Logger.Log("[GRDNConnect] CommsRadioAPI not installed — radio integration disabled.");
            return;
        }

        // 2. Parse channels from settings
        var channels = ParseChannels(Main.Settings.RadioChannelsJson);
        if (channels.Count == 0)
        {
            Main.ModEntry.Logger.Log("[GRDNConnect] No radio channels configured — radio integration disabled.");
            return;
        }

        // 3. Register modes when the CommsRadio controller is ready
        ControllerAPI.Ready += () =>
        {
            try
            {
                // GRDN Radio — cycle Discord voice channels
                CommsRadioMode.Create(
                    new GRDNRadioState(channels, OnChannelSelected),
                    Color.white,
                    null
                );
                Main.ModEntry.Logger.Log($"[GRDNConnect] Radio mode registered — {channels.Count} channel(s).");

                // GRDN Crew — in-game loco assignment
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
        string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/') ?? "";
        string secret  = Main.Settings.BotSecret ?? "";

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

            if (req.result == UnityWebRequest.Result.Success)
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
    private readonly int _index;

    public GRDNRadioState(List<(string name, string vcId)> channels, Action<string> onSelected, int index = 0)
        : base(new CommsRadioState(
            "GRDN RADIO",
            MakeContent(channels, index),
            channels.Count > 1 ? "TUNE" : "",
            LCDArrowState.Off,
            LEDState.Off,
            channels.Count > 1 ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore))
    {
        _channels   = channels;
        _onSelected = onSelected;
        _index      = index;
    }

    private static string MakeContent(List<(string name, string vcId)> channels, int idx)
    {
        if (channels.Count == 0) return "No channels";
        return $"{idx + 1}/{channels.Count}  {channels[idx].name}";
    }

    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        if (_channels.Count == 0) return this;

        int next = _index;
        if      (action == InputAction.Up)   next = (_index - 1 + _channels.Count) % _channels.Count;
        else if (action == InputAction.Down) next = (_index + 1) % _channels.Count;
        else return this;

        _onSelected?.Invoke(_channels[next].vcId);
        return new GRDNRadioState(_channels, _onSelected, next);
    }
}

#else
// ── Stub — compiled when CommsRadioAPI.dll is absent from lib/ ────────────────
public static class RadioIntegration
{
    public static void TryInit(UnityEngine.MonoBehaviour host) { }
    internal static string GetPlayerTrainNumber() => null;
    internal static string Esc(string s) => s ?? "";
}
#endif
