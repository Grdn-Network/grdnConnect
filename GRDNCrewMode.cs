// GRDNCrewMode.cs
// Second CommsRadioAPI mode — lets a player re-assign themselves to a different
// loco from inside the game, without touching Discord.
// Compiled only when CommsRadioAPI.dll is present in lib/ (COMMS_RADIO_API defined).
#if COMMS_RADIO_API
//
// HOW IT WORKS
// ─────────────
// When activated, scans for all locomotives in the world and presents them as
// a scrollable list (Button A = previous, Button B = next).
// Pressing the action confirms and tells the bot:
//   POST /update-crew { fromTrainNumber: "034", toTrainNumber: "201" }
// The bot looks up whoever is registered to "034" and re-assigns them to "201".
//
// FIRST-TIME REQUIREMENT
// ───────────────────────
// The player must have run /setcrew in Discord at least once to establish
// their initial Discord ↔ train link. After that, this mode handles all
// mid-op loco changes automatically.
//
// SOFT DEPENDENCY: CommsRadioAPI
// This file is only active when CommsRadioAPI is found.
// Registered by RadioIntegration.TryInit().

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The GRDN Crew radio mode — scroll through active locos and claim one.
/// Registered alongside GRDNRadioMode by RadioIntegration.TryInit().
/// </summary>
public class GRDNCrewMode : AStateBehaviour
{
    // ── State ─────────────────────────────────────────────────────────────────

    private List<string> _locoIds = new List<string>(); // numeric train numbers
    private int _index;
    private readonly MonoBehaviour _host; // for coroutine hosting

    // ── Construction ──────────────────────────────────────────────────────────

    public GRDNCrewMode(MonoBehaviour host)
    {
        _host = host;
    }

    // ── AStateBehaviour lifecycle ─────────────────────────────────────────────

    public override void Enable()
    {
        RefreshLocoList();
        UpdateDisplay();
    }

    public override void Disable() { }

    // ── Buttons ───────────────────────────────────────────────────────────────

    public override ButtonBehaviourType ButtonABehaviour =>
        _locoIds.Count > 1 ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore;

    public override ButtonBehaviourType ButtonBBehaviour =>
        _locoIds.Count > 0 ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore;

    // Button A (◄) — previous loco
    public override void ButtonACustomAction()
    {
        if (_locoIds.Count == 0) return;
        _index = (_index - 1 + _locoIds.Count) % _locoIds.Count;
        UpdateDisplay();
    }

    // Button B (►) — next loco OR confirm (when only one option)
    public override void ButtonBCustomAction()
    {
        if (_locoIds.Count == 0) return;

        if (_locoIds.Count == 1)
        {
            // Single loco — B = confirm
            ConfirmSelection();
        }
        else
        {
            _index = (_index + 1) % _locoIds.Count;
            UpdateDisplay();
        }
    }

    // ── Confirm selection ─────────────────────────────────────────────────────

    private void ConfirmSelection()
    {
        if (_locoIds.Count == 0) return;

        string toTrain   = _locoIds[_index];
        string fromTrain = RadioIntegration.GetPlayerTrainNumber();

        if (string.IsNullOrEmpty(fromTrain))
        {
            display?.SetContent("GRDN CREW", "Not in a loco", Color.red);
            return;
        }

        if (fromTrain == toTrain)
        {
            display?.SetContent("GRDN CREW", $"Already on {toTrain}", Color.yellow);
            return;
        }

        display?.SetContent("GRDN CREW", $"Claiming {toTrain}...", Color.cyan);
        _host.StartCoroutine(PostCrewUpdate(fromTrain, toTrain));
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────

    private IEnumerator PostCrewUpdate(string fromTrain, string toTrain)
    {
        string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
        string secret  = Main.Settings.BotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            display?.SetContent("GRDN CREW", "No BotPushUrl set", Color.red);
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

            if (req.result == UnityWebRequest.Result.Success)
            {
                Main.ModEntry.Logger.Log($"[CrewMode] Re-assigned {fromTrain} → {toTrain}");
                display?.SetContent("GRDN CREW", $"✓ Now on {toTrain}", Color.green);
            }
            else
            {
                Main.ModEntry.Logger.Warning($"[CrewMode] Update failed: {req.error}");
                display?.SetContent("GRDN CREW", "Update failed", Color.red);
            }
        }
    }

    // ── Loco list ─────────────────────────────────────────────────────────────

    private void RefreshLocoList()
    {
        _locoIds.Clear();
        _index = 0;

        var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
        foreach (var car in allCars)
        {
            if (!car.IsLoco) continue;
            var num = ExtractNumber(car.ID);
            if (!string.IsNullOrEmpty(num) && !_locoIds.Contains(num))
                _locoIds.Add(num);
        }

        _locoIds.Sort(); // consistent order

        // Pre-select the player's current loco if present
        string myTrain = RadioIntegration.GetPlayerTrainNumber();
        if (!string.IsNullOrEmpty(myTrain))
        {
            int idx = _locoIds.IndexOf(myTrain);
            if (idx >= 0) _index = idx;
        }
    }

    // ── Display ───────────────────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        if (display == null) return;

        if (_locoIds.Count == 0)
        {
            display.SetContent("GRDN CREW", "No locos found", Color.grey);
            return;
        }

        string current  = RadioIntegration.GetPlayerTrainNumber() ?? "?";
        string selected = _locoIds[_index];
        string body     = $"{_index + 1}/{_locoIds.Count}  [{selected}]\nNow: {current}";
        display.SetContent("GRDN CREW", body, Color.white);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
