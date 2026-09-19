// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Steamworks;
using UnityEngine;

namespace HowToFishTrainer
{
    // Lets players who aren't the host use the host-side features in an agreed
    // room.
    //
    // Those features write server state, which only the host's game can do. A
    // client's trainer sends the request as a Steam lobby chat message and the
    // host's trainer carries it out. Steam stamps every lobby chat message with
    // the sender's SteamID, so nobody can send requests in someone else's name.
    // The host only acts while its own gate says the whole room has agreed, and
    // only for lobby members who have opted in. The game itself doesn't use
    // lobby chat, so these messages never appear in-game.
    //
    // One-off actions (money, spawning, clearing, slot machine) are carried out
    // on arrival. Toggles (lock health/fullness, boss immortality, roulette)
    // are re-sent every second while on and lapse on the host a few seconds
    // after they stop arriving, so a client that leaves or turns them off
    // doesn't leave them stuck on.
    public partial class Plugin
    {
        private const string RemotePrefix = "HTFT|";
        private const float RemoteStateSendInterval = 1f;
        private const float RemoteStateLifetime = 3f;
        private const int MaxRemoteMoney = 99999;

        private Callback<LobbyChatMsg_t> _lobbyChatCallback;
        private readonly byte[] _chatBuffer = new byte[4096];
        private float _nextRemoteStateSend;

        private class RemoteState
        {
            public bool LockHealth;
            public bool LockFullness;
            public bool IgnoreBoss;
            public bool RouletteWin;
            public float Expires;
        }

        // Host side: toggles other players currently have on, keyed by SteamID.
        private readonly Dictionary<ulong, RemoteState> _remoteStates = new Dictionary<ulong, RemoteState>();

        // Read by the patches on the host.
        internal static readonly HashSet<ulong> RemoteLockHealth = new HashSet<ulong>();
        public static bool RemoteRouletteWin;

        private bool IsRemoteClient => _gateOpen && _wantCheatsEnabled && !_isHost;
        private bool RemoteTogglesOn => LockHealth || _lockFullness || IgnoreBossImmortality || RouletteAlwaysWin;

        private void InitRemote()
        {
            _lobbyChatCallback = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);
        }

        private void ShutdownRemote()
        {
            _lobbyChatCallback?.Dispose();
            _lobbyChatCallback = null;
        }

        // Runs every frame, before patches are (un)installed.
        private void UpdateRemote()
        {
            if (_isHost && _gateOpen)
            {
                ApplyRemoteStates();
            }
            else
            {
                _remoteStates.Clear();
                RemoteLockHealth.Clear();
                RemoteRouletteWin = false;
            }

            if (IsRemoteClient && RemoteTogglesOn && Time.unscaledTime >= _nextRemoteStateSend)
            {
                _nextRemoteStateSend = Time.unscaledTime + RemoteStateSendInterval;
                SendHostRequest("state", Flag(LockHealth) + Flag(_lockFullness) + Flag(IgnoreBossImmortality) + Flag(RouletteAlwaysWin));
            }
        }

        private static string Flag(bool on) => on ? "1" : "0";

        // ---- Client side --------------------------------------------------------
        private void SendHostRequest(string command, params string[] args)
        {
            CSteamID lobby = SteamManager.CurrentLobbyID;
            if (lobby.m_SteamID == 0) return;

            string text = RemotePrefix + PluginVersion + "|" + command
                          + (args.Length > 0 ? "|" + string.Join("|", args) : "");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            SteamMatchmaking.SendLobbyChatMsg(lobby, bytes, bytes.Length);
        }

        // Features below are what the menu and hotkeys call: done locally on the
        // host, forwarded to the host otherwise.
        private void DoAddMoney(int amount)
        {
            if (_isHost)
            {
                if (Player.LocalPlayer != null) MoneyManager.AddMoney(amount, Player.LocalPlayer);
            }
            else
            {
                SendHostRequest("money", amount.ToString(CultureInfo.InvariantCulture));
            }
        }

        // A client's own hits can't get past a boss's immortality, which only
        // the host can lift, so the host does the whole kill-all.
        private void DoKillAll()
        {
            if (_isHost) KillAllCreatures(Player.LocalPlayer);
            else SendHostRequest("killall");
        }

        private void DoClearGroundItems()
        {
            if (_isHost) ClearGroundItems();
            else SendHostRequest("clear");
        }

        private void DoSlotCheat(Item item)
        {
            if (item == null || item.SkinPreset == null) return;
            byte skin = item.SkinPreset.GetRandomSkinIndex(Rarity.Legendary);
            if (_isHost) SlotMachineManager.SetCheatSkin(item, skin);
            else SendHostRequest("slot", item.ID.ToString(CultureInfo.InvariantCulture), skin.ToString(CultureInfo.InvariantCulture));
        }

        private void DoSlotReset()
        {
            if (_isHost) SlotMachineManager.SetCheatSkin(null, byte.MaxValue);
            else SendHostRequest("slotreset");
        }

        // Two metres in front of whoever asked, as the game's /spawn does for the host.
        private void DoSpawn(string key)
        {
            var cam = GameInfo.CurCamera;
            if (cam == null) return;
            Vector3 pos = cam.transform.position + cam.transform.forward * 2f;

            if (_isHost)
            {
                SpawnAt(key, pos);
            }
            else
            {
                SendHostRequest("spawn", key,
                    pos.x.ToString("R", CultureInfo.InvariantCulture),
                    pos.y.ToString("R", CultureInfo.InvariantCulture),
                    pos.z.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static void SpawnAt(string key, Vector3 position)
        {
            Item prefab = GameInfo.GetSpawnable(key);
            if (prefab == null) return;
            Item item = Instantiate(prefab, position, Quaternion.identity);
            Server.Instance.Spawn(item.gameObject);
        }

        // Skins live in each player's own local save, so this never needs the
        // host. Same steps as the game's /allskins command.
        private static void UnlockAllSkins()
        {
            SaveManager.LockAllSkins();
            foreach (Item item in GameInfo.ItemWithSkinsforCommands ?? new Item[0])
            {
                if (item == null || item.SkinPreset == null) continue;
                for (int i = 0; i < item.SkinPreset.Skins.Count; i++) SaveManager.UnlockSkin(item.ID, (byte)i);
            }
            if (BoatManager.Boat != null && BoatManager.Boat.SkinPreset != null)
            {
                for (int i = 0; i < BoatManager.Boat.SkinPreset.Skins.Count; i++) SaveManager.UnlockSkin(byte.MaxValue, (byte)i);
            }
        }

        // ---- Host side ----------------------------------------------------------
        private void OnLobbyChatMessage(LobbyChatMsg_t msg)
        {
            try
            {
                CSteamID lobby = SteamManager.CurrentLobbyID;
                if (!_isHost || !_gateOpen || lobby.m_SteamID == 0 || msg.m_ulSteamIDLobby != lobby.m_SteamID) return;

                int length = SteamMatchmaking.GetLobbyChatEntry(lobby, (int)msg.m_iChatID, out CSteamID sender,
                    _chatBuffer, _chatBuffer.Length, out EChatEntryType _);
                if (length <= 0 || sender == SteamUser.GetSteamID()) return;

                string text = Encoding.UTF8.GetString(_chatBuffer, 0, length).TrimEnd('\0');
                if (!text.StartsWith(RemotePrefix, StringComparison.Ordinal)) return;

                // The gate already requires every member to have agreed; check the
                // sender individually anyway.
                if (SteamMatchmaking.GetLobbyMemberData(lobby, sender, ConsentMemberKey) != "1") return;

                string[] parts = text.Substring(RemotePrefix.Length).Split('|');
                if (parts.Length < 2) return;
                HandleRemoteCommand(sender, parts[0], parts[1], parts.Skip(2).ToArray());
            }
            catch (Exception e)
            {
                Logger.LogWarning("Remote request failed: " + e.Message);
            }
        }

        private void HandleRemoteCommand(CSteamID sender, string version, string command, string[] args)
        {
            if (command != "state")
            {
                Logger.LogInfo($"Remote request from {sender.m_SteamID} (v{version}): {command} {string.Join(" ", args)}");
            }

            switch (command)
            {
                case "money":
                    if (args.Length >= 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                    {
                        Player player = FindPlayer(sender.m_SteamID) ?? Player.LocalPlayer;
                        MoneyManager.AddMoney(Mathf.Clamp(amount, 0, MaxRemoteMoney), player);
                    }
                    break;

                case "clear":
                    ClearGroundItems();
                    break;

                case "killall":
                    KillAllCreatures(FindPlayer(sender.m_SteamID) ?? Player.LocalPlayer);
                    break;

                case "slot":
                    if (args.Length >= 2
                        && byte.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte itemId)
                        && byte.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte skin))
                    {
                        Item item = GameInfo.IDToItem(itemId);
                        if (item != null && item.SkinPreset != null && skin < item.SkinPreset.Skins.Count)
                        {
                            SlotMachineManager.SetCheatSkin(item, skin);
                        }
                    }
                    break;

                case "slotreset":
                    SlotMachineManager.SetCheatSkin(null, byte.MaxValue);
                    break;

                case "spawn":
                    if (args.Length >= 4
                        && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        SpawnAt(args[0], new Vector3(x, y, z));
                    }
                    break;

                case "state":
                    if (args.Length >= 1 && args[0].Length >= 4)
                    {
                        string f = args[0];
                        _remoteStates[sender.m_SteamID] = new RemoteState
                        {
                            LockHealth = f[0] == '1',
                            LockFullness = f[1] == '1',
                            IgnoreBoss = f[2] == '1',
                            RouletteWin = f[3] == '1',
                            Expires = Time.unscaledTime + RemoteStateLifetime,
                        };
                    }
                    break;
            }
        }

        private void ApplyRemoteStates()
        {
            foreach (var expired in _remoteStates.Where(kv => kv.Value.Expires < Time.unscaledTime).Select(kv => kv.Key).ToList())
            {
                _remoteStates.Remove(expired);
            }

            RemoteLockHealth.Clear();
            RemoteRouletteWin = false;
            bool ignoreBoss = false;

            foreach (var kv in _remoteStates)
            {
                if (kv.Value.LockHealth) RemoteLockHealth.Add(kv.Key);
                RemoteRouletteWin |= kv.Value.RouletteWin;
                ignoreBoss |= kv.Value.IgnoreBoss;

                Player player = FindPlayer(kv.Key);
                var vitals = player != null ? player.Vitals : null;
                if (vitals == null) continue;

                if (kv.Value.LockHealth)
                {
                    if (vitals._syncedHealth.Value < 100) vitals._syncedHealth.Value = 100;
                    if (vitals._syncedPoison.Value > 0) vitals._syncedPoison.Value = 0;
                    if (vitals._syncedFire.Value > 0) vitals._syncedFire.Value = 0;
                }
                if (kv.Value.LockFullness && vitals._syncedFullness.Value < 100)
                {
                    vitals._syncedFullness.Value = 100;
                }
            }

            if (ignoreBoss) ForceBossMortal();
        }

        private static Player FindPlayer(ulong steamId)
        {
            foreach (var player in PlayerManager.Players)
            {
                if (player != null && player.SteamID == steamId) return player;
            }
            return null;
        }
    }
}
