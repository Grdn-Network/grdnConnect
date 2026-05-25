// GRDNCrewMode.cs
// GRDN Crew radio mode — aim the comms radio at a loco and press ASSIGN.
// Works exactly like the switch changer: point at the loco from outside and click.
// Only recognises locos (IsLoco == true) — tenders and cargo cars are ignored.
//
// HOW IT WORKS
// ─────────────
// Point the comms radio at any locomotive.
// The LCD shows its type and number.
// Press ASSIGN to register yourself to that loco.
//
// ASSIGN sends: POST /update-crew { fromTrainNumber, toTrainNumber, locoType }
//
//   fromTrainNumber = last successfully registered train this session,
//                     or the targeted loco if this is the first use
//                     (confirms initial registration / updates locoType only).
//   toTrainNumber   = the targeted loco's number.
//   locoType        = the targeted loco's type code (DE6, S282, BE2, …).
//
// The bot updates train_number + loco_type in the DB, then refreshes the
// train board and your Discord nickname automatically.
//
// FIRST-TIME REQUIREMENT
// ───────────────────────
// You must have run /setcrew in Discord at least once per account to link
// your Discord ID to a train number. After that this mode handles all
// mid-op loco changes without Discord.
//
// SWITCHING LOCOS MID-OP
// ───────────────────────
// 1. Stand alongside the new loco so it's in your crosshair.
// 2. Open GRDN CREW — LCD shows the loco type + number.
//    (If the display is stale, tap Up or Down to rescan.)
// 3. Press ASSIGN.
#if COMMS_RADIO_API

using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using CommsRadioAPI;
using DV;
using UnityEngine;
using UnityEngine.Networking;

public class GRDNCrewState : AStateBehaviour
{
    private readonly MonoBehaviour _host;
    private readonly string        _trainNumber;  // targeted loco number (null = nothing in sights)
    private readonly string        _locoType;     // targeted loco type code (null = unknown)
    private readonly Disp          _disp;

    // ── Display state enum ─────────────────────────────────────────────────────
    private enum Disp { Idle, Pending, Success, Failure }

    // ── Static result signal — set by the PostCrewAssign coroutine ────────────
    // Reset to Idle before each send; set to Success/Failure on completion.
    // Unity coroutines run on the main thread so no volatile needed.
    private static Disp _crewResult = Disp.Idle;

    // Persists for the life of the game session.
    private static string _lastRegisteredTrain = null;

    // ── Highlight state — CommsRadioAPI "Valid" blue material on targeted loco ─
    private static Material _highlightMat      = null;  // cached on first use
    private static TrainCar _lastScannedCar    = null;  // set by ScanTarget as side effect
    private static TrainCar _lastHighlightedCar = null; // currently highlighted car

    private const float RAYCAST_RANGE = 100f;

    // ── Entry point — called when this mode is first constructed ──────────────
    public GRDNCrewState(MonoBehaviour host)
        : this(host, ScanTarget(null), Disp.Idle)
    { }

    // ── Internal constructor ──────────────────────────────────────────────────
    private GRDNCrewState(MonoBehaviour host, (string num, string type) target, Disp disp)
        : base(new CommsRadioState(
            "GRDN CREW",
            MakeContent(target.num, target.type, disp),
            (disp == Disp.Idle && target.num != null) ? "ASSIGN" : "",
            LCDArrowState.Off,
            (disp == Disp.Idle && target.num != null) ? LEDState.On : LEDState.Off,
            (disp == Disp.Idle && target.num != null) ? ButtonBehaviourType.Override : ButtonBehaviourType.Regular))
    {
        _host        = host;
        _trainNumber = target.num;
        _locoType    = target.type;
        _disp        = disp;
    }

    // ── Real-time targeting — updates LCD as the player aims ──────────────────
    public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
    {
        // Pending: check if the coroutine has posted a result
        if (_disp == Disp.Pending)
        {
            var r = _crewResult;
            if (r == Disp.Success || r == Disp.Failure)
                return new GRDNCrewState(_host, (_trainNumber, _locoType), r);
            return this;
        }

        // Success/Failure: hold until player rescans with Up/Down
        if (_disp == Disp.Success || _disp == Disp.Failure)
            return this;

        // Idle: rescan each frame for a target change
        var target = ScanTarget(utility.SignalOrigin);
        var newCar = _lastScannedCar; // set as side effect of ScanTarget

        // Keep highlight in sync — apply when a new car is scanned, clear when lost
        if (newCar != _lastHighlightedCar)
        {
            ClearHighlight();
            if (newCar != null)
                ApplyHighlight(utility, newCar);
        }

        if (target.num == _trainNumber && target.type == _locoType)
            return this;

        return new GRDNCrewState(_host, target, Disp.Idle);
    }

    // ── State machine ─────────────────────────────────────────────────────────
    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        switch (action)
        {
            // Up / Down — always rescan, clears any result or pending state
            case InputAction.Up:
            case InputAction.Down:
                ClearHighlight();
                return new GRDNCrewState(_host, ScanTarget(utility.SignalOrigin), Disp.Idle);

            case InputAction.Activate:
                // Non-idle: treat as rescan
                if (_disp != Disp.Idle)
                {
                    ClearHighlight();
                    return new GRDNCrewState(_host, ScanTarget(utility.SignalOrigin), Disp.Idle);
                }

                // Nothing targeted — fresh scan
                if (_trainNumber == null)
                {
                    ClearHighlight();
                    return new GRDNCrewState(_host, ScanTarget(utility.SignalOrigin), Disp.Idle);
                }

                string fromTrain = _lastRegisteredTrain ?? _trainNumber;
                _crewResult = Disp.Idle; // clear previous result before firing
                ClearHighlight();         // entering Pending — remove highlight
                _host.StartCoroutine(PostCrewAssign(fromTrain, _trainNumber, _locoType));
                return new GRDNCrewState(_host, (_trainNumber, _locoType), Disp.Pending);

            default:
                return new GRDNCrewState(_host, (_trainNumber, _locoType), _disp);
        }
    }

    // ── LCD content ───────────────────────────────────────────────────────────
    private static string MakeContent(string trainNumber, string locoType, Disp disp)
    {
        switch (disp)
        {
            case Disp.Pending: return "Assigning...";
            case Disp.Success: return $"Done! Train {trainNumber}";
            case Disp.Failure: return "Failed! See log";
        }
        // Idle
        if (trainNumber == null) return "Aim at a loco";
        string prefix = !string.IsNullOrEmpty(locoType) ? locoType + "  " : "";
        return $"{prefix}{trainNumber}";
    }

    // ── Raycast targeting ─────────────────────────────────────────────────────
    private static (string num, string type) ScanTarget(Transform signalOrigin)
    {
        try
        {
            Vector3 pos, dir;
            if (signalOrigin != null)
            {
                pos = signalOrigin.position;
                dir = signalOrigin.forward;
            }
            else
            {
                var cam = Camera.main;
                if (cam == null) { _lastScannedCar = null; return (null, null); }
                pos = cam.transform.position;
                dir = cam.transform.forward;
            }

            if (!Physics.Raycast(pos, dir, out RaycastHit hit, RAYCAST_RANGE))
            { _lastScannedCar = null; return (null, null); }

            var car = hit.transform.GetComponentInParent<TrainCar>();
            if (car == null || !car.IsLoco) { _lastScannedCar = null; return (null, null); }

            _lastScannedCar = car;
            var match  = Regex.Match(car.ID ?? "", @"(\d+)$");
            string num = match.Success ? match.Groups[1].Value : car.ID;
            string type = RadioIntegration.MapCarType(((object)car.carType).ToString());

            return (num, type);
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[CrewMode] ScanTarget: " + ex.Message);
            _lastScannedCar = null;
            return (null, null);
        }
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────
    private IEnumerator PostCrewAssign(string fromTrain, string toTrain, string locoType)
    {
        string pushUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
        string secret  = GRDNConnectBehaviour.ActiveBotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[CrewMode] BotPushUrl not set — skipped.");
            _crewResult = Disp.Failure;
            yield break;
        }

        string locoField = !string.IsNullOrEmpty(locoType)
            ? $",\"locoType\":\"{Esc(locoType)}\""
            : "";

        var (steamId, steamName) = RadioIntegration.GetLocalSteamInfo();
        string steamField = steamId > 0
            ? $",\"steamId\":\"{steamId}\",\"steamName\":\"{Esc(steamName)}\""
            : "";

        string fullUrl = pushUrl + "/update-crew";
        string body    = $"{{\"fromTrainNumber\":\"{Esc(fromTrain)}\",\"toTrainNumber\":\"{Esc(toTrain)}\"{locoField}{steamField}}}";
        byte[] raw     = Encoding.UTF8.GetBytes(body);

        using (var req = new UnityWebRequest(fullUrl, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(secret))
                req.SetRequestHeader("x-secret", secret);

            yield return req.SendWebRequest();

            if (req.error == null)
            {
                _lastRegisteredTrain = toTrain;
                _crewResult = Disp.Success;
                Main.ModEntry.Logger.Log(
                    $"[CrewMode] Registered → [{locoType ?? "?"}] {toTrain} (from {fromTrain})");
            }
            else
            {
                _crewResult = Disp.Failure;
                Main.ModEntry.Logger.Warning(
                    $"[CrewMode] Request failed ({req.responseCode}): {req.error}");
            }
        }
    }

    // ── Loco highlight ─────────────────────────────────────────────────────────
    // Applies the CommsRadioAPI "Valid" (blue) material as an extra layer on all
    // MeshRenderers of the targeted loco — identical to how the vanilla Clear
    // comms radio mode highlights its target car.

    private static void ApplyHighlight(CommsRadioUtility utility, TrainCar car)
    {
        if (car == null) return;

        if (_highlightMat == null)
            _highlightMat = utility.GetMaterial(VanillaMaterial.Valid);
        if (_highlightMat == null) return;

        _lastHighlightedCar = car;
        try
        {
            foreach (var rend in car.GetComponentsInChildren<MeshRenderer>())
            {
                if (rend == null) continue;
                var mats    = rend.sharedMaterials;
                var newMats = new Material[mats.Length + 1];
                mats.CopyTo(newMats, 0);
                newMats[mats.Length] = _highlightMat;
                rend.sharedMaterials = newMats;
            }
        }
        catch { }
    }

    private static void ClearHighlight()
    {
        if (_lastHighlightedCar == null) return;
        if (_highlightMat == null) { _lastHighlightedCar = null; return; }

        try
        {
            foreach (var rend in _lastHighlightedCar.GetComponentsInChildren<MeshRenderer>())
            {
                if (rend == null) continue;
                var mats = rend.sharedMaterials;
                if (mats.Length > 0 && mats[mats.Length - 1] == _highlightMat)
                {
                    var newMats = new Material[mats.Length - 1];
                    Array.Copy(mats, newMats, mats.Length - 1);
                    rend.sharedMaterials = newMats;
                }
            }
        }
        catch { }

        _lastHighlightedCar = null;
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
#endif
