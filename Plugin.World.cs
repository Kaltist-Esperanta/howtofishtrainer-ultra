// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HowToFishTrainer
{
    public partial class Plugin
    {
        private const float ClearConfirmWindow = 3f;
        private float _clearConfirmUntil = -1f;
        private int _clearPreviewCount;

        // Shown at the top of the screen, since the hotkey is used with the menu
        // closed and there's no button to show the confirm state on.
        private string _worldNotice;
        private float _worldNoticeUntil = -1f;
        private GUIStyle _noticeStyle;

        // Items lying in the world: not held, not in anyone's inventory, not on
        // a hook or in a bird's beak, and not in use elsewhere (IsInteractable is
        // cleared for items on the casino table and ones already being
        // destroyed). Living creatures are animals rather than items, and quest
        // items and player corpses are left alone so nothing progress-related
        // disappears.
        private static bool IsGroundItem(Item item)
        {
            if (!item.isActiveAndEnabled || item.IsDestroying || item.IsDeinitializing) return false;
            if (!item.IsInteractable) return false;
            if (item.Holder != null || item.SyncedHolder != null) return false;
            if (item.BirdHolder != null || item.AttachedRod != null) return false;
            if (item.IsQuestItem || item.DeadPlayer != null) return false;
            if (item.Creature != null && !item.Creature.IsDead) return false;
            return true;
        }

        private static List<Item> FindGroundItems()
        {
            // Items is keyed by transform and one item can own several.
            return ItemManager.Items.Values
                .Where(item => item != null)
                .Distinct()
                .Where(IsGroundItem)
                .ToList();
        }

        // Same removal the game uses elsewhere: a puff of smoke, then despawned
        // on the server.
        private void ClearGroundItems()
        {
            var items = FindGroundItems();
            foreach (var item in items)
            {
                item.DestroyItem((byte)DestroyReason.Default);
            }
            Logger.LogInfo($"Cleared {items.Count} ground items.");
            ShowWorldNotice($"已清理 {items.Count} 件物品", 2f);
        }

        private bool ClearConfirmPending => Time.unscaledTime < _clearConfirmUntil;

        // Shared by the button and the PgDn hotkey. This can't be undone, so the
        // first use only arms it and the second, within the window, clears.
        private void RequestClearGroundItems()
        {
            if (ClearConfirmPending)
            {
                _clearConfirmUntil = -1f;
                DoClearGroundItems();
                if (!_isHost) ShowWorldNotice("已请求房主清理地上物品", 2f);
                return;
            }

            _clearPreviewCount = FindGroundItems().Count;
            _clearConfirmUntil = Time.unscaledTime + ClearConfirmWindow;
            ShowWorldNotice($"再按一次 PgDn 确认清理 {_clearPreviewCount} 件物品", ClearConfirmWindow);
        }

        private void ShowWorldNotice(string text, float seconds)
        {
            _worldNotice = text;
            _worldNoticeUntil = Time.unscaledTime + seconds;
        }

        private void DrawWorldNotice()
        {
            if (Time.unscaledTime >= _worldNoticeUntil || string.IsNullOrEmpty(_worldNotice)) return;
            if (Event.current.type != EventType.Repaint) return;

            if (_noticeStyle == null)
            {
                _noticeStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 22,
                };
            }

            var rect = new Rect(0f, Screen.height * 0.12f, Screen.width, 40f);
            string text = M(_worldNotice);
            _noticeStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _noticeStyle);
            _noticeStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
            GUI.Label(rect, text, _noticeStyle);
        }

        private void DrawWorldSection()
        {
            GUILayout.Space(8);
            GUILayout.Label(M("世界:"));

            string label = ClearConfirmPending
                ? $"再点一次确认清理 {_clearPreviewCount} 件物品 ({ClearConfirmWindow:0} 秒内)"
                : "清理地上物品 (快捷键 PgDn 连按两次；含生物尸体，不含任务物品)";

            if (GUILayout.Button(M(label))) RequestClearGroundItems();
        }
    }
}
