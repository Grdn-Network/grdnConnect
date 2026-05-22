// RadioIntegration.cs
// Soft dependency on CommsRadioAPI (https://github.com/fauxnik/dv-comms-radio-api).
//
// HOW THE SOFT DEP WORKS
// ──────────────────────
// • GRDNRadioMode extends AStateBehaviour, which is in Assembly-CSharp.dll
//   (already referenced) — no extra compile-time dep needed.
// • Registration with CommsRadioAPI is done via reflection at runtime.
//   If the mod is not installed the init silently bails.
// • Call RadioIntegration.TryInit() from GRDNConnectBehaviour.Start().
//
// CHANNEL FORMAT (Settings.RadioChannelsJson)
// ────────────────────────────────────────────
//   [{"name":"Main Line","vcId":"123456789"},{"name":"Harbor","vcId":"987654321"}]
//
// ON CHANNEL CHANGE → POST {BotPushUrl}/radio-change
//   Header: x-secret: {BotSecret}
//   Body:   { "trainNumber": "DE2-034", "vcId": "..." }
//
// The bot looks up the Discord user from the train number using its own crew
// registrations — players don't need to configure a Discord ID here.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// ── Bootstrap ─────────────────────────────────────────────────────────────────

/// <summary>
/// Call TryInit() from GRDNConnectBehaviour.Start() after settings are loaded.
/// </summary>
public static class RadioIntegration
{
    private static MonoBehaviour _host;
    private static Assembly      _apiAssembly;

    public static void TryInit(MonoBehaviour coroutineHost)
    {
        _host = coroutineHost;

        // 1. Is CommsRadioAPI installed and active?
        var apiMod = UnityModManagerNet.UnityModManager.FindMod("dv-comms-radio-api");
        if (apiMod == null || !apiMod.Active)
        {
            Main.ModEntry.Logger.Log("[GRDNConnect] CommsRadioAPI not present — radio integration disabled.");
            return;
        }

        // 2. Parse channels from settings
        var channels = ParseChannels(Main.Settings.RadioChannelsJson);
        if (channels.Count == 0)
        {
            Main.ModEntry.Logger.Log("[GRDNConnect] No radio channels configured — radio integration disabled.");
            return;
        }

        _apiAssembly = apiMod.Assembly;

        // 3. Register our modes with CommsRadioAPI via reflection.
        //    CommsRadioAPI exposes ControllerAPI.AddMode(AStateBehaviour).
        //    We discover the exact type/method name at runtime.
        try
        {
            // GRDN Radio — cycle through VC channels
            var radioMode = new GRDNRadioMode(channels, OnChannelSelected);
            if (!RegisterMode(_apiAssembly, radioMode))
            {
                Main.ModEntry.Logger.Warning("[GRDNConnect] Could not find CommsRadioAPI registration method.");
                return;
            }
            Main.ModEntry.Logger.Log($"[GRDNConnect] Radio mode registered — {channels.Count} channel(s).");

            // GRDN Crew — in-game loco assignment / mid-op re-assign
            var crewMode = new GRDNCrewMode(_host);
            RegisterMode(_apiAssembly, crewMode); // best-effort; log inside RegisterMode if it fails
            Main.ModEntry.Logger.Log("[GRDNConnect] Crew mode registered.");
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Error("[GRDNConnect] Radio init error: " + ex.Message);
        }
    }

    // ── Registration via reflection ───────────────────────────────────────────

    private static bool RegisterMode(Assembly apiAssembly, AStateBehaviour mode)
    {
        // Try the most likely class/method name combinations from CommsRadioAPI.
        // If fauxnik changes the API shape, update the strings below.
        string[] classNames  = { "CommsRadioAPI.ControllerAPI", "ControllerAPI" };
        string[] methodNames = { "AddMode", "RegisterMode", "AddRadioMode" };

        foreach (var cls in classNames)
        {
            var type = apiAssembly.GetType(cls);
            if (type == null) continue;

            foreach (var mth in methodNames)
            {
                var method = type.GetMethod(mth,
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(AStateBehaviour) },
                    null);

                if (method == null) continue;

                method.Invoke(null, new object[] { mode });
                Main.ModEntry.Logger.Log($"[GRDNConnect] Registered via {cls}.{mth}()");
                return true;
            }
        }

        // Dump available types so the user can report the correct name
        Main.ModEntry.Logger.Warning("[GRDNConnect] CommsRadioAPI registration not found. Available types:");
        foreach (var t in apiAssembly.GetExportedTypes())
            Main.ModEntry.Logger.Warning("  " + t.FullName);

        return false;
    }

    // ── Push callback (runs on the coroutine host MonoBehaviour) ─────────────

    internal static void OnChannelSelected(string vcId)
    {
        if (_host != null)
            _host.StartCoroutine(PushRadioChange(vcId));
    }

    private static IEnumerator PushRadioChange(string vcId)
    {
        string pushUrl = (Main.Settings.BotPushUrl?.TrimEnd('/') ?? "");
        string secret  = Main.Settings.BotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[GRDNConnect] Radio push skipped — BotPushUrl not set.");
            yield break;
        }

        // Identify the player by their current loco ID (e.g. "DE2-034").
        // The bot matches this to a Discord user via its /setcrew registrations.
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

    /// <summary>
    /// Returns the NUMERIC portion of the local player's current loco ID.
    /// e.g. "DE2-034" → "034"  |  "DH4-201" → "201"
    /// This matches the train number format players enter in /setcrew on Discord.
    /// Returns null if the player is not inside a locomotive.
    /// </summary>
    internal static string GetPlayerTrainNumber()
    {
        try
        {
            var car = PlayerManager.Car;
            if (car == null) return null;

            // Walk up to the loco if the player is in a coupled wagon
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

            // Extract trailing digits: "DE2-034" → "034", "DH4-201" → "201"
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
            // Walks through the JSON array extracting {"name":"...","vcId":"..."} pairs.
            // Avoids any runtime JSON library dependency.
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

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}

// ── Radio mode ─────────────────────────────────────────────────────────────────
//
// Extends DV's AStateBehaviour (Assembly-CSharp.dll — already referenced).
// CommsRadioAPI plugs custom AStateBehaviour instances into the in-game radio.
//
// CONTROLS
//   Button A (left/◄)  — previous channel
//   Button B (right/►) — next channel
//   Tuning to a channel immediately pushes to Discord bot.
//
// DISPLAY
//   Header : GRDN RADIO
//   Body   : "1/3  Main Line"   (index / total  name)
// ─────────────────────────────────────────────────────────────────────────────

public class GRDNRadioMode : AStateBehaviour
{
    private readonly List<(string name, string vcId)> _channels;
    private readonly Action<string>                   _onSelected;
    private int _index;

    public GRDNRadioMode(List<(string name, string vcId)> channels, Action<string> onSelected)
    {
        _channels   = channels;
        _onSelected = onSelected;
        _index      = 0;
    }

    // ── AStateBehaviour lifecycle ─────────────────────────────────────────────

    public override void Enable()
    {
        UpdateDisplay();
    }

    public override void Disable() { }

    // ── Button behaviour ──────────────────────────────────────────────────────
    //
    // ButtonBehaviourType.Override  — this mode handles the button
    // ButtonBehaviourType.Ignore    — button is disabled / not used

    public override ButtonBehaviourType ButtonABehaviour =>
        _channels.Count > 1 ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore;

    public override ButtonBehaviourType ButtonBBehaviour =>
        _channels.Count > 1 ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore;

    // Button A (left / ◄) — previous channel
    public override void ButtonACustomAction()
    {
        if (_channels.Count == 0) return;
        _index = (_index - 1 + _channels.Count) % _channels.Count;
        UpdateDisplay();
        _onSelected?.Invoke(_channels[_index].vcId);
    }

    // Button B (right / ►) — next channel
    public override void ButtonBCustomAction()
    {
        if (_channels.Count == 0) return;
        _index = (_index + 1) % _channels.Count;
        UpdateDisplay();
        _onSelected?.Invoke(_channels[_index].vcId);
    }

    // ── LCD display ───────────────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        if (display == null || _channels.Count == 0) return;
        var ch   = _channels[_index];
        var body = $"{_index + 1}/{_channels.Count}  {ch.name}";
        // SetContent signature: (string header, string content, Color color)
        // Adjust if CommsRadioAPI/DV uses a different overload.
        display.SetContent("GRDN RADIO", body, Color.white);
    }
}
