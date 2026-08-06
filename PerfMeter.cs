// PerfMeter.cs
// The lag meter for GRDNConnect, ported from DLE/Data/PerfMeter.cs so both mods
// report the same shape of numbers and can be read side by side.
//
// Three things get measured:
//   1. Frame time, sampled allocation-free from GRDNConnectBehaviour.Update, as
//      p50/p95/max plus a hitch count. This is the whole game's frame, not just
//      the mod's, so it is the baseline everything else is judged against.
//   2. GC collections and heap size, snapshotted once per second over a rolling
//      3 minute window. Managed allocation is what turns a cheap timer into a
//      stutter, so it is tracked separately from raw frame time.
//   3. Main-thread HTTP handler time per route, for the listener on port 7230.
//      HandleRequest runs inside the ListenLoop coroutine, so every millisecond
//      it spends is a millisecond the frame paid for.
//
// Also counts listener spin frames: ListenLoop parks on `yield return null`
// while waiting for a connection, so it resumes once per frame whether or not
// any request is in flight. That count makes the idle cost visible instead of
// leaving it as an assumption.
//
// Read it with `connect.lag` in the in-game console, or `connect.lag watch` for
// a line every 10 seconds that can be correlated with what is happening around
// you. A real Unity profiler needs a development build of the game; this is the
// practical in-mod equivalent.
//
// Cost discipline: fixed-size rings, no per-frame allocation, no LINQ in the
// sampling path. The only allocation is in the report, which is on demand.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

internal static class PerfMeter
{
	// ── Frame ring — raw unscaled delta times, main thread only ───────────────
	private const int FrameN = 2048;
	private static readonly float[] _frames  = new float[FrameN];
	private static readonly float[] _sortBuf = new float[FrameN];
	private static int _head;
	private static int _filled;

	// ── Per-second ring — GC count, heap, hitches, spins. 3 minutes of history ─
	private const int SecN = 180;
	private static readonly int[]   _gc      = new int[SecN];
	private static readonly float[] _heapMb  = new float[SecN];
	private static readonly int[]   _hitches = new int[SecN];
	private static readonly int[]   _spins   = new int[SecN];
	private static int   _secHead;
	private static int   _secFilled;
	private static float _secAccum;
	private static int   _hitchAccum;
	private static int   _spinAccum;

	// ── HTTP handler time by route, cumulative for the session ────────────────
	private static readonly Dictionary<string, long> _reqMs =
		new Dictionary<string, long>(StringComparer.Ordinal);
	private static readonly Dictionary<string, int> _reqN =
		new Dictionary<string, int>(StringComparer.Ordinal);
	private static readonly Dictionary<string, long> _reqMax =
		new Dictionary<string, long>(StringComparer.Ordinal);

	internal static bool Watch;
	private static float _watchAccum;

	// A frame slower than this counts as a hitch (50 ms ~= a visible stutter).
	private const float HitchSec = 0.05f;

	/// <summary>Called every frame from GRDNConnectBehaviour.Update. No allocations.</summary>
	internal static void Sample(float dt)
	{
		_frames[_head] = dt;
		_head = (_head + 1) % FrameN;
		if (_filled < FrameN) _filled++;
		if (dt > HitchSec) _hitchAccum++;

		_secAccum += dt;
		if (_secAccum >= 1f)
		{
			_secAccum = 0f;
			_gc[_secHead]      = GC.CollectionCount(0);
			_heapMb[_secHead]  = GC.GetTotalMemory(false) / (1024f * 1024f);
			_hitches[_secHead] = _hitchAccum;
			_spins[_secHead]   = _spinAccum;
			_hitchAccum = 0;
			_spinAccum  = 0;
			_secHead = (_secHead + 1) % SecN;
			if (_secFilled < SecN) _secFilled++;

			if (Watch)
			{
				_watchAccum += 1f;
				if (_watchAccum >= 10f)
				{
					_watchAccum = 0f;
					Main.ModEntry.Logger.Log("[Lag] " + OneLine());
				}
			}
		}
	}

	/// <summary>
	/// One frame where ListenLoop resumed only to find no connection waiting.
	/// A plain increment so the measurement cannot distort what it measures.
	/// </summary>
	internal static void NoteListenerSpin() => _spinAccum++;

	/// <summary>
	/// Main-thread HTTP handler time, keyed by route. GRDNConnect's routes are flat
	/// (/locos, /jobs, ...), so the path itself is already the route shape; anything
	/// deeper is trimmed to its first two segments so ids never fan the keys out.
	/// </summary>
	internal static void RecordRequest(string path, long ms)
	{
		string key = RouteKey(path);
		_reqMs.TryGetValue(key, out long t); _reqMs[key] = t + ms;
		_reqN.TryGetValue(key, out int n);   _reqN[key]  = n + 1;
		_reqMax.TryGetValue(key, out long m); if (ms > m) _reqMax[key] = ms;
	}

	private static string RouteKey(string path)
	{
		if (string.IsNullOrEmpty(path)) return "/";
		string p = path.ToLowerInvariant();
		string[] parts = p.Split('/');
		// parts[0] is empty for a leading slash: "/locos" -> ["", "locos"]
		if (parts.Length > 3) return "/" + parts[1] + "/" + parts[2];
		return p;
	}

	// ── Readouts ──────────────────────────────────────────────────────────────

	internal static (float p50, float p95, float max) FramePercentiles()
	{
		if (_filled == 0) return (0f, 0f, 0f);
		Array.Copy(_frames, _sortBuf, _filled);
		Array.Sort(_sortBuf, 0, _filled);
		float p50 = _sortBuf[(int)(_filled * 0.50f)] * 1000f;
		float p95 = _sortBuf[Math.Min(_filled - 1, (int)(_filled * 0.95f))] * 1000f;
		float max = _sortBuf[_filled - 1] * 1000f;
		return (p50, p95, max);
	}

	private static int SumRing(int[] ring, int seconds)
	{
		int take  = Math.Min(seconds, _secFilled);
		int total = 0;
		for (int i = 1; i <= take; i++)
			total += ring[(_secHead - i + SecN) % SecN];
		return total;
	}

	/// <summary>GC(0) collections over the last 60 s, as a delta of the running count.</summary>
	internal static int Gc60()
	{
		int take = Math.Min(60, _secFilled);
		if (take < 2) return 0;
		int newest = _gc[(_secHead - 1 + SecN) % SecN];
		int oldest = _gc[(_secHead - take + SecN) % SecN];
		return Math.Max(0, newest - oldest);
	}

	internal static int Hitches60() => SumRing(_hitches, 60);
	internal static int Spins60()   => SumRing(_spins, 60);

	internal static float HeapMb() =>
		_secFilled == 0 ? 0f : _heapMb[(_secHead - 1 + SecN) % SecN];

	/// <summary>
	/// Spawned car count, read straight off CarSpawner's live registry.
	/// This used to reflect into TrainCarRegistry and always reported 0: `Instance`
	/// is declared on the base SingletonBehaviour&lt;T&gt;, and GetProperty with
	/// Static but no FlattenHierarchy does not return base-class static members, so
	/// the lookup silently returned null. CarSpawner is already a compile-time
	/// reference here, so there is no reason to reflect for this at all.
	/// </summary>
	internal static int LiveCars()
	{
		try
		{
			var all = CarSpawner.Instance?.AllCars;
			if (all == null) return 0;
			if (all is System.Collections.ICollection c) return c.Count;
			int n = 0;
			foreach (var _ in all) n++;
			return n;
		}
		catch { return 0; }
	}

	internal static string OneLine()
	{
		var (p50, p95, max) = FramePercentiles();
		return $"frame p50 {p50:0.0}ms p95 {p95:0.0}ms max {max:0.0}ms; " +
		       $"{Hitches60()} hitch(es)/60s; {Gc60()} GC/60s; heap {HeapMb():0}MB; " +
		       $"{LiveCars()} live car(s); listener spin {Spins60()} frame(s)/60s";
	}

	internal static string FullReport()
	{
		var sb = new StringBuilder();
		sb.AppendLine("[Lag] " + OneLine());

		if (_reqMs.Count == 0)
		{
			sb.AppendLine("[Lag] no HTTP requests served yet this session.");
			return sb.ToString().TrimEnd();
		}

		// Cumulative session totals. The wording is deliberate: a bare number here
		// was once read as a per-request latency, so every line says TOTAL, the
		// request count, and the average explicitly.
		sb.AppendLine("[Lag] HTTP handler time this session (main thread, port " +
		              Main.Settings.Port + "):");

		// Insertion-ordered manual max-selection, no LINQ, top 8 by total cost.
		int shown = 0;
		var used = new HashSet<string>(StringComparer.Ordinal);
		while (shown < 8 && used.Count < _reqMs.Count)
		{
			string bestKey = null;
			long   bestVal = -1;
			foreach (var kv in _reqMs)
			{
				if (used.Contains(kv.Key)) continue;
				if (kv.Value > bestVal) { bestVal = kv.Value; bestKey = kv.Key; }
			}
			if (bestKey == null) break;
			used.Add(bestKey);
			shown++;

			int  n   = _reqN.TryGetValue(bestKey, out int nn) ? nn : 0;
			long mx  = _reqMax.TryGetValue(bestKey, out long mm) ? mm : 0;
			float avg = bestVal / (float)Math.Max(1, n);
			sb.AppendLine($"[Lag]   {bestKey}: {bestVal}ms TOTAL over {n} request(s), " +
			              $"avg {avg:0.0}ms each, worst {mx}ms");
		}
		return sb.ToString().TrimEnd();
	}
}
