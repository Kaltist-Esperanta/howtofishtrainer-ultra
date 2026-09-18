// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HowToFishTrainer
{
    public partial class Plugin
    {
        private bool _cursorFreed;
        private static FieldInfo _thinkingField;

        private void SetCursorFreed(bool freed)
        {
            _cursorFreed = freed;
            SetGameInputBlocked(freed);

            if (freed)
            {
                ApplyFreedCursor();
            }
            else
            {
                try { PlayerCamera.ToggleMouse(false); }
                catch (Exception e) { Logger.LogWarning("Could not re-lock cursor: " + e.Message); }
            }
        }

        private void ApplyFreedCursor()
        {
            try
            {
                // ToggleMouse dereferences GameInfo.Input, which doesn't exist
                // until the game is past its initial load.
                if (GameInfo.Input == null) return;
                PlayerCamera.ToggleMouse(true);
            }
            catch
            {
                // Not in a state where the camera can be touched; try again
                // next frame rather than spamming the log.
            }
        }

        // Blocks the game's keyboard/mouse handling while the cursor is out, so
        // typing in the trainer doesn't walk the player around or trigger the
        // dev hotkeys (M/N/G/H/O/T).
        //
        // Two mechanisms are needed: PlayerInput covers everything bound through
        // the new Input System, while PlayerThinking.IsThinking is what
        // Player.BlockInputs reads, which is the flag the game's legacy
        // Input.GetKeyDown hotkeys check. Setting IsThinking also makes
        // PlayerCamera.ToggleMouse keep the cursor unlocked, so a click no
        // longer re-locks it.
        private void SetGameInputBlocked(bool blocked)
        {
            try
            {
                // Order matters on restore: ToggleMouse refuses to re-lock while
                // IsThinking is still set.
                SetThinkingFlag(blocked);

                var input = GameInfo.Input;
                if (input != null)
                {
                    if (blocked) input.DeactivateInput();
                    else input.ActivateInput();
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not toggle game input: " + e.Message);
            }
        }

        private void SetThinkingFlag(bool value)
        {
            // Written directly rather than through ToggleThinking, which would
            // also open the in-game journal UI.
            if (_thinkingField == null)
            {
                _thinkingField = AccessTools.Field(typeof(PlayerThinking), "<IsThinking>k__BackingField")
                    ?? typeof(PlayerThinking)
                        .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(f => f.FieldType == typeof(bool) && f.Name.Contains("IsThinking"));

                if (_thinkingField == null)
                {
                    Logger.LogWarning("IsThinking backing field not found; the game's legacy cheat hotkeys stay active while the cursor is out.");
                    return;
                }
            }
            _thinkingField.SetValue(null, value);
        }
    }
}
