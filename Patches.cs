// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HowToFishTrainer
{
    // Harmony patch bodies. Every one checks its own feature flag, since all of
    // them are installed together whenever any feature needs patching.
    internal static class Patches
    {
        // ---- Aim assist -------------------------------------------------------
        private static FieldInfo _aimAssistPlayerField;

        // Lifts the "Controller only" restriction on the built-in aim assist.
        public static void CanUseAimAssistPostfix(PlayerAimAssist __instance, ref bool __result)
        {
            if (!Plugin.AimAssistBoost || __result) return;

            // Replicate the original checks minus the control-scheme test, so
            // the assist isn't forced on in menus or with no player alive.
            if (_aimAssistPlayerField == null)
            {
                _aimAssistPlayerField = AccessTools.Field(typeof(PlayerAimAssist), "_player");
                if (_aimAssistPlayerField == null) return;
            }

            var player = _aimAssistPlayerField.GetValue(__instance) as Player;
            if (player == null || player.BlockInputs) return;
            if (player.Camera == null || !player.Camera.MouseLocked) return;
            if (GameInfo.Input == null) return;

            __result = true;
        }

        // Aim assist normally only runs while aiming down sights.
        public static void IsAdsPostfix(ref bool __result)
        {
            if (!Plugin.AimAssistBoost || !Plugin.HipFireAssist || __result) return;

            var player = Player.LocalPlayer;
            if (player == null || player.Holding == null) return;

            var held = player.Holding.HeldItem;
            if (held == null || held.Weapon == null) return;

            __result = true;
        }

        // GetRotationDelta scales the assist by (1 - manualLookAmount), so any
        // mouse movement while aiming fades it out. Pretend there was none.
        public static void GetRotationDeltaPrefix(ref float manualLookAmount)
        {
            if (Plugin.AimAssistBoost) manualLookAmount = 0f;
        }

        // ---- Head targeting ---------------------------------------------------
        // The game scores a ranged hit as a headshot when the hit point's local z
        // exceeds Creature.HeadPos, i.e. the head is the forward end of the body.
        // Find the snout by raycasting back along the creature's forward axis
        // against its own colliders, and aim midway into that region.
        private static readonly Dictionary<Creature, Collider[]> _bodyColliders = new Dictionary<Creature, Collider[]>();

        private static Collider[] GetBodyColliders(Creature creature)
        {
            if (_bodyColliders.TryGetValue(creature, out var cached) && cached != null) return cached;

            // Triggers (detection spheres and the like) aren't the body.
            var colliders = creature.GetComponentsInChildren<Collider>()
                .Where(c => c != null && !c.isTrigger)
                .ToArray();

            if (_bodyColliders.Count > 256) _bodyColliders.Clear();
            _bodyColliders[creature] = colliders;
            return colliders;
        }

        private static bool TryGetHeadPoint(Creature creature, out Vector3 head)
        {
            head = default;
            var t = creature.transform;
            var colliders = GetBodyColliders(creature);
            if (colliders.Length == 0) return false;

            Vector3 forward = t.forward;
            var ray = new Ray(t.TransformPoint(0f, 0f, creature.HeadPos) + forward * 50f, -forward);

            bool found = false;
            float nearest = float.MaxValue;
            Vector3 snout = default;
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;
                if (col.Raycast(ray, out var hit, 100f) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    snout = hit.point;
                    found = true;
                }
            }
            if (!found) return false;

            float snoutZ = t.InverseTransformPoint(snout).z;
            if (snoutZ <= creature.HeadPos) return false;

            head = t.TransformPoint(0f, 0f, (creature.HeadPos + snoutZ) * 0.5f);
            return true;
        }

        // Used by the aim assist for both acquiring and tracking, and by the
        // magic bullet through the same method, so all of them go for the head.
        public static bool GetTargetPositionPrefix(Creature creature, ref Vector3 __result)
        {
            if (!Plugin.AimAssistBoost || !Plugin.AimAtHead || creature == null) return true;

            // FindBestTarget calls this for every creature in the world; skip the
            // raycasts for ones the assist would reject on range anyway.
            var cam = GameInfo.CurCamera;
            float skipRange = Plugin.AimRange + 50f;
            if (cam != null && (creature.transform.position - cam.transform.position).sqrMagnitude > skipRange * skipRange)
            {
                return true;
            }

            try
            {
                if (TryGetHeadPoint(creature, out Vector3 head))
                {
                    __result = head;
                    return false;
                }
            }
            catch { /* fall back to centre of mass */ }
            return true;
        }

        // ---- Vitals / movement / damage / casino -----------------------------
        // Damage is applied on the host, so this also covers other players who
        // asked the host to lock their health (Plugin.RemoteLockHealth).
        public static bool TakeDamagePrefix(PlayerVitals __instance)
        {
            var local = Player.LocalPlayer;
            if (Plugin.LockHealth && local != null && local.Vitals == __instance) return false;

            if (Plugin.RemoteLockHealth.Count > 0)
            {
                foreach (var player in PlayerManager.Players)
                {
                    if (player != null && player.Vitals == __instance && Plugin.RemoteLockHealth.Contains(player.SteamID))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // Player sends its position and camera angles through here every tick;
        // other players turn your body to the yaw in `rot` and tilt your head
        // and arms to the pitch.
        public static void UpdatePlayerPosRotPrefix(Player player, ref Vector2 rot)
        {
            if (!Plugin.SpinBot || player == null || player != Player.LocalPlayer) return;
            rot.x = Plugin.ReportedPitch(rot.x);
            rot.y = Plugin.ReportedYaw(rot.y);
        }

        public static bool JumpInputPrefix(PlayerMovement __instance)
        {
            if (!Plugin.InfiniteJump) return true;

            var player = Player.LocalPlayer;
            if (player == null || player.Movement != __instance || player.BlockInputs) return true;

            __instance.Jump();
            return false;
        }

        // Every melee / projectile / thrown-item hit on a creature funnels
        // through Creature.LocalHit, so the multiplier only needs to go here.
        // Capped well below int.MaxValue: a large multiplier (or the kill-all
        // damage) would otherwise wrap to a negative number, and LocalHit still
        // multiplies headshots by GameInfo.HeadShotDamageMulti after this.
        public const float MaxScaledDamage = 100_000_000f;

        public static void LocalHitPrefix(Player player, ref int damage)
        {
            if (!Plugin.DamageMultiplierOn) return;
            if (player == null || player != Player.LocalPlayer) return;

            damage = Mathf.RoundToInt(Mathf.Clamp(damage * Plugin.DamageMultiplier, 1f, MaxScaledDamage));
        }

        private static FieldInfo _curBetColorField;

        // The roulette result comes from where the ball physically lands.
        // Overriding it with the colour the player bet on makes the bet win.
        public static void RoulettePrefix(ref BetColor winColor)
        {
            if (!Plugin.RouletteAlwaysWin && !Plugin.RemoteRouletteWin) return;

            if (_curBetColorField == null)
            {
                _curBetColorField = AccessTools.Field(typeof(CasinoManager), "_curBetColor");
                if (_curBetColorField == null) return;
            }

            winColor = (BetColor)_curBetColorField.GetValue(null);
        }

        // ---- Guns ---------------------------------------------------------------
        private const float SniperShotInterval = 0.05f;
        private static readonly Dictionary<Weapon, float> _lastShotTime = new Dictionary<Weapon, float>();

        // ShootEffects(fromLocal: true) is only reached once Shoot has passed
        // all its checks, so it marks a shot that actually went off.
        public static void RecordShotPrefix(Weapon __instance, bool fromLocal)
        {
            if (!fromLocal || __instance == null) return;
            if (_lastShotTime.Count > 256) _lastShotTime.Clear();
            _lastShotTime[__instance] = Time.time;
        }

        // Replaces the sniper rifle's own cooldown (shot timer and the bolt
        // animation lock) with a flat 50 ms between shots.
        public static void HasCooldownPostfix(Weapon __instance, ref bool __result)
        {
            if (!Plugin.SniperNoCooldown || !IsSniperRifle(__instance)) return;

            __result = _lastShotTime.TryGetValue(__instance, out float last)
                       && Time.time - last < SniperShotInterval;
        }

        private static readonly Dictionary<Weapon, bool> _sniperRifles = new Dictionary<Weapon, bool>();

        // Nothing in the weapon data says "sniper rifle": UseSniperUi describes
        // the sight currently fitted (any gun can mount a scope) and every gun
        // ships with iron sights. The prefab is named "Sniper Rifle" in the game
        // assets, so go by name, with the localised name as a fallback.
        private static bool IsSniperRifle(Weapon weapon)
        {
            if (weapon == null) return false;
            if (_sniperRifles.TryGetValue(weapon, out bool cached)) return cached;

            var item = weapon.GetComponent<Item>();
            string displayName = "";
            try { displayName = item != null ? item.GetName() ?? "" : ""; }
            catch { /* localisation not ready; the object name still works */ }

            string objectName = item != null ? item.name : weapon.name;
            bool isSniper = objectName.IndexOf("sniper", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || displayName.Contains("狙击");

            if (_sniperRifles.Count > 256) _sniperRifles.Clear();
            _sniperRifles[weapon] = isSniper;

            Plugin.Log?.LogInfo($"Sniper rifle check: {objectName} / {displayName} = {isSniper}");
            return isSniper;
        }

        private static FieldInfo _attachSightsField;
        private static FieldInfo _attachBarrelsField;
        private static FieldInfo _attachLaserField;
        private static FieldInfo _attachExtMagInfoField;

        // Game bug: HasAttachment also reports the barrel listed just before the
        // fitted one as owned (the `index - 1` check), so after swapping to a
        // different muzzle the previous one shows "already bought" and can't be
        // bought back. Recompute ownership from what is actually fitted.
        public static void HasAttachmentPostfix(Attachments __instance, AttachmentInfo info, ref bool __result)
        {
            if (!Plugin.MuzzleFixActive || !__result || info == null) return;

            try
            {
                if (_attachSightsField == null)
                {
                    _attachSightsField = AccessTools.Field(typeof(Attachments), "_sights");
                    _attachBarrelsField = AccessTools.Field(typeof(Attachments), "_barrelAttachments");
                    _attachLaserField = AccessTools.Field(typeof(Attachments), "_laserSight");
                    _attachExtMagInfoField = AccessTools.Field(typeof(Attachments), "_extendedMagInfo");
                }

                var sights = _attachSightsField.GetValue(__instance) as List<Sight>;
                var barrels = _attachBarrelsField.GetValue(__instance) as List<BarrelAttachment>;
                var laser = _attachLaserField.GetValue(__instance) as LaserSight;
                var extMagInfo = _attachExtMagInfoField.GetValue(__instance) as AttachmentInfo;

                __result =
                    (sights != null && sights[__instance._syncedSight.Value].Info == info)
                    || (barrels != null && barrels[__instance._syncedBarrelAttachment.Value].Info == info)
                    || (laser != null && laser.Info == info && __instance._syncedLaserSight.Value)
                    || (extMagInfo == info && __instance._syncedExtendedMag.Value);
            }
            catch { /* keep the game's answer */ }
        }

        public static void RecoilPrefix(ref Vector2 recoil)
        {
            if (Plugin.RecoilReductionOn) recoil *= Plugin.RecoilMultiplier;
        }

        // Every shot teleports a jointed recoil rig (AddPos/AddRotToRecoilRig)
        // and offsets the tool's look rotation; the joint spring then pulls it
        // back. This is the gun model's shake, separate from camera recoil.
        public static void ToolRecoilPrefix(ref Vector2 recoil)
        {
            if (Plugin.ModelShakeReductionOn) recoil *= Plugin.CurrentModelShakeMultiplier;
        }

        public static void RecoilRigPosPrefix(ref Vector3 posForce)
        {
            if (Plugin.ModelShakeReductionOn) posForce *= Plugin.CurrentModelShakeMultiplier;
        }

        public static void RecoilRigRotPrefix(ref Vector3 rotForce)
        {
            if (Plugin.ModelShakeReductionOn) rotForce *= Plugin.CurrentModelShakeMultiplier;
        }

        // ---- Magic bullet -------------------------------------------------------
        // Redirects the projectile at whatever the aim assist has locked on.
        private static FieldInfo _aimTargetField;
        private static MethodInfo _targetPosMethod;

        // Homing asks for the target once per projectile per physics step, and
        // the head lookup raycasts, so the answer is cached per step.
        private static int _targetCacheFrame = -1;
        private static float _targetCacheFixedTime = -1f;
        private static bool _targetCacheValid;
        private static Vector3 _targetCachePos;

        private static bool TryGetLockedTargetPosition(out Vector3 pos)
        {
            if (_targetCacheFrame != Time.frameCount || _targetCacheFixedTime != Time.fixedTime)
            {
                _targetCacheFrame = Time.frameCount;
                _targetCacheFixedTime = Time.fixedTime;
                _targetCacheValid = ComputeLockedTargetPosition(out _targetCachePos);
            }
            pos = _targetCachePos;
            return _targetCacheValid;
        }

        private static bool ComputeLockedTargetPosition(out Vector3 pos)
        {
            pos = default;

            var player = Player.LocalPlayer;
            if (player == null) return false;

            var assist = player.GetComponent<PlayerAimAssist>();
            if (assist == null) return false;

            if (_aimTargetField == null)
            {
                _aimTargetField = AccessTools.Field(typeof(PlayerAimAssist), "_target");
                _targetPosMethod = AccessTools.Method(typeof(PlayerAimAssist), "GetTargetPosition");
                if (_aimTargetField == null || _targetPosMethod == null) return false;
            }

            var target = _aimTargetField.GetValue(assist) as Creature;
            if (target == null || target.IsDead) return false;

            pos = (Vector3)_targetPosMethod.Invoke(null, new object[] { target });
            return true;
        }

        private static bool TryGetLockedTargetDirection(Vector3 from, out Vector3 dir)
        {
            dir = Vector3.zero;
            if (!TryGetLockedTargetPosition(out Vector3 pos)) return false;

            Vector3 delta = pos - from;
            if (delta.sqrMagnitude < 0.0001f) return false;

            dir = delta.normalized;
            return true;
        }

        private static bool MagicBulletActive(Player owner, Vector3 pos)
        {
            if (!Plugin.MagicBullet || !Plugin.AimAssistBoost) return false;
            if (owner == null || owner != Player.LocalPlayer) return false;

            // Weapon.Shoot parks the projectile far below the world when the
            // muzzle is clipping something; don't redirect those.
            return pos.y > -5000f;
        }

        public static void AddProjectilePrefix(Player owner, Vector3 pos, ref Vector3 velocity, bool fromNpc)
        {
            // Albatross droppings and whale lava are spawned with the local
            // player as owner and fromNpc set; without this they'd be steered at
            // the locked target too.
            if (fromNpc) return;
            if (!MagicBulletActive(owner, pos)) return;
            if (!TryGetLockedTargetDirection(pos, out Vector3 dir)) return;

            velocity = dir * (velocity.magnitude * Plugin.MagicBulletSpeed);
        }

        public static void AddProjectilesPrefix(Player owner, Vector3 pos, Vector3[] velocities)
        {
            if (velocities == null || !MagicBulletActive(owner, pos)) return;
            if (!TryGetLockedTargetDirection(pos, out Vector3 dir)) return;

            for (int i = 0; i < velocities.Length; i++)
            {
                velocities[i] = dir * (velocities[i].magnitude * Plugin.MagicBulletSpeed);
            }
        }

        // Runs every physics step, before the projectile's hit scan.
        public static bool UpdateProjectileScanPrefix(ProjectileManager __instance, Projectile projectile, ProjectileType type)
        {
            if (!IsLocalPlayerProjectile(projectile)) return true;

            // Aiming at the target when fired still misses slow bullets: the
            // target moves during the flight and gravity pulls the bullet down.
            // Turning it back onto the target and removing its drop every step
            // makes it home in; UpdateProjectilePos then moves it along the
            // corrected velocity.
            if (MagicBulletActive(projectile.Owner, projectile.Position)
                && TryGetLockedTargetDirection(projectile.Position, out Vector3 dir))
            {
                projectile.Velocity = dir * projectile.Velocity.magnitude;
                projectile.GravityForce = 0f;
            }

            if (!Plugin.WaterPenetration) return true;
            ScanWithoutWaterStop(__instance, projectile, type);
            return false;
        }

        private static FieldInfo _sqrMaxRangeField;
        private static MethodInfo _addToRemoveQueueMethod;
        private static MethodInfo _hitWaterMethod;
        private static float _sqrMaxRange;

        // The original scan removes a projectile the moment it drops half a
        // metre below the water surface, before its hit check, so nothing
        // underwater can ever be shot. This is the same scan without that stop:
        // it still splashes on the way in, then keeps going.
        private static void ScanWithoutWaterStop(ProjectileManager manager, Projectile projectile, ProjectileType type)
        {
            if (_addToRemoveQueueMethod == null)
            {
                _hitMethod = AccessTools.Method(typeof(ProjectileManager), "Hit");
                _addToRemoveQueueMethod = AccessTools.Method(typeof(ProjectileManager), "AddToRemoveQueue");
                _hitWaterMethod = AccessTools.Method(typeof(ProjectileManager), "HitWater");
                _sqrMaxRangeField = AccessTools.Field(typeof(ProjectileManager), "_sqrMaxProjRange");
            }
            if (_sqrMaxRange <= 0f) _sqrMaxRange = (float)_sqrMaxRangeField.GetValue(manager);

            float waterLine = WaterManager.WaterHeight - 0.5f;
            if (projectile.Owner == null
                || (projectile.Position - projectile.Owner.Transform.position).sqrMagnitude > _sqrMaxRange)
            {
                _addToRemoveQueueMethod.Invoke(manager, new object[] { projectile });
            }
            else if (projectile.PreviousPosition.y >= waterLine && projectile.Position.y < waterLine)
            {
                _hitWaterMethod.Invoke(manager, new object[] { projectile });
            }

            if (Physics.SphereCast(projectile.Position, type.WidthRadius, projectile.Velocity, out var hit,
                StepDistance(projectile), GameInfo.ProjectileHitLayer))
            {
                _hitMethod.Invoke(manager, new object[] { projectile, type, hit });
            }
        }

        // ---- Through walls -------------------------------------------------------
        public static bool IsTargetObstructedPrefix(ref bool __result)
        {
            if (!Plugin.AimThroughWalls || !Plugin.AimAssistBoost) return true;
            __result = false;
            return false;
        }

        private static bool _inHitScan;
        private static MethodInfo _hitMethod;
        private static FieldInfo _catchUpSpeedField;

        // Instant-hit projectile types resolve through HitScan, which casts with
        // unlimited range; the penetration lookup has to match it.
        public static void HitScanPrefix() { _inHitScan = true; }
        public static void HitScanPostfix() { _inHitScan = false; }

        // Terrain, the boat, props: anything a projectile can hit that isn't a
        // creature/item, a player or an NPC.
        private static bool IsWallHit(RaycastHit hit)
        {
            if (hit.transform == null) return false;
            if (ItemManager.Get(hit.collider) != null) return false;
            if (PlayerManager.GetPlayerFromBodyPart(hit.transform) != null) return false;
            return !hit.transform.CompareTag("NPC");
        }

        // Albatross droppings and whale lava are local, owned by the local
        // player and flagged FromNpc; they aren't the player's shots.
        private static bool IsLocalPlayerProjectile(Projectile projectile)
        {
            return projectile != null && projectile.IsLocal && !projectile.FromNpc
                   && projectile.Owner != null && projectile.Owner == Player.LocalPlayer;
        }

        private static bool Penetrates(Projectile projectile)
        {
            return Plugin.WallPenetration && IsLocalPlayerProjectile(projectile);
        }

        // Same distance UpdateProjectileScan casts for this step.
        private static float StepDistance(Projectile projectile)
        {
            if (_catchUpSpeedField == null) _catchUpSpeedField = AccessTools.Field(typeof(ProjectileManager), "_catchUpSpeed");

            float catchUp = 0f;
            if (_catchUpSpeedField != null && ProjectileManager.Instance != null)
            {
                catchUp = (float)_catchUpSpeedField.GetValue(ProjectileManager.Instance);
            }
            return projectile.Velocity.magnitude * (1f + projectile.CatchingUpToDo * catchUp) * Time.fixedDeltaTime;
        }

        // A projectile's scan only reports the first thing it touches. When
        // that's a wall, ignore it (the projectile isn't removed and keeps
        // flying) and look further along the same scan for something that
        // isn't a wall, so a creature just behind it is still hit this step.
        public static bool HitPrefix(ProjectileManager __instance, Projectile projectile, ProjectileType type, RaycastHit hit)
        {
            if (!Penetrates(projectile) || !IsWallHit(hit)) return true;

            float distance = _inHitScan ? float.PositiveInfinity : StepDistance(projectile);
            RaycastHit[] hits = Physics.SphereCastAll(projectile.Position, type.WidthRadius, projectile.Velocity,
                distance, GameInfo.ProjectileHitLayer);

            bool found = false;
            RaycastHit nearest = default;
            foreach (var candidate in hits)
            {
                if (IsWallHit(candidate)) continue;
                if (!found || candidate.distance < nearest.distance)
                {
                    nearest = candidate;
                    found = true;
                }
            }

            if (found)
            {
                // Goes back through this prefix, which lets a non-wall hit through.
                if (_hitMethod == null) _hitMethod = AccessTools.Method(typeof(ProjectileManager), "Hit");
                _hitMethod.Invoke(__instance, new object[] { projectile, type, nearest });
            }
            return false;
        }

        // With the muzzle inside a wall, Shoot resolves the shot on whatever the
        // clipping ray touched and throws the projectile away; for a wall that
        // means the shot does nothing. Report no hit so the projectile is fired.
        public static void GunClippingRayPostfix(Weapon __instance, ref RaycastHit __result)
        {
            if (!Plugin.WallPenetration || __result.transform == null) return;
            if (!IsWallHit(__result)) return;

            var player = Player.LocalPlayer;
            if (player == null || player.Holding == null || player.Holding.HeldItem == null) return;
            if (player.Holding.HeldItem.Weapon != __instance) return;

            __result = default;
        }

        // ---- Fire audio rate limit ---------------------------------------------
        // Local fire sounds all go through one AudioSource.PlayOneShot, and the
        // game's per-clip cooldown shrinks with the shot interval. At raised
        // fire rates that stacks long gunshot tails until the voice limit is hit
        // and clips start stealing each other. Weapon.ShootEffects is bracketed
        // so the sound calls it makes can be told apart from every other sound
        // in the game and dropped when they come too fast.
        private static bool _inFireEffects;
        private static bool _suppressFireAudio;
        private static float _lastFireAudioTime = -999f;

        public static void ShootEffectsPrefix()
        {
            _inFireEffects = true;
            _suppressFireAudio = false;

            float interval = Plugin.FireAudioInterval;
            if (interval <= 0f) return;

            if (Time.time - _lastFireAudioTime < interval) _suppressFireAudio = true;
            else _lastFireAudioTime = Time.time;
        }

        public static void ShootEffectsPostfix()
        {
            ResetFireEffectsState();
        }

        public static void ResetFireEffectsState()
        {
            _inFireEffects = false;
            _suppressFireAudio = false;
        }

        // Shared by the fire sound and the bolt/pump sound sequence (both its
        // start and the cancel of the previous one), so a dropped shot keeps the
        // previous shot's sequence playing instead of cutting it off.
        public static bool FireAudioGatePrefix()
        {
            return !(_inFireEffects && _suppressFireAudio);
        }
    }
}
