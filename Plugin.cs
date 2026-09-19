// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace HowToFishTrainer
{
    // ---------------------------------------------------------------------
    // How to Fish - single-player trainer
    //
    // SAFETY DESIGN:
    // The game uses FishNet for co-op (P2P over Steam). Money, casino payouts
    // and unlocked content are server-authoritative and shared by everyone in
    // the session. Every feature is hard-gated behind the check at the end of
    // this file: it only opens in a solo session or in a room where every
    // player has this mod and has opted in themselves, re-checks every second,
    // and force-disables the game's cheat flag and every trainer feature as
    // soon as that stops being true.
    //
    // Where the developers shipped a debug hook (cheat chat commands,
    // ClientSettings.CheatsEnabled, SlotMachineManager.SetCheatSkin) the
    // trainer reuses it. Harmony patches are applied on demand rather than at
    // load, because patching during BepInEx chainload froze this game on boot.
    // ---------------------------------------------------------------------

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class Plugin : BaseUnityPlugin
    {
        private const string PluginGuid = "local.howtofish.trainer";
        private const string PluginName = "How To Fish Trainer";
        private const string PluginVersion = "1.4.1";

        public static Plugin Instance;
        internal static ManualLogSource Log;

        private Harmony _harmony;

        private bool _menuVisible;
        private bool _wantCheatsEnabled;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            InitThirdPerson();
            InitRemote();
            Logger.LogInfo("How To Fish Trainer loaded. F1 = menu, Alt = cursor.");
        }

        private void OnDestroy()
        {
            ShutdownRemote();
            ShutdownThirdPerson();
            ForceDisableEverything();
            SetPatches(false);
            if (_cursorFreed) SetCursorFreed(false);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) _menuVisible = !_menuVisible;

            if (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt))
            {
                SetCursorFreed(!_cursorFreed);
            }

            // The game re-locks the cursor on every left click (PlayerCamera
            // .MouseClick), so holding it free means re-asserting it.
            if (_cursorFreed) ApplyFreedCursor();

            // Must run before anything below it; see the end of this file.
            EnforceGate();

            if (!_wantCheatsEnabled) DisableAllFeatureFlags();

            MuzzleFixActive = _gateOpen && _wantCheatsEnabled && _fixMuzzleBug;

            if (_gateOpen && _wantCheatsEnabled && Input.GetKeyDown(KeyCode.PageUp)) DoKillAll();
            if (_gateOpen && _wantCheatsEnabled && Input.GetKeyDown(KeyCode.PageDown)) RequestClearGroundItems();

            // Hold-to-aim gates only whether the assist engages this frame.
            // The patches and tuning overrides stay installed while the master
            // switch is on, so tapping the button isn't re-patching.
            AimAssistBoost = _aimEnabled && (!_aimHoldMode || Input.GetMouseButton(_aimHoldButton));

            // Fire sounds are capped at twice the SMG's rate of fire whenever
            // guns can shoot faster than they were designed to.
            FireAudioInterval = 0f;
            if (_forceFullAuto || _fireRateOn)
            {
                EnsureTemplateCandidates();
                float smgInterval = _templateTimeBetweenShots > 0f ? _templateTimeBetweenShots : 0.1f;
                FireAudioInterval = smgInterval / 2f;
            }

            CurrentModelShakeMultiplier = HeldWeaponIsNativeFullAuto() ? _modelShakeAuto : _modelShakeSemi;

            // Shoot effects never span frames; clear in case a patched call
            // threw before its postfix could reset the flags.
            Patches.ResetFireEffectsState();

            UpdateRemote();
            SetPatches(AnyPatchFeatureOn());
            ApplyAimTuning(_wantCheatsEnabled && _aimEnabled);
            ApplyVitalsAndGear();
            CollectEspTargets();

            // ApplyVitalsAndGear bails out when the trainer is off, so the
            // weapon restore has to happen outside it.
            if (!AnyWeaponTweakOn()) RestoreWeapons();
        }

        private void LateUpdate()
        {
            if (_cursorFreed) ApplyFreedCursor();
            UpdateThirdPerson();
        }

        private bool AnyPatchFeatureOn()
        {
            return _aimEnabled || LockHealth || InfiniteJump || RouletteAlwaysWin
                   || DamageMultiplierOn || MagicBullet || WallPenetration || WaterPenetration || SniperNoCooldown
                   || RecoilReductionOn || ModelShakeReductionOn || FireAudioInterval > 0f
                   || MuzzleFixActive || SpinBot
                   // Other players' toggles the host carries out for them.
                   || RemoteLockHealth.Count > 0 || RemoteRouletteWin;
        }

        // Every on/off feature switch in the menu, so they can be cleared,
        // remembered and brought back together.
        private (Func<bool> get, Action<bool> set)[] _featureFlags;

        private (Func<bool> get, Action<bool> set)[] FeatureFlags => _featureFlags ?? (_featureFlags = new (Func<bool>, Action<bool>)[]
        {
            (() => _aimEnabled, v => _aimEnabled = v),
            (() => HipFireAssist, v => HipFireAssist = v),
            (() => LockHealth, v => LockHealth = v),
            (() => InfiniteJump, v => InfiniteJump = v),
            (() => RouletteAlwaysWin, v => RouletteAlwaysWin = v),
            (() => DamageMultiplierOn, v => DamageMultiplierOn = v),
            (() => MagicBullet, v => MagicBullet = v),
            (() => WallPenetration, v => WallPenetration = v),
            (() => WaterPenetration, v => WaterPenetration = v),
            (() => IgnoreBossImmortality, v => IgnoreBossImmortality = v),
            (() => SpinBot, v => SpinBot = v),
            (() => _thirdPerson, v => _thirdPerson = v),
            (() => SniperNoCooldown, v => SniperNoCooldown = v),
            (() => RecoilReductionOn, v => RecoilReductionOn = v),
            (() => ModelShakeReductionOn, v => ModelShakeReductionOn = v),
            (() => _lockFullness, v => _lockFullness = v),
            (() => _infiniteAmmo, v => _infiniteAmmo = v),
            (() => _shotgunSpread, v => _shotgunSpread = v),
            (() => _forceFullAuto, v => _forceFullAuto = v),
            (() => _fireRateOn, v => _fireRateOn = v),
            (() => _esp, v => _esp = v),
        });

        private void DisableAllFeatureFlags()
        {
            AimAssistBoost = false;
            foreach (var flag in FeatureFlags) flag.set(false);
        }

        private void ForceDisableEverything()
        {
            _wantCheatsEnabled = false;
            DisableAllFeatureFlags();
            _suspendedFlags = null;
            try
            {
                SlotMachineManager.SetCheatSkin(null, byte.MaxValue);
                if (ClientSettings.CheatsEnabled) ClientSettings.ToggleCheats(false);
                if (PlayerManager.InGodMode) PlayerManager.ToggleGodMode();
                RestoreAimTuning();
                RestoreWeapons();
            }
            catch { /* game may already be shut down */ }
        }

        // ---- Harmony patches, applied only while something needs them ------
        private void SetPatches(bool enabled)
        {
            if (enabled == (_harmony != null)) return;

            try
            {
                if (enabled)
                {
                    _harmony = new Harmony(PluginGuid);

                    Patch(typeof(PlayerAimAssist), "CanUseAimAssist", nameof(Patches.CanUseAimAssistPostfix), post: true);
                    Patch(typeof(PlayerAimAssist), "IsAimingDownSights", nameof(Patches.IsAdsPostfix), post: true);
                    Patch(typeof(PlayerAimAssist), "GetRotationDelta", nameof(Patches.GetRotationDeltaPrefix), post: false);
                    Patch(typeof(PlayerAimAssist), "GetTargetPosition", nameof(Patches.GetTargetPositionPrefix), post: false);

                    Patch(typeof(PlayerVitals), "TakeDamage", nameof(Patches.TakeDamagePrefix), post: false);
                    Patch(typeof(Server), "UpdatePlayerPosRot", nameof(Patches.UpdatePlayerPosRotPrefix), post: false);
                    Patch(typeof(PlayerMovement), "JumpInput", nameof(Patches.JumpInputPrefix), post: false);
                    Patch(typeof(Creature), "LocalHit", nameof(Patches.LocalHitPrefix), post: false);
                    Patch(typeof(CasinoManager), "ServerRouletteResult", nameof(Patches.RoulettePrefix), post: false);

                    Patch(typeof(Weapon), "HasCooldown", nameof(Patches.HasCooldownPostfix), post: true);
                    Patch(typeof(Attachments), "HasAttachment", nameof(Patches.HasAttachmentPostfix), post: true);
                    Patch(typeof(Weapon), "ShootEffects", nameof(Patches.RecordShotPrefix), post: false);
                    Patch(typeof(PlayerCamera), "Recoil", nameof(Patches.RecoilPrefix), post: false);
                    Patch(typeof(PlayerToolMovement), "Recoil", nameof(Patches.ToolRecoilPrefix), post: false);
                    Patch(typeof(PlayerToolMovement), "AddPosToRecoilRig", nameof(Patches.RecoilRigPosPrefix), post: false);
                    Patch(typeof(PlayerToolMovement), "AddRotToRecoilRig", nameof(Patches.RecoilRigRotPrefix), post: false);
                    Patch(typeof(ProjectileManager), "AddProjectile", nameof(Patches.AddProjectilePrefix), post: false);
                    Patch(typeof(ProjectileManager), "AddProjectiles", nameof(Patches.AddProjectilesPrefix), post: false);
                    Patch(typeof(ProjectileManager), "UpdateProjectileScan", nameof(Patches.UpdateProjectileScanPrefix), post: false);
                    Patch(typeof(ProjectileManager), "Hit", nameof(Patches.HitPrefix), post: false);
                    Patch(typeof(ProjectileManager), "HitScan", nameof(Patches.HitScanPrefix), post: false);
                    Patch(typeof(ProjectileManager), "HitScan", nameof(Patches.HitScanPostfix), post: true);
                    Patch(typeof(Weapon), "GunClippingRay", nameof(Patches.GunClippingRayPostfix), post: true);
                    Patch(typeof(PlayerAimAssist), "IsTargetObstructed", nameof(Patches.IsTargetObstructedPrefix), post: false);

                    Patch(typeof(Weapon), "ShootEffects", nameof(Patches.ShootEffectsPrefix), post: false);
                    Patch(typeof(Weapon), "ShootEffects", nameof(Patches.ShootEffectsPostfix), post: true);
                    Patch(typeof(AudioManager), "PlayRandomPlayerClip", nameof(Patches.FireAudioGatePrefix), post: false);
                    Patch(typeof(AudioSequenceManager), "CancelAllActiveSequencesFromOwner", nameof(Patches.FireAudioGatePrefix), post: false);
                    Patch(typeof(AudioSequenceManager), "PlayPlayerSequence", nameof(Patches.FireAudioGatePrefix), post: false);

                    Logger.LogInfo("Patches applied.");
                }
                else
                {
                    _harmony.UnpatchSelf();
                    _harmony = null;
                    Logger.LogInfo("Patches removed.");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Patching failed: " + e);
                DisableAllFeatureFlags();
                _harmony = null;
            }
        }

        private void Patch(Type type, string method, string patchName, bool post)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                Logger.LogWarning($"Could not find {type.Name}.{method}, skipping that feature.");
                return;
            }
            var patch = new HarmonyMethod(AccessTools.Method(typeof(Patches), patchName));
            _harmony.Patch(target, prefix: post ? null : patch, postfix: post ? patch : null);
        }

        // =====================================================================
        // Multiplayer gate
        //
        // The trainer only runs where it can't affect anyone who hasn't agreed:
        //  - a solo session (you are the host and nobody else is connected), or
        //  - a room where every player has this mod and has opted in.
        //
        // Opt-in is per player and per room. Each client writes its own flag
        // into its Steam lobby member data, which Steam only lets that member
        // set, so nobody can agree on someone else's behalf. The room only
        // counts as agreed when every lobby member's flag is set and every
        // player actually in the game is one of those members. Anything that
        // can't be verified (no Steam lobby, a connection outside the lobby, a
        // player whose SteamID isn't known yet) keeps the gate closed.
        //
        // EnforceGate is called first thing in Update, before any feature runs,
        // so nothing gets a frame after the room stops qualifying.
        // =====================================================================
        private const string ModMemberKey = "htf_trainer";
        private const string ConsentMemberKey = "htf_trainer_consent";

        private float _nextGateCheckTime;
        private bool _gateOpen;
        private bool _isHost;
        private string _gateStatus = "checking...";

        // This player's own opt-in for the current room. Starts off in every
        // new room.
        private bool _consentOptIn;
        private ulong _consentLobby;
        private string _publishedConsent;
        private string _consentSummary = "";

        // Member data is re-sent periodically, not just on change, in case a
        // write made right after entering the lobby gets dropped.
        private const float RepublishInterval = 5f;
        private float _nextRepublishTime;

        // Diagnostics: what Steam holds for this player, and the raw values
        // the host sees for every member.
        private string _ownMemberDataStatus = "";
        private string _lastMemberDump;

        // The switches that were on when the gate last closed, and the room
        // they were on in. Everything stays off while the gate is closed; once
        // that same room is fully agreed again they are switched back on, so
        // someone joining (who has to tick the consent box first) doesn't cost
        // everyone their setup.
        private bool[] _suspendedFlags;
        private ulong _suspendedLobby;

        private void EnforceGate()
        {
            bool wasOpen = _gateOpen;
            if (Time.unscaledTime >= _nextGateCheckTime)
            {
                _nextGateCheckTime = Time.unscaledTime + 1f;
                RefreshGate();
            }

            if (!_gateOpen)
            {
                if (wasOpen) SuspendFeatures();
                _wantCheatsEnabled = false;
                DisableAllFeatureFlags();
                if (ClientSettings.CheatsEnabled) ClientSettings.ToggleCheats(false);
            }
            else
            {
                if (!wasOpen) ResumeFeatures();
                if (ClientSettings.CheatsEnabled != _wantCheatsEnabled) ClientSettings.ToggleCheats(_wantCheatsEnabled);
            }
        }

        private void SuspendFeatures()
        {
            Logger.LogInfo($"Gate closed: {_gateStatus}. {_consentSummary}");

            // Armed on the host until the next roll; nobody agreed to that roll.
            SlotMachineManager.SetCheatSkin(null, byte.MaxValue);

            ulong lobby = SteamManager.CurrentLobbyID.m_SteamID;
            if (_wantCheatsEnabled && lobby != 0)
            {
                _suspendedFlags = FeatureFlags.Select(flag => flag.get()).ToArray();
                _suspendedLobby = lobby;
                ShowWorldNotice("房间未全员同意，功能已暂停 (全员同意后自动恢复)", 3f);
            }
            else
            {
                _suspendedFlags = null;
            }
        }

        private void ResumeFeatures()
        {
            Logger.LogInfo($"Gate opened: {_gateStatus}. {_consentSummary}");

            bool[] flags = _suspendedFlags;
            _suspendedFlags = null;
            if (flags == null || _suspendedLobby != SteamManager.CurrentLobbyID.m_SteamID) return;

            var all = FeatureFlags;
            for (int i = 0; i < all.Length; i++) all[i].set(flags[i]);
            _wantCheatsEnabled = true;
            ShowWorldNotice("房间重新全员同意，已恢复之前开着的功能", 3f);
            Logger.LogInfo("Restored the switches that were on before the gate closed.");
        }

        private void SetConsent(bool agree)
        {
            Logger.LogInfo($"Consent {(agree ? "given" : "withdrawn")} from the menu.");
            _consentOptIn = agree;
            PublishConsent();
            _nextGateCheckTime = 0f;
        }

        // Writes this player's flags into their own lobby member data, only
        // when something changed.
        private void PublishConsent()
        {
            CSteamID lobby = SteamManager.CurrentLobbyID;
            if (lobby.m_SteamID == 0)
            {
                if (_consentOptIn) Logger.LogInfo("Consent reset: left the Steam lobby.");
                _consentOptIn = false;
                _consentLobby = 0;
                _publishedConsent = null;
                return;
            }

            if (lobby.m_SteamID != _consentLobby)
            {
                if (_consentOptIn) Logger.LogInfo("Consent reset: entered a different Steam lobby.");
                _consentOptIn = false;
                _consentLobby = lobby.m_SteamID;
            }

            string state = $"{lobby.m_SteamID}|{_consentOptIn}";
            if (state == _publishedConsent && Time.unscaledTime < _nextRepublishTime) return;

            SteamMatchmaking.SetLobbyMemberData(lobby, ModMemberKey, PluginVersion);
            SteamMatchmaking.SetLobbyMemberData(lobby, ConsentMemberKey, _consentOptIn ? "1" : "0");
            _publishedConsent = state;
            _nextRepublishTime = Time.unscaledTime + RepublishInterval;

            // Read back what Steam now holds for us, to tell "never written"
            // apart from "written but not reaching the others".
            CSteamID me = SteamUser.GetSteamID();
            string mod = SteamMatchmaking.GetLobbyMemberData(lobby, me, ModMemberKey);
            string consent = SteamMatchmaking.GetLobbyMemberData(lobby, me, ConsentMemberKey);
            string status = $"房间 {lobby.m_SteamID}: mod='{mod}' 同意='{consent}'";
            if (status != _ownMemberDataStatus)
            {
                _ownMemberDataStatus = status;
                Logger.LogInfo($"Own lobby member data: lobby={lobby.m_SteamID} me={me.m_SteamID} mod='{mod}' consent='{consent}'");
            }
        }

        // True when every lobby member has opted in and every player in the
        // game is one of them.
        private bool RoomFullyConsented(CSteamID lobby, int remoteClients)
        {
            int total = SteamMatchmaking.GetNumLobbyMembers(lobby);
            var consenting = new HashSet<ulong>();
            var holdouts = new List<string>();
            var dump = new List<string>();

            for (int i = 0; i < total; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                string consentValue = SteamMatchmaking.GetLobbyMemberData(lobby, member, ConsentMemberKey);
                dump.Add($"{member.m_SteamID}: mod='{SteamMatchmaking.GetLobbyMemberData(lobby, member, ModMemberKey)}' consent='{consentValue}'");

                bool agreed = consentValue == "1";
                if (agreed)
                {
                    consenting.Add(member.m_SteamID);
                }
                else
                {
                    // Builds from before the agreed-room feature never write
                    // the mod key, so "no mod" can also mean an old version.
                    string version = SteamMatchmaking.GetLobbyMemberData(lobby, member, ModMemberKey);
                    string note = string.IsNullOrEmpty(version) ? "(没装 mod 或 mod 版本太旧)"
                        : version != PluginVersion ? $"(mod 版本 {version}，你是 {PluginVersion})"
                        : "";
                    holdouts.Add(SteamFriends.GetFriendPersonaName(member) + note);
                }
            }

            _consentSummary = $"{consenting.Count}/{total} 人已同意"
                              + (holdouts.Count > 0 ? "，未同意: " + string.Join("、", holdouts) : "");

            string dumpLine = $"lobby={lobby.m_SteamID} " + string.Join("; ", dump)
                              + " | players in game: " + string.Join(",", PlayerManager.Players.Where(p => p != null).Select(p => p.SteamID));
            if (dumpLine != _lastMemberDump)
            {
                _lastMemberDump = dumpLine;
                Logger.LogInfo("Lobby member data seen: " + dumpLine);
            }

            if (total <= 1 || consenting.Count != total) return false;

            // A connection that isn't a lobby member can't have agreed.
            if (_isHost && remoteClients > total - 1) return false;

            foreach (var player in PlayerManager.Players)
            {
                if (player == null || !consenting.Contains(player.SteamID)) return false;
            }
            return true;
        }

        private void RefreshGate()
        {
            try
            {
                PublishConsent();

                bool inSession = Server.Instance != null && Player.LocalPlayer != null
                                 && (Server.Instance.IsServerInitialized || Server.Instance.IsClientInitialized);
                if (!inSession)
                {
                    _gateOpen = false;
                    _isHost = false;
                    _consentSummary = "";
                    _gateStatus = "未进入对局";
                    return;
                }

                _isHost = Server.Instance.IsServerInitialized;
                int otherPlayers = PlayerManager.OtherPlayers.Count;

                // FishNet counts the host's own client connection in Clients,
                // so a solo host sees 1 there. Only connections that aren't the
                // local client represent other people.
                int remoteClients = 0;
                var serverManager = FishNet.InstanceFinder.ServerManager;
                if (_isHost && serverManager != null)
                {
                    foreach (var conn in serverManager.Clients.Values)
                    {
                        if (conn != null && !conn.IsLocalClient) remoteClients++;
                    }
                }

                CSteamID lobby = SteamManager.CurrentLobbyID;
                bool inLobby = lobby.m_SteamID != 0;
                int lobbyMembers = inLobby ? SteamMatchmaking.GetNumLobbyMembers(lobby) : 1;

                if (_isHost && otherPlayers == 0 && remoteClients == 0 && lobbyMembers <= 1)
                {
                    _gateOpen = true;
                    _consentSummary = "";
                    _gateStatus = "单人会话";
                    return;
                }

                if (!inLobby)
                {
                    _gateOpen = false;
                    _consentSummary = "";
                    _gateStatus = "多人对局，但不是 Steam 大厅，无法确认其他人是否同意";
                    return;
                }

                _gateOpen = RoomFullyConsented(lobby, remoteClients);
                _gateStatus = _gateOpen ? "全员同意房间" : "多人房间，未全员同意";
            }
            catch (Exception e)
            {
                // Anything that can't be confirmed keeps the trainer off.
                _gateOpen = false;
                _gateStatus = "检测失败，已禁用 (" + e.Message + ")";
            }
        }
    }
}
