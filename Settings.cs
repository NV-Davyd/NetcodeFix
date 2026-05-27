using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace NetcodeFix
{
    // Tunables for every patch. Persisted to a small JSON file next to the
    // mod DLL so tournament organizers can ship a known-good profile.
    //
    // All values are intentionally CONSERVATIVE by default — the mod must
    // not make racing feel worse than vanilla, even at fresh install. Aggressive
    // settings are opt-in via the UI.
    [Serializable]
    public class Settings
    {
        // Master toggle — kill switch the tournament organizer can flip.
        public bool enabled = true;

        // ── Tier 1: adaptive smoothing + smart snap ────────────────────────
        // Multiplier on the per-frame correction rate. Vanilla = 1.0 (1 m/s
        // forward, 2 m/s side at unit magnitude). 2.0 doubles it, so a 3m
        // error converges in roughly half the time. Higher = tighter tracking
        // but more visible micro-jitter from packet timing.
        public float smoothingRateMultiplier = 2.0f;

        // When combined position error exceeds this (in metres), snap
        // immediately instead of trying to smooth. Vanilla is 6m (way too
        // high — by then the car has been visibly wrong for seconds). 3m
        // is a good compromise: smooth in normal conditions, hard snap on
        // a real lag spike.
        public float snapThresholdMetres = 3.0f;

        // ── Tier 1: sanity filter on incoming packets ──────────────────────
        // Reject any incoming packet whose implied speed exceeds this many
        // m/s relative to the previous frame.
        //
        // Empirically: 80 m/s rejected ~1000+ legitimate frames per session
        // in busy rooms (timing jitter on the modelTime stamping can make
        // a legitimate-but-quick-arriving packet pair look fast). 150 m/s
        // (~540 km/h) is safely above any real drift car at sane jitter
        // levels while still catching teleport-grade corruption.
        public float maxImpliedSpeedMps = 150f;

        // Track how often we reject — visible in the UI for tuning.
        [NonSerialized] public int rejectedFramesThisSession;

        // ── Tier 2/3: feature gates ────────────────────────────────────────
        // These don't do anything yet — wired up by future patches.
        // Kept here so the JSON schema is stable across versions.
        public bool tier2InputHistory = false;
        public bool tier2UploadRateBoost = false;
        public bool tier3ContactArbitration = false;

        // ── Singleton-ish accessors ────────────────────────────────────────
        public static Settings Current = new Settings();
        private static string ConfigPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir ?? ".", "netcodefix.json");
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    Current = JsonUtility.FromJson<Settings>(json) ?? new Settings();
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NetcodeFix] Settings load failed, using defaults: " + ex.Message);
            }
            Current = new Settings();
            Save();
        }

        public static void Save()
        {
            try { File.WriteAllText(ConfigPath, JsonUtility.ToJson(Current, true)); }
            catch (Exception ex) { Debug.LogWarning("[NetcodeFix] Settings save failed: " + ex.Message); }
        }
    }
}
