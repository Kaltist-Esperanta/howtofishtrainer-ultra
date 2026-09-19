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
        public static bool SniperNoCooldown;

        // A bug fix rather than a cheat, so it defaults on and isn't cleared with
        // the feature toggles; it still only applies while the trainer is on.
        private bool _fixMuzzleBug = true;
        public static bool MuzzleFixActive;
        public static bool RecoilReductionOn;
        public static float RecoilMultiplier = 0.3f;
        public static bool ModelShakeReductionOn;

        // Resolved each frame for the gun in hand, so the recoil patches don't
        // have to work out which kind of gun fired.
        public static float CurrentModelShakeMultiplier = 1f;
        private float _modelShakeAuto = 0.4f;
        private float _modelShakeSemi = 0.2f;

        // Minimum seconds between two weapon-fire sound bursts; 0 = no limit.
        public static float FireAudioInterval;

        private bool _shotgunSpread;
        private bool _forceFullAuto;
        private bool _fireRateOn;
        private float _fireRateMultiplier = 2f;

        private const float ShotgunSpreadScale = 0.2f;
        private const float MinTimeBetweenShots = 1f / 60f;

        // Weapon tuning lives in private fields that Weapon.Shoot reads inline,
        // so it's applied by writing the fields. Originals are captured once
        // per weapon and everything is recomputed from them, which keeps
        // repeated writes idempotent.
        private class WeaponBackup
        {
            public float Spread;
            public bool FullAuto;
            public bool NoShootDuringAnim;
            public float TimeBetweenShots;
            public int ProjectileCount;
            public int RecoilKnockback;
            public List<KeyValuePair<BarrelAttachment, object[]>> Barrels;
            public Dictionary<string, float> FireAnimSpeeds;

            // What was last written, so the weapon is only rewritten (and its
            // recoil springs rebuilt) when the configuration actually changes.
            public string AppliedSignature;
        }

        private readonly Dictionary<Weapon, WeaponBackup> _weaponBackups = new Dictionary<Weapon, WeaponBackup>();

        // BarrelAttachment recoil fields, in a fixed order shared by backups and
        // the template. Kick fields are the per-shot impulses; the rest tune how
        // the recoil springs recover.
        private static readonly string[] RecoilFieldNames =
        {
            "_adsRecoilPosMulti", "_adsRecoilRotMulti", "_adsRecoilSpringMulti",
            "_screenRecoilAmount", "_modelRecoilPos", "_modelRecoilRot", "_weaponRecoilMulti",
            "_recoilSpringPos", "_recoilDamperPos", "_recoilSpringRot", "_recoilDamperRot",
        };

        private static readonly HashSet<string> KickFieldNames = new HashSet<string>
        {
            "_screenRecoilAmount", "_modelRecoilPos", "_modelRecoilRot", "_weaponRecoilMulti",
        };

        private static readonly string[] FireAnimNames = { "Fire", "FireLast" };

        private static FieldInfo _spreadField;
        private static FieldInfo _projCountField;
        private static FieldInfo _fullAutoField;
        private static FieldInfo _noShootAnimField;
        private static FieldInfo _timeBetweenShotsField;
        private static FieldInfo _knockbackField;
        private static FieldInfo _barrelListField;
        private static FieldInfo _animField;
        private static FieldInfo[] _recoilFields;
        private static bool _weaponFieldsFailed;

        // Natively full-auto weapons, offered as the recoil template for
        // converted guns. The code never names the SMG, so it's found at runtime.
        private List<Item> _templateCandidates;
        private int _templateIndex = -1;
        private object[] _templateRecoil;
        private int _templateKnockback;
        private float _templateTimeBetweenShots;

        private bool AnyWeaponTweakOn()
        {
            return _shotgunSpread || _forceFullAuto || _fireRateOn;
        }

        private bool EnsureWeaponFields()
        {
            if (_spreadField != null) return true;
            if (_weaponFieldsFailed) return false;

            _spreadField = AccessTools.Field(typeof(Weapon), "_spread");
            _projCountField = AccessTools.Field(typeof(Weapon), "_projectileCountPerShot");
            _fullAutoField = AccessTools.Field(typeof(Weapon), "_fullAuto");
            _noShootAnimField = AccessTools.Field(typeof(Weapon), "_noShootingDuringShootAnim");
            _timeBetweenShotsField = AccessTools.Field(typeof(Weapon), "_timeBetweenShots");
            _knockbackField = AccessTools.Field(typeof(Weapon), "_recoilKnockback");
            _barrelListField = AccessTools.Field(typeof(Attachments), "_barrelAttachments");
            _animField = AccessTools.Field(typeof(Tool), "_anim");
            _recoilFields = RecoilFieldNames.Select(n => AccessTools.Field(typeof(BarrelAttachment), n)).ToArray();

            if (_spreadField == null || _projCountField == null || _fullAutoField == null
                || _noShootAnimField == null || _timeBetweenShotsField == null || _knockbackField == null
                || _barrelListField == null || _animField == null || _recoilFields.Any(f => f == null))
            {
                Logger.LogWarning("Weapon fields not found; weapon tweaks disabled.");
                _spreadField = null;
                _weaponFieldsFailed = true;
                _shotgunSpread = _forceFullAuto = _fireRateOn = false;
                return false;
            }
            return true;
        }

        // Rifles and SMGs, as opposed to semi-auto guns converted by the trainer.
        private bool HeldWeaponIsNativeFullAuto()
        {
            var weapon = GetHeldWeapon(Player.LocalPlayer);
            if (weapon == null) return false;

            // Once tweaked, _fullAuto may have been overwritten; the backup has
            // what the gun really is.
            if (_weaponBackups.TryGetValue(weapon, out var backup)) return backup.FullAuto;
            return EnsureWeaponFields() && (bool)_fullAutoField.GetValue(weapon);
        }

        private static List<BarrelAttachment> GetBarrels(Weapon weapon)
        {
            var attachments = weapon != null ? weapon.Attachments : null;
            if (attachments == null) return null;
            return _barrelListField.GetValue(attachments) as List<BarrelAttachment>;
        }

        private static object[] ReadRecoil(BarrelAttachment barrel)
        {
            return _recoilFields.Select(f => f.GetValue(barrel)).ToArray();
        }

        // ---- Recoil template (the SMG) --------------------------------------
        private void EnsureTemplateCandidates()
        {
            if (_templateCandidates != null || !EnsureWeaponFields()) return;

            try
            {
                var all = AccessTools.Field(typeof(GameInfo), "_allItems")?.GetValue(null)
                    as Dictionary<byte, Item>;
                if (all == null) return;

                var found = new List<Item>();
                foreach (var item in all.Values)
                {
                    // These are prefabs. Item.Weapon is only assigned when a
                    // weapon wakes up (Weapon's Awake sets it), which a prefab
                    // never does, so it's always null here; the prefab item is
                    // the Weapon itself.
                    var weapon = item as Weapon;
                    if (weapon == null || !(bool)_fullAutoField.GetValue(weapon)) continue;

                    var barrels = GetBarrels(weapon);
                    if (barrels == null || barrels.Count == 0 || barrels[0] == null) continue;

                    found.Add(item);
                }

                // Item table not loaded yet; leave the cache empty and retry.
                if (found.Count == 0) return;
                _templateCandidates = found;

                // Prefer something that reads as an SMG. Failing that, take the
                // full-auto gun with the lightest recoil rather than whichever
                // happens to be first, which could be a heavy machine gun.
                int preferred = _templateCandidates.FindIndex(i =>
                {
                    string label = (i.GetName() ?? "") + " " + i.name;
                    return new[] { "冲锋", "smg", "uzi", "submachine" }
                        .Any(k => label.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                });
                if (preferred < 0)
                {
                    preferred = _templateCandidates
                        .Select((item, idx) => (idx, kick: KickMagnitude(ReadRecoil(GetBarrels(item as Weapon)[0]))))
                        .OrderBy(t => t.kick)
                        .First().idx;
                }

                SelectTemplate(preferred);
                Logger.LogInfo("Full-auto recoil templates: " +
                    string.Join(", ", _templateCandidates.Select(i => $"{i.GetName()} [{i.name}]")) +
                    $"; using {_templateCandidates[preferred].GetName()}");
            }
            catch (Exception e)
            {
                _templateCandidates = null;
                Logger.LogWarning("Could not build recoil template list: " + e.Message);
            }
        }

        private void SelectTemplate(int index)
        {
            _templateIndex = index;
            _templateRecoil = null;
            _templateKnockback = 0;
            _templateTimeBetweenShots = 0f;
            if (index < 0 || index >= _templateCandidates.Count) return;

            // The prefab's first barrel is the weapon's stock configuration.
            var weapon = _templateCandidates[index] as Weapon;
            _templateRecoil = ReadRecoil(GetBarrels(weapon)[0]);
            _templateKnockback = (int)_knockbackField.GetValue(weapon);
            _templateTimeBetweenShots = (float)_timeBetweenShotsField.GetValue(weapon);
        }

        // Rough per-shot recoil size from a barrel's kick fields, for ranking.
        private static float KickMagnitude(object[] recoil)
        {
            float total = 0f;
            for (int i = 0; i < RecoilFieldNames.Length; i++)
            {
                if (!KickFieldNames.Contains(RecoilFieldNames[i])) continue;
                switch (recoil[i])
                {
                    case float f: total += Mathf.Abs(f); break;
                    case Vector2 v2: total += v2.magnitude; break;
                    case Vector3 v3: total += v3.magnitude; break;
                }
            }
            return total;
        }

        private static object ScaleRecoilValue(object value, float scale)
        {
            switch (value)
            {
                case float f: return f * scale;
                case Vector2 v2: return v2 * scale;
                case Vector3 v3: return v3 * scale;
                default: return value;
            }
        }

        // ---- Apply / restore ------------------------------------------------
        private void ApplyWeaponTweaks(Player player)
        {
            if (!EnsureWeaponFields()) return;
            EnsureTemplateCandidates();

            var held = player.Holding != null ? player.Holding.HeldItem : null;
            var weapon = held != null ? held.Weapon : null;
            if (weapon == null) return;

            if (!_weaponBackups.TryGetValue(weapon, out var backup))
            {
                backup = CaptureBackup(weapon);
                _weaponBackups[weapon] = backup;
            }

            float rate = _fireRateOn ? Mathf.Max(0.05f, _fireRateMultiplier) : 1f;
            string signature = $"{_shotgunSpread}|{_forceFullAuto}|{rate}|{_templateIndex}";
            if (backup.AppliedSignature == signature) return;
            backup.AppliedSignature = signature;

            bool isShotgun = backup.ProjectileCount > 1;
            bool converted = _forceFullAuto && !backup.FullAuto;

            float spread = backup.Spread;
            if (_shotgunSpread && isShotgun) spread *= ShotgunSpreadScale;
            _spreadField.SetValue(weapon, spread);

            // Clearing NoShootDuringAnim is what keeps a bolt-action's scope up:
            // HandleAiming drops ADS while the fire animation plays, and Shoot
            // calls CancelToggledAim, both gated on this flag.
            _fullAutoField.SetValue(weapon, _forceFullAuto || backup.FullAuto);
            _noShootAnimField.SetValue(weapon, !_forceFullAuto && backup.NoShootDuringAnim);

            float shotDelay = Mathf.Max(MinTimeBetweenShots, backup.TimeBetweenShots / rate);
            _timeBetweenShotsField.SetValue(weapon, shotDelay);

            // Every shot does _anim.Stop() + _anim.Play("Fire"). When the clip
            // (a bolt or pump cycle) is longer than the shot interval, each shot
            // snaps it back to frame 0 mid-cycle. Speed the clip up so it
            // finishes inside one interval.
            var fireAnim = _animField.GetValue(weapon) as Animation;
            if (fireAnim != null)
            {
                bool faster = _forceFullAuto || _fireRateOn;
                foreach (var speed in backup.FireAnimSpeeds)
                {
                    var state = fireAnim[speed.Key];
                    if (state == null) continue;
                    state.speed = faster ? Mathf.Max(speed.Value, state.length / shotDelay) : speed.Value;
                }
            }

            // Converted guns push the player like the template does, except
            // shotguns, which don't push at all.
            int knockback = backup.RecoilKnockback;
            if (converted) knockback = isShotgun ? 0 : _templateKnockback;
            _knockbackField.SetValue(weapon, knockback);

            bool useTemplate = converted && _templateRecoil != null;
            foreach (var barrel in backup.Barrels)
            {
                if (barrel.Key == null) continue;
                var source = useTemplate ? _templateRecoil : barrel.Value;
                for (int i = 0; i < _recoilFields.Length; i++)
                {
                    object value = source[i];
                    // Shots per second go up by `rate`, so each kick shrinks by
                    // the same factor: recoil per second stays what the gun was
                    // tuned for instead of piling up faster than the springs
                    // recover.
                    if (rate != 1f && KickFieldNames.Contains(RecoilFieldNames[i]))
                    {
                        value = ScaleRecoilValue(value, 1f / rate);
                    }
                    _recoilFields[i].SetValue(barrel.Key, value);
                }
            }

            // Spring stiffness/damping are pushed into a ConfigurableJoint here,
            // so field changes don't take effect until this runs. Only called on
            // change: rebuilding the joint every frame causes jitter itself.
            weapon.SetRecoilSprings();

            string templateName = useTemplate ? _templateCandidates[_templateIndex].GetName() : "(own)";
            var active = weapon.Attachments;
            Logger.LogInfo($"Weapon tuned: {held.GetName()} converted={converted} template={templateName} " +
                $"rate={rate} shotDelay={shotDelay:0.###}s screenRecoil={active.ScreenRecoilAmount} " +
                $"modelRot={active.ModelRecoilRot} modelPos={active.ModelRecoilPos} knockback={knockback}");
        }

        private WeaponBackup CaptureBackup(Weapon weapon)
        {
            var barrels = GetBarrels(weapon) ?? new List<BarrelAttachment>();
            var backup = new WeaponBackup
            {
                Spread = (float)_spreadField.GetValue(weapon),
                FullAuto = (bool)_fullAutoField.GetValue(weapon),
                NoShootDuringAnim = (bool)_noShootAnimField.GetValue(weapon),
                TimeBetweenShots = (float)_timeBetweenShotsField.GetValue(weapon),
                ProjectileCount = (int)_projCountField.GetValue(weapon),
                RecoilKnockback = (int)_knockbackField.GetValue(weapon),
                // Every barrel, not just the fitted one, so swapping attachments
                // mid-session keeps the tuning.
                Barrels = barrels.Where(b => b != null)
                    .Select(b => new KeyValuePair<BarrelAttachment, object[]>(b, ReadRecoil(b)))
                    .ToList(),
                FireAnimSpeeds = new Dictionary<string, float>(),
            };

            var anim = _animField.GetValue(weapon) as Animation;
            if (anim != null)
            {
                foreach (var name in FireAnimNames)
                {
                    var state = anim[name];
                    if (state != null) backup.FireAnimSpeeds[name] = state.speed;
                }
            }
            return backup;
        }

        private void RestoreWeapons()
        {
            if (_spreadField == null || _weaponBackups.Count == 0) return;

            foreach (var kv in _weaponBackups)
            {
                // The weapon item may have been destroyed since.
                if (kv.Key == null) continue;
                var weapon = kv.Key;
                var backup = kv.Value;

                _spreadField.SetValue(weapon, backup.Spread);
                _fullAutoField.SetValue(weapon, backup.FullAuto);
                _noShootAnimField.SetValue(weapon, backup.NoShootDuringAnim);
                _timeBetweenShotsField.SetValue(weapon, backup.TimeBetweenShots);
                _knockbackField.SetValue(weapon, backup.RecoilKnockback);

                foreach (var barrel in backup.Barrels)
                {
                    if (barrel.Key == null) continue;
                    for (int i = 0; i < _recoilFields.Length; i++)
                    {
                        _recoilFields[i].SetValue(barrel.Key, barrel.Value[i]);
                    }
                }

                var anim = _animField.GetValue(weapon) as Animation;
                if (anim != null)
                {
                    foreach (var speed in backup.FireAnimSpeeds)
                    {
                        var state = anim[speed.Key];
                        if (state != null) state.speed = speed.Value;
                    }
                }

                try { weapon.SetRecoilSprings(); }
                catch { /* not held any more; springs are rebuilt on next equip */ }
            }
            _weaponBackups.Clear();
        }
    }
}
