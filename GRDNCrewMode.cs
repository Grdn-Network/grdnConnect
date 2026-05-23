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
    private readonly bool          _sent;

    // Persists for the life of the game session.
    // Tracks the last train number successfully pushed so the bot knows which
    // registration to move when the player changes locos.
    private static string _lastRegisteredTrain = null;

    private const float RAYCAST_RANGE = 100f;   // metres — same order of magnitude as switch changer

    // ── Entry point — called when this mode state is first constructed ────────
    public GRDNCrewState(MonoBehaviour host)
        : this(host, ScanTarget(), sent: false)
    { }

    // ── Internal constructor ──────────────────────────────────────────────────
    private GRDNCrewState(MonoBehaviour host, (string num, string type) target, bool sent)
        : base(new CommsRadioState(
            "GRDN CREW",
            MakeContent(target.num, target.type, sent),
            (!sent && target.num != null) ? "ASSIGN" : "",
            LCDArrowState.Off,
            LEDState.Off,
            (!sent && target.num != null) ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore))
    {
        _host        = host;
        _trainNumber = target.num;
        _locoType    = target.type;
        _sent        = sent;
    }

    // ── State machine ─────────────────────────────────────────────────────────
    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        switch (action)
        {
            // Up / Down — rescan targeting in case the player has re-aimed at a different loco.
            // Also clears the sent state so the player can assign again after moving.
            case InputAction.Up:
            case InputAction.Down:
                return new GRDNCrewState(_host, ScanTarget(), sent: false);

            case InputAction.Activate:
                if (_sent) return this;  // ignore; prevent double-send while request is in flight

                // Nothing targeted — do a fresh scan rather than assigning nothing
                if (_trainNumber == null)
                    return new GRDNCrewState(_host, ScanTarget(), sent: false);

                // fromTrain: where the bot will look for the existing registration.
                // First use this session → assume the targeted loco is already registered
                // (just updates locoType). Subsequent uses → move from the last registered train.
                string fromTrain = _lastRegisteredTrain ?? _trainNumber;
                _host.StartCoroutine(PostCrewAssign(fromTrain, _trainNumber, _locoType));

                // Return sent state — disables ASSIGN until player re-aims (Up/Down)
                return new GRDNCrewState(_host, (_trainNumber, _locoType), sent: true);

            default:
                return this;
        }
    }

    // ── Raycast targeting ─────────────────────────────────────────────────────
    // Fires a ray from the player's camera forward.
    // Returns (trainNumber, locoType) if a loco is centred; (null, null) otherwise.
    // Tenders and cargo cars return (null, null) — IsLoco filters them out.
    private static (string num, string type) ScanTarget()
    {
        try
        {
            var cam = Camera.main;
            if (cam == null) return (null, null);

            if (!Physics.Raycast(cam.transform.position, cam.transform.forward,
                    out RaycastHit hit, RAYCAST_RANGE))
                return (null, null);

            // The hit collider may be on a child transform — walk up to find the TrainCar root
            var car = hit.transform.GetComponentInParent<TrainCar>();
            if (car == null || !car.IsLoco) return (null, null);   // ignore tenders / cargo

            var match    = Regex.Match(car.ID ?? "", @"(\d+)$");
            string num   = match.Success ? match.Groups[1].Value : car.ID;
            string type  = RadioIntegration.MapCarType(((object)car.carType).ToString());

            return (num, type);
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning("[CrewMode] ScanTarget: " + ex.Message);
            return (null, null);
        }
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────
    private IEnumerator PostCrewAssign(string fromTrain, string toTrain, string locoType)
    {
        string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
        string secret  = Main.Settings.BotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[CrewMode] BotPushUrl not set — skipped.");
            yield break;
        }

        // locoType is optional — omit the field entirely if unknown
        string locoField = !string.IsNullOrEmpty(locoType)
            ? $",\"locoType\":\"{Esc(locoType)}\""
            : "";

        string body = $"{{\"fromTrainNumber\":\"{Esc(fromTrain)}\",\"toTrainNumber\":\"{Esc(toTrain)}\"{locoField}}}";
        byte[] raw  = Encoding.UTF8.GetBytes(body);

        using (var req = new UnityWebRequest(pushUrl + "/update-crew", "POST"))
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
                Main.ModEntry.Logger.Log(
                    $"[CrewMode] Registered → [{locoType ?? "?"}] {toTrain} (from {fromTrain})");
            }
            else
            {
                Main.ModEntry.Logger.Warning(
                    $"[CrewMode] Request failed ({req.responseCode}): {req.error}");
            }
        }
    }

    // ── Display ───────────────────────────────────────────────────────────────
    private static string MakeContent(string trainNumber, string locoType, bool sent)
    {
        if (sent)                return "Assigning...";
        if (trainNumber == null) return "Aim at a loco";
        // e.g. "DE6  034" or just "034" if type unknown
        string prefix = !string.IsNullOrEmpty(locoType) ? locoType + "  " : "";
        return $"{prefix}{trainNumber}";
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
#endif
