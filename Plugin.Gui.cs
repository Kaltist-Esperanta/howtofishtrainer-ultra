// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HowToFishTrainer
{
    public partial class Plugin
    {
        private Rect _windowRect = new Rect(40, 40, 580, 720);
        private Vector2 _windowScroll;
        private Vector2 _fishScroll;
        private Vector2 _skinScroll;
        private string _fishFilter = "";

        private string _aimStrengthText = "20";
        private string _spinSpeedText = "720";
        private string _thirdPersonDistanceText = "3";
        private string _magicBulletSpeedText = "3";
        private string _damageMultiplierText = "5";
        private string _fireRateText = "2";
        private string _recoilText = "0.3";
        private string _modelShakeAutoText = "0.4";
        private string _modelShakeSemiText = "0.2";
        private string _espFontSizeText = "18";

        private List<(string key, string display)> _spawnables;
        private Item[] _skinItems;

        // Every piece of trainer text ends in 喵.
        internal static string M(string text) => text + "喵";

        private void OnGUI()
        {
            DrawEsp();
            DrawWorldNotice();
            if (!_menuVisible) return;

            // While the game has the cursor locked, IMGUI still sees every
            // click at the centre of the screen, so firing a gun with the menu
            // open would flip whatever switch happens to sit there.
            if (Cursor.lockState == CursorLockMode.Locked && (Event.current.isMouse || Event.current.isScrollWheel))
            {
                Event.current.Use();
            }

            _windowRect = GUILayout.Window(0xF15A, _windowRect, DrawWindow,
                M($"How To Fish Trainer v{PluginVersion} (F1 开关菜单 / Alt 呼出鼠标)"));
        }

        private void DrawWindow(int id)
        {
            // Always clickable: it's how an agreed room gets unlocked. First in
            // the window so the status lines below, whose length changes as
            // people agree, can't shift it under the mouse mid-click.
            GUI.enabled = SteamManager.CurrentLobbyID.m_SteamID != 0;
            bool agree = GUILayout.Toggle(_consentOptIn,
                M("我同意在本房间使用修改器 (所有人都装了 mod 并同意才会解锁；换房间自动取消)"));
            if (agree != _consentOptIn) SetConsent(agree);
            GUI.enabled = true;

            string status = (_gateOpen ? "状态: 可用 - " : "状态: 已锁定 - ") + _gateStatus;
            GUILayout.Label(M(status));
            if (!string.IsNullOrEmpty(_consentSummary)) GUILayout.Label(M("房间同意情况: " + _consentSummary));
            if (_gateOpen && !_isHost) GUILayout.Label(M("你不是房主：金钱/生成/清理/锁血等由房主的 mod 代为执行"));

            GUILayout.Label(M("鼠标: " + (_cursorFreed
                ? "已释放，游戏键鼠输入已屏蔽 (再按 Alt 恢复)"
                : "游戏锁定中 (按 Alt 呼出指针并屏蔽游戏输入)")));

            _windowScroll = GUILayout.BeginScrollView(_windowScroll, GUILayout.Height(Mathf.Min(620f, Screen.height - 180f)));

            GUI.enabled = _gateOpen;
            _wantCheatsEnabled = GUILayout.Toggle(_wantCheatsEnabled, M("启用修改器 (等同游戏内置开发者作弊模式)"));
            GUI.enabled = _gateOpen && _wantCheatsEnabled;

            DrawMoneySection();
            DrawWorldSection();
            DrawCombatSection();
            DrawOnlineSection();
            DrawGunSection();
            DrawCasinoSection();
            DrawSpawnSection();

            GUI.enabled = true;
            GUILayout.EndScrollView();

            GUILayout.Label(M("有没装 mod 或没同意的人在房间里时，所有功能会自动关闭。"));
            GUI.DragWindow();
        }


        private void DrawOnlineSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("视角 / 联机:"));

            GUILayout.BeginHorizontal();
            bool third = GUILayout.Toggle(_thirdPerson, M("第三人称 (本地重建身体，会跟着旋转；腿不会动)"), GUILayout.Width(420));
            if (third && !_thirdPerson) _standInFailed = false;
            _thirdPerson = third;
            GUILayout.Label(M("距离"), GUILayout.Width(40));
            _thirdPersonDistanceText = GUILayout.TextField(_thirdPersonDistanceText, GUILayout.Width(60));
            if (TryParseFloat(_thirdPersonDistanceText, out float distance))
            {
                _thirdPersonDistance = Mathf.Clamp(distance, 1f, 15f);
                GUILayout.Label(M($"米 (当前 {_thirdPersonDistance:0.#})"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            SpinBot = GUILayout.Toggle(SpinBot, M("旋转 (其他玩家和第三人称里看到你在转，你的视角不变)"), GUILayout.Width(420));
            GUILayout.Label(M("转速"), GUILayout.Width(40));
            _spinSpeedText = GUILayout.TextField(_spinSpeedText, GUILayout.Width(60));
            if (TryParseFloat(_spinSpeedText, out float spin))
            {
                SpinSpeed = Mathf.Clamp(spin, 10f, 7200f);
                GUILayout.Label(M($"°/s (当前 {SpinSpeed:0})"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();

            SpinPitchDown = GUILayout.Toggle(SpinPitchDown, M("    旋转时视角朝下 (别人看到你一直低着头)"));
        }

        private void DrawMoneySection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("金钱 (房主启用后也可用游戏自带的 M / N 键)"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(M("+9999")) && Player.LocalPlayer != null)
            {
                DoAddMoney(9999);
            }
            if (GUILayout.Button(M("+99999")) && Player.LocalPlayer != null)
            {
                DoAddMoney(99999);
            }
            if (GUILayout.Button(M("解锁全部皮肤")))
            {
                UnlockAllSkins();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawCombatSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("战斗:"));

            if (GUILayout.Button(M("秒杀所有生物 (快捷键 PgUp，包括 Boss)")))
            {
                DoKillAll();
            }
            IgnoreBossImmortality = GUILayout.Toggle(IgnoreBossImmortality,
                M("无视 Boss 无敌 (Boss 播动画/换阶段/多只同时存在时也能打)"));

            GUILayout.BeginHorizontal();
            _esp = GUILayout.Toggle(_esp, M($"透视 ({EspMaxDistance:0}m 内生物，方框+名字/距离/血量)"), GUILayout.Width(360));
            GUILayout.Label(M("字号"), GUILayout.Width(40));
            _espFontSizeText = GUILayout.TextField(_espFontSizeText, GUILayout.Width(40));
            if (int.TryParse(_espFontSizeText, out int fontSize)) _espFontSize = Mathf.Clamp(fontSize, 8, 60);
            GUILayout.EndHorizontal();

            _aimEnabled = GUILayout.Toggle(_aimEnabled, M($"自瞄 ({AimRange:0}m，键鼠可用，移动鼠标不会削弱吸附)"));
            AimAtHead = GUILayout.Toggle(AimAtHead, M("    锁头 (瞄准生物头部；从背后打会被身体挡住)"));
            _aimAllAround = GUILayout.Toggle(_aimAllAround, M("    360° 锁定 (身后的目标也锁，镜头会直接甩过去)"));
            AimThroughWalls = GUILayout.Toggle(AimThroughWalls, M("    穿墙锁 (隔着地形/船也锁定，配合穿墙子弹)"));
            HipFireAssist = GUILayout.Toggle(HipFireAssist, M("    腰射也自瞄 (不需要开镜)"));

            GUILayout.BeginHorizontal();
            _aimHoldMode = GUILayout.Toggle(_aimHoldMode, M("    按住侧键才自瞄"), GUILayout.Width(150));
            if (GUILayout.Toggle(_aimHoldButton == 4, M("前侧键"), GUILayout.Width(80))) _aimHoldButton = 4;
            if (GUILayout.Toggle(_aimHoldButton == 3, M("后侧键"), GUILayout.Width(80))) _aimHoldButton = 3;
            GUILayout.Label(M(_aimEnabled && AimAssistBoost ? "● 生效中" : "○ 未触发"));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(M("    自瞄强度"), GUILayout.Width(90));
            _aimStrengthText = GUILayout.TextField(_aimStrengthText, GUILayout.Width(60));
            if (TryParseFloat(_aimStrengthText, out float strength))
            {
                _aimStrength = Mathf.Clamp(strength, 0.1f, 100f);
                GUILayout.Label(M($"倍 (当前 {_aimStrength:0.#}，转速 {1440f * _aimStrength:0}°/s)"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            MagicBullet = GUILayout.Toggle(MagicBullet, M("魔法子弹 (追踪锁定目标、无下坠)"), GUILayout.Width(240));
            GUILayout.Label(M("弹速"), GUILayout.Width(40));
            _magicBulletSpeedText = GUILayout.TextField(_magicBulletSpeedText, GUILayout.Width(50));
            if (TryParseFloat(_magicBulletSpeedText, out float bulletSpeed))
            {
                MagicBulletSpeed = Mathf.Clamp(bulletSpeed, 1f, 20f);
                GUILayout.Label(M($"倍 (当前 {MagicBulletSpeed:0.#})"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();
            WallPenetration = GUILayout.Toggle(WallPenetration, M("穿墙子弹 (子弹穿过地形/船，只会被生物/玩家挡住)"));
            WaterPenetration = GUILayout.Toggle(WaterPenetration, M("穿水子弹 (子弹入水不消失，能打水下的鱼)"));
            LockHealth = GUILayout.Toggle(LockHealth, M("锁血 (免疫伤害/中毒/着火)"));
            _lockFullness = GUILayout.Toggle(_lockFullness, M("锁饱食度"));
            InfiniteJump = GUILayout.Toggle(InfiniteJump, M("无限连跳"));
            _infiniteAmmo = GUILayout.Toggle(_infiniteAmmo, M("无限子弹"));

            GUILayout.BeginHorizontal();
            DamageMultiplierOn = GUILayout.Toggle(DamageMultiplierOn, M("伤害倍率"), GUILayout.Width(100));
            _damageMultiplierText = GUILayout.TextField(_damageMultiplierText, GUILayout.Width(70));
            if (TryParseFloat(_damageMultiplierText, out float mult))
            {
                DamageMultiplier = Mathf.Clamp(mult, 0.01f, 100000f);
                GUILayout.Label(M($"x 当前: {DamageMultiplier:0.##}"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGunSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("枪械:"));
            _fixMuzzleBug = GUILayout.Toggle(_fixMuzzleBug, M("修复枪口购买 bug (换了枪口后，原来的枪口可以再买回来)"));
            SniperNoCooldown = GUILayout.Toggle(SniperNoCooldown, M("狙击枪射击间隔改为 50ms (按枪判断，装什么倍镜都有效)"));
            _shotgunSpread = GUILayout.Toggle(_shotgunSpread, M("霰弹枪散布降为 20%"));
            _forceFullAuto = GUILayout.Toggle(_forceFullAuto,
                M("半自动枪改全自动 (不退镜 / 抖动套用模板 / 霰弹枪无推力)"));

            if (_forceFullAuto) DrawTemplatePicker();

            GUILayout.BeginHorizontal();
            _fireRateOn = GUILayout.Toggle(_fireRateOn, M("射速倍率"), GUILayout.Width(110));
            _fireRateText = GUILayout.TextField(_fireRateText, GUILayout.Width(70));
            if (TryParseFloat(_fireRateText, out float rate))
            {
                _fireRateMultiplier = Mathf.Clamp(rate, 0.05f, 100f);
                GUILayout.Label(M($"倍 (当前 {_fireRateMultiplier:0.##}，越大越快)"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            RecoilReductionOn = GUILayout.Toggle(RecoilReductionOn, M("减弱镜头后坐"), GUILayout.Width(110));
            _recoilText = GUILayout.TextField(_recoilText, GUILayout.Width(70));
            if (TryParseFloat(_recoilText, out float recoil))
            {
                RecoilMultiplier = Mathf.Clamp(recoil, 0f, 1f);
                GUILayout.Label(M($"倍 (当前 {RecoilMultiplier:0.##}，准星上飘/跳动)"));
            }
            else
            {
                GUILayout.Label(M("数值无效"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            ModelShakeReductionOn = GUILayout.Toggle(ModelShakeReductionOn, M("减弱枪身抖动"), GUILayout.Width(110));
            GUILayout.Label(M("全自动枪"), GUILayout.Width(70));
            _modelShakeAutoText = GUILayout.TextField(_modelShakeAutoText, GUILayout.Width(50));
            if (TryParseFloat(_modelShakeAutoText, out float shakeAuto)) _modelShakeAuto = Mathf.Clamp(shakeAuto, 0f, 1f);
            GUILayout.Label(M("半自动枪"), GUILayout.Width(70));
            _modelShakeSemiText = GUILayout.TextField(_modelShakeSemiText, GUILayout.Width(50));
            if (TryParseFloat(_modelShakeSemiText, out float shakeSemi)) _modelShakeSemi = Mathf.Clamp(shakeSemi, 0f, 1f);
            GUILayout.EndHorizontal();
            GUILayout.Label(M("    全自动枪=步枪/冲锋枪，半自动枪=狙击/霰弹/手枪 (含改成全自动的)，0 = 完全不抖"));
        }

        private void DrawTemplatePicker()
        {
            EnsureTemplateCandidates();
            if (_templateCandidates == null)
            {
                GUILayout.Label(M("    抖动模板: 物品表尚未加载，进入对局后再看"));
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(M("    抖动模板:"), GUILayout.Width(90));
            for (int i = 0; i < _templateCandidates.Count; i++)
            {
                if (GUILayout.Toggle(_templateIndex == i, M(_templateCandidates[i].GetName())) && _templateIndex != i)
                {
                    SelectTemplate(i);
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawCasinoSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("赌场 - 轮盘 (押鱼/物品，黑红 2 倍、绿 35 倍):"));
            RouletteAlwaysWin = GUILayout.Toggle(RouletteAlwaysWin, M("轮盘必胜 (开奖结果强制等于你押的颜色，押绿=35倍)"));

            GUILayout.Space(8);
            GUILayout.Label(M("老虎机 (投入生物换皮肤) - 强制下次开出指定物品的传说皮肤:"));
            EnsureSkinList();
            _skinScroll = GUILayout.BeginScrollView(_skinScroll, GUILayout.Height(110));
            foreach (var item in _skinItems)
            {
                if (item == null || item.SkinPreset == null) continue;
                if (GUILayout.Button(M("传说皮肤: " + item.GetName())))
                {
                    DoSlotCheat(item);
                }
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button(M("恢复老虎机为正常随机")))
            {
                DoSlotReset();
            }
        }

        private void DrawSpawnSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("生成鱼类/生物 (在准星前方):"));
            EnsureSpawnList();

            GUILayout.BeginHorizontal();
            GUILayout.Label(M("筛选:"), GUILayout.Width(50));
            _fishFilter = GUILayout.TextField(_fishFilter);
            GUILayout.EndHorizontal();

            _fishScroll = GUILayout.BeginScrollView(_fishScroll, GUILayout.Height(150));
            foreach (var entry in _spawnables)
            {
                if (!string.IsNullOrEmpty(_fishFilter) &&
                    entry.display.IndexOf(_fishFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    entry.key.IndexOf(_fishFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (GUILayout.Button(M("生成: " + entry.display)))
                {
                    DoSpawn(entry.key);
                }
            }
            GUILayout.EndScrollView();
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void EnsureSpawnList()
        {
            if (_spawnables != null) return;
            _spawnables = new List<(string, string)>();
            try
            {
                var dict = AccessTools.Field(typeof(GameInfo), "_nameToSpawnable")?.GetValue(null)
                    as Dictionary<string, Item>;
                if (dict == null) return;

                foreach (var kv in dict)
                {
                    string display = kv.Value != null ? kv.Value.GetName() : kv.Key;
                    _spawnables.Add((kv.Key, string.IsNullOrEmpty(display) ? kv.Key : display));
                }
                _spawnables = _spawnables.OrderBy(e => e.display).ToList();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to read spawnable item list: " + e);
            }
        }

        private void EnsureSkinList()
        {
            if (_skinItems != null) return;
            _skinItems = GameInfo.ItemWithSkinsforCommands ?? new Item[0];
        }
    }
}
