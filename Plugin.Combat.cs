// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HowToFishTrainer
{
    public partial class Plugin
    {
        // Read by the patch bodies, so they have to be static.
        // AimAssistBoost is the momentary state (may be gated by a held mouse
        // button); _aimEnabled is the master switch that decides whether the
        // patches and tuning overrides are installed at all.
        public static bool AimAssistBoost;
        public static bool HipFireAssist;
        public static bool AimAtHead = true;
        public static bool MagicBullet;
        public static float MagicBulletSpeed = 3f;
        public static bool AimThroughWalls = true;
        public static bool WallPenetration;
        public static bool WaterPenetration;
        public static bool IgnoreBossImmortality;

        // Changes only the facing this client reports to the server, i.e. what
        // other players see; the local camera is untouched. Only meaningful in
        // an agreed room, since in solo nobody else is there to see it.
        public static bool SpinBot;
        public static float SpinSpeed = 720f;
        public static bool SpinPitchDown = true;

        // Localised camera x angle for looking straight down; 90 exactly makes
        // the yaw ambiguous.
        private const float SpinDownPitch = 89f;

        // The camera angles this client reports to the server, which is what
        // other players see. Shared by the network patch and the third-person
        // body so the two always match.
        public static float ReportedYaw(float cameraYaw)
        {
            return SpinBot ? Mathf.Repeat(Time.time * SpinSpeed, 360f) : cameraYaw;
        }

        public static float ReportedPitch(float cameraPitch)
        {
            return SpinBot && SpinPitchDown ? SpinDownPitch : cameraPitch;
        }
        public static bool LockHealth;
        public static bool InfiniteJump;
        public static bool RouletteAlwaysWin;
        public static bool DamageMultiplierOn;
        public static float DamageMultiplier = 5f;

        private bool _aimEnabled;
        private bool _aimHoldMode = true;
        private int _aimHoldButton = 4;
        private float _aimStrength = 20f;
        private bool _aimAllAround = true;
        private string _appliedAimSignature;

        // Also used by the head-targeting patch to skip creatures out of range.
        public const float AimRange = 500f;

        private bool _lockFullness;
        private bool _infiniteAmmo;

        // Original PlayerAimAssist tuning values, captured before we override
        // them so the component can be restored when the feature is switched off.
        private PlayerAimAssist _boostedAimAssist;
        private Dictionary<string, float> _originalAimValues;

        private static readonly Dictionary<string, float> AggressiveAimValues = new Dictionary<string, float>
        {
            { "_maxTargetDistance", AimRange },
            { "_adsAcquireAngle", 90f },
            { "_trackingBreakAngle", 180f },
            { "_targetScanInterval", 0.02f },
            { "_maxRotationSpeed", 1440f },
            { "_trackingSharpness", 60f },
        };

        private float AimTuningValue(string field)
        {
            float value = AggressiveAimValues[field];
            // Strength scales how hard and fast the camera is pulled.
            if (field == "_trackingSharpness" || field == "_maxRotationSpeed") value *= _aimStrength;
            // 180 makes the acquire cone cover everything, including behind:
            // CacheSettings turns it into cos(180) = -1, which every target passes.
            if (field == "_adsAcquireAngle" && _aimAllAround) value = 180f;
            return value;
        }

        // ---- Vitals / ammo -------------------------------------------------
        private void ApplyVitalsAndGear()
        {
            if (!_gateOpen || !_wantCheatsEnabled) return;

            // Bosses re-enable immortality at the start of each animation.
            if (_isHost && IgnoreBossImmortality) ForceBossMortal();

            var player = Player.LocalPlayer;
            if (player == null) return;

            // SyncVar writes only take effect on the server, so vitals are
            // host-only.
            var vitals = player.Vitals;
            if (_isHost && vitals != null)
            {
                if (LockHealth)
                {
                    if (vitals._syncedHealth.Value < 100) vitals._syncedHealth.Value = 100;
                    if (vitals._syncedPoison.Value > 0) vitals._syncedPoison.Value = 0;
                    if (vitals._syncedFire.Value > 0) vitals._syncedFire.Value = 0;
                }
                if (_lockFullness && vitals._syncedFullness.Value < 100)
                {
                    vitals._syncedFullness.Value = 100;
                }
            }

            if (AnyWeaponTweakOn()) ApplyWeaponTweaks(player);

            if (_infiniteAmmo)
            {
                var weapon = GetHeldWeapon(player);
                if (weapon != null && weapon.Attachments != null)
                {
                    int full = weapon.Attachments.AmmoPerMag;
                    if (weapon.Ammo < full) SetWeaponAmmo(weapon, full);
                }
            }
        }

        // Big enough for any boss, small enough that the damage patch's cap
        // keeps it from overflowing.
        private const int KillAllDamage = 10_000_000;

        // The game's /killallcreatures command only marks every species as
        // caught in the journal (and saves that), so this instead lands one
        // lethal melee hit on each living creature through Creature.LocalHit,
        // the same path a real hit takes (and /killboss uses), so deaths, kill
        // score and drops behave normally.
        private static FieldInfo _bossImmortalField;

        // Bosses are immortal while they play an animation (intro, phase
        // change), and the flag is global: InitializeBossFight only clears it
        // for the first boss alive, so with several spawned the rest depend on
        // that one's state. Both Creature.LocalHit and ServerChangeHp bail out
        // on it, which is why neither shots nor kill-all did anything.
        private static void ForceBossMortal()
        {
            // The server's flag is the one that blocks damage; a client clearing
            // its own copy would only desync its view.
            if (Server.Instance == null || !Server.Instance.IsServerInitialized) return;
            if (!BossManager.IsImmortal) return;

            BossManager.ToggleImmortal(false);

            // The static mirrors the SyncVar through its OnChange callback; set
            // it directly as well so the very next hit isn't blocked if that
            // callback hasn't run yet.
            if (_bossImmortalField == null)
            {
                _bossImmortalField = AccessTools.Field(typeof(BossManager), "<IsImmortal>k__BackingField");
            }
            _bossImmortalField?.SetValue(null, false);
        }

        // Runs on the host, crediting the kills to whoever asked for them.
        private void KillAllCreatures(Player player)
        {
            if (player == null) return;

            ForceBossMortal();

            // Snapshot first: deaths can change ItemManager.Items while we go.
            // One creature can own several item transforms, hence Distinct.
            var creatures = ItemManager.Items.Values
                .Select(item => item != null ? item.Creature : null)
                .Where(c => c != null)
                .Distinct()
                .ToList();

            int killed = 0;
            foreach (var creature in creatures)
            {
                if (creature == null || !creature.isActiveAndEnabled || creature.IsDeinitializing || creature.IsDead) continue;

                Vector3 point = creature.transform.position;
                Vector3 dir = (point - player.Transform.position).normalized;
                try
                {
                    creature.LocalHit(creature.transform, point, dir, player, KillAllDamage, rangedHit: false);
                    killed++;
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Could not kill {creature.name}: {e.Message}");
                }
            }
            Logger.LogInfo($"Kill all: hit {killed} creatures.");
        }

        private static Weapon GetHeldWeapon(Player player)
        {
            if (player == null || player.Holding == null) return null;
            var held = player.Holding.HeldItem;
            return held != null ? held.Weapon : null;
        }

        private static MethodInfo _ammoSetter;

        private void SetWeaponAmmo(Weapon weapon, int amount)
        {
            try
            {
                if (_ammoSetter == null)
                {
                    _ammoSetter = AccessTools.PropertySetter(typeof(Weapon), "Ammo");
                    if (_ammoSetter == null) return;
                }
                _ammoSetter.Invoke(weapon, new object[] { amount });
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not set ammo: " + e.Message);
                _infiniteAmmo = false;
            }
        }

        // ---- Aim assist tuning ---------------------------------------------
        private void ApplyAimTuning(bool wanted)
        {
            try
            {
                var player = Player.LocalPlayer;
                var assist = player != null ? player.GetComponent<PlayerAimAssist>() : null;

                bool otherInstance = _boostedAimAssist != null && assist != _boostedAimAssist;
                if (!wanted || assist == null || otherInstance)
                {
                    RestoreAimTuning();
                    if (!wanted || assist == null) return;
                }
                string signature = $"{_aimStrength}|{_aimAllAround}";
                if (_boostedAimAssist == assist && _appliedAimSignature == signature) return;

                if (_boostedAimAssist != assist)
                {
                    _originalAimValues = new Dictionary<string, float>();
                    foreach (var kv in AggressiveAimValues)
                    {
                        var field = AccessTools.Field(typeof(PlayerAimAssist), kv.Key);
                        if (field != null) _originalAimValues[kv.Key] = (float)field.GetValue(assist);
                    }
                    _boostedAimAssist = assist;
                }

                foreach (var name in AggressiveAimValues.Keys)
                {
                    AccessTools.Field(typeof(PlayerAimAssist), name)?.SetValue(assist, AimTuningValue(name));
                }
                _appliedAimSignature = signature;

                // Derived values (squared distance, cosine thresholds) are
                // cached, so they have to be recomputed after the overrides.
                AccessTools.Method(typeof(PlayerAimAssist), "CacheSettings")?.Invoke(assist, null);
                Logger.LogInfo("Aim assist tuning boosted.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not tune aim assist: " + e.Message);
            }
        }

        private void RestoreAimTuning()
        {
            if (_boostedAimAssist == null || _originalAimValues == null)
            {
                _boostedAimAssist = null;
                _appliedAimSignature = null;
                return;
            }

            try
            {
                foreach (var kv in _originalAimValues)
                {
                    AccessTools.Field(typeof(PlayerAimAssist), kv.Key)?.SetValue(_boostedAimAssist, kv.Value);
                }
                AccessTools.Method(typeof(PlayerAimAssist), "CacheSettings")?.Invoke(_boostedAimAssist, null);
            }
            catch { /* component is gone; nothing to restore */ }

            _boostedAimAssist = null;
            _originalAimValues = null;
            _appliedAimSignature = null;
        }
    }
}
