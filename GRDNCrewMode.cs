// GRDNCrewMode.cs
// GRDN Crew radio mode — lets a player re-assign themselves to a different
// loco from inside the game, without touching Discord.
// Compiled only when CommsRadioAPI.dll is present in lib/ (COMMS_RADIO_API defined).
//
// HOW IT WORKS
// ─────────────
// When the mode is opened, scans the world for all locomotives and presents
// them as a scrollable list (Up = previous, Down = next).
// Pressing Activate confirms and tells the bot:
//   POST /update-crew { fromTrainNumber: "034", toTrainNumber: "201" }
// The bot looks up whoever is registered to "034" and re-assigns them to "201".
//
// FIRST-TIME REQUIREMENT
// ───────────────────────
// The player must have run /setcrew in Discord at least once to establish
// their initial Discord ↔ train link. After that, this mode handles all
// mid-op loco changes automatically.
//
// API VERSION: CommsRadioAPI v1.0.3 (state-machine pattern)
// Registered by RadioIntegration.TryInit() via ControllerAPI.Ready.
#if COMMS_RADIO_API

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CommsRadioAPI;
using DV;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Immutable state object for the GRDN Crew radio mode.
/// OnAction returns a new instance with updated index or busy flag.
/// </summary>
public class GRDNCrewState : AStateBehaviour
{
    private readonly List<string> _locoIds;
    private readonly int          _index;
    private readonly MonoBehaviour _host;

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>Called by RadioIntegration — scans world locos on first open.</summary>
    public GRDNCrewState(MonoBehaviour host)
        : this(host, ScanLocos(), 0, busy: false) { }

    // ── Internal constructor ──────────────────────────────────────────────────

    private GRDNCrewState(MonoBehaviour host, List<string> locos, int index, bool busy)
        : base(new CommsRadioState(
            "GRDN CREW",
            MakeContent(locos, index, busy),
            busy ? "" : (locos.Count > 0 ? "ASSIGN" : ""),
            LCDArrowState.Off,
            LEDState.Off,
            busy || locos.Count == 0 ? ButtonBehaviourType.Ignore : ButtonBehaviourType.Override))
    {
        _host    = host;
        _locoIds = locos;
        _index   = index;
    }

    // ── State machine ─────────────────────────────────────────────────────────

    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        if (_locoIds.Count == 0) return this;

        switch (action)
        {
            case InputAction.Up:
                return new GRDNCrewState(_host, _locoIds,
                    (_index - 1 + _locoIds.Count) % _locoIds.Count, false);

            case InputAction.Down:
                return new GRDNCrewState(_host, _locoIds,
                    (_index + 1) % _locoIds.Count, false);

            case InputAction.Activate:
            {
                string toTrain   = _locoIds[_index];
                string fromTrain = RadioIntegration.GetPlayerTrainNumber();

                if (string.IsNullOrEmpty(fromTrain))
                {
                    Main.ModEntry.Logger.Warning("[CrewMode] Not in a loco — can't re-assign.");
                    return this;
                }
                if (fromTrain == toTrain)
                {
                    Main.ModEntry.Logger.Log($"[CrewMode] Already on {toTrain}.");
                    return this;
                }

                _host.StartCoroutine(PostCrewUpdate(fromTrain, toTrain));
                // Return busy state — disables ASSIGN until user navigates away/back
                return new GRDNCrewState(_host, _locoIds, _index, busy: true);
            }

            default:
                return this;
        }
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────

    private IEnumerator PostCrewUpdate(string fromTrain, string toTrain)
    {
        string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
        string secret  = Main.Settings.BotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[CrewMode] BotPushUrl not set — re-assign skipped.");
            yield break;
        }

        string url  = pushUrl + "/update-crew";
        string body = $"{{\"fromTrainNumber\":\"{Esc(fromTrain)}\",\"toTrainNumber\":\"{Esc(toTrain)}\"}}";
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
                Main.ModEntry.Logger.Log($"[CrewMode] Re-assigned {fromTrain} → {toTrain}");
            else
                Main.ModEntry.Logger.Warning($"[CrewMode] Update failed ({req.responseCode}): {req.error}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MakeContent(List<string> locos, int idx, bool busy)
    {
        if (busy)             return "Sending...";
        if (locos.Count == 0) return "No locos found";

        string current  = RadioIntegration.GetPlayerTrainNumber() ?? "?";
        string selected = locos[idx];
        return $"{idx + 1}/{locos.Count}  [{selected}]\nNow: {current}";
    }

    private static List<string> ScanLocos()
    {
        var list = new List<string>();
        try
        {
            var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
            foreach (var car in allCars)
            {
                if (!car.IsLoco) continue;
                string num = ExtractNumber(car.ID);
                if (!string.IsNullOrEmpty(num) && !list.Contains(num))
                    list.Add(num);
            }
            list.Sort();
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[CrewMode] ScanLocos: " + ex.Message);
        }
        return list;
    }

    private static string ExtractNumber(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var m = Regex.Match(id, @"(\d+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
#endif
