// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HowToFishTrainer
{
    public partial class Plugin
    {
        private const float EspMaxDistance = 300f;

        private bool _esp;
        private int _espFontSize = 18;
        private GUIStyle _espStyle;
        private static Texture2D _whiteTex;

        private readonly List<Creature> _espTargets = new List<Creature>();
        private readonly HashSet<Creature> _espSeen = new HashSet<Creature>();
        private readonly Dictionary<Creature, string> _espNames = new Dictionary<Creature, string>();
        private readonly Dictionary<Creature, Renderer[]> _espRenderers = new Dictionary<Creature, Renderer[]>();

        // Scanned once per frame here: OnGUI runs several times per frame, so
        // walking every item from there would multiply the cost.
        private void CollectEspTargets()
        {
            _espTargets.Clear();
            if (!_esp || !_gateOpen || !_wantCheatsEnabled)
            {
                if (_espNames.Count > 0) _espNames.Clear();
                if (_espRenderers.Count > 0) _espRenderers.Clear();
                return;
            }

            var cam = GameInfo.CurCamera;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            float maxSqr = EspMaxDistance * EspMaxDistance;

            // Items is keyed by transform and one item can own several, so the
            // same creature can come up more than once.
            _espSeen.Clear();
            foreach (var item in ItemManager.Items.Values)
            {
                var creature = item != null ? item.Creature : null;
                if (creature == null || !creature.isActiveAndEnabled || creature.IsDeinitializing || creature.IsDead) continue;
                if (!_espSeen.Add(creature)) continue;
                if ((creature.transform.position - camPos).sqrMagnitude > maxSqr) continue;
                _espTargets.Add(creature);
            }

            if (_espNames.Count > 512) _espNames.Clear();
        }

        private Renderer[] GetBodyRenderers(Creature creature)
        {
            if (_espRenderers.TryGetValue(creature, out var cached) && cached != null) return cached;

            // Only the meshes: particle renderers (blood, bubbles) have huge
            // bounds and would blow the box up.
            var renderers = creature.GetComponentsInChildren<Renderer>()
                .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                .ToArray();

            if (_espRenderers.Count > 512) _espRenderers.Clear();
            _espRenderers[creature] = renderers;
            return renderers;
        }

        // Projects the creature's world-space bounds and returns the screen
        // rectangle enclosing all eight corners.
        private bool TryGetScreenBox(Camera cam, Creature creature, float sx, float sy, out Rect box)
        {
            box = default;

            bool has = false;
            Bounds bounds = default;
            foreach (var r in GetBodyRenderers(creature))
            {
                if (r == null || !r.enabled) continue;
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!has) return false;

            Vector3 min = bounds.min, max = bounds.max;
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z);
                Vector3 s = cam.WorldToScreenPoint(corner);
                if (s.z <= 0f) return false;

                float x = s.x * sx;
                float y = Screen.height - s.y * sy;
                xMin = Mathf.Min(xMin, x); xMax = Mathf.Max(xMax, x);
                yMin = Mathf.Min(yMin, y); yMax = Mathf.Max(yMax, y);
            }

            box = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        private static void EnsureWhiteTex()
        {
            if (_whiteTex != null) return;
            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
        }

        private static void DrawBox(Rect r, Color color, float thickness)
        {
            EnsureWhiteTex();
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, r.width, thickness), _whiteTex);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), _whiteTex);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, thickness, r.height), _whiteTex);
            GUI.DrawTexture(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), _whiteTex);
            GUI.color = previous;
        }

        // IMGUI has no line primitive: draw a thin texture strip rotated around
        // its start point.
        private static void DrawLine(Vector2 from, Vector2 to, Color color, float width)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 1f) return;

            EnsureWhiteTex();
            var matrix = GUI.matrix;
            var previous = GUI.color;

            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, from);
            GUI.color = color;
            GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, length, width), _whiteTex);

            GUI.matrix = matrix;
            GUI.color = previous;
        }

        private struct EspEntry
        {
            public Creature Creature;
            public Color Color;
            public float Distance;
            public bool HasBox;
            public Rect Box;
            public bool Behind;
            // Where the label sits and the snapline ends: the top middle of the
            // box, the creature's centre when it has no visible mesh, or a point
            // on the screen edge when it's behind the camera.
            public Vector2 Anchor;
        }

        private const float EdgeMargin = 14f;

        // A point behind the camera has no screen position, so point toward it
        // from the screen centre instead. Behind is shown as "down": the line
        // heads for the bottom edge, leaning left or right by how far to the
        // side the creature is, rather than up onto the top edge where it would
        // run along the border next to the snapline origin.
        private static Vector2 EdgePointToward(Camera cam, Vector3 world)
        {
            Vector3 local = cam.transform.InverseTransformPoint(world);
            var dir = new Vector2(local.x, Mathf.Abs(local.y) + Mathf.Abs(local.z));
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.up;
            dir.Normalize();

            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float tx = dir.x > 0f ? (Screen.width - EdgeMargin - center.x) / dir.x
                : dir.x < 0f ? (EdgeMargin - center.x) / dir.x
                : float.MaxValue;
            float ty = (Screen.height - EdgeMargin - center.y) / dir.y;
            return center + dir * Mathf.Min(tx, ty);
        }

        private readonly List<EspEntry> _espEntries = new List<EspEntry>();

        private void DrawEsp()
        {
            if (_espTargets.Count == 0 || Event.current.type != EventType.Repaint) return;

            var cam = GameInfo.CurCamera;
            if (cam == null) return;

            // In third person the frame was rendered from behind the player;
            // project from there too so boxes line up with the image.
            bool moved = BeginThirdPersonProjection(cam, out Vector3 savedCamPos);
            try
            {
                DrawEspFrom(cam);
            }
            finally
            {
                if (moved) cam.transform.position = savedCamPos;
            }
        }

        private void DrawEspFrom(Camera cam)
        {
            if (_espStyle == null)
            {
                _espStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.LowerCenter,
                    fontStyle = FontStyle.Bold,
                };
            }
            _espStyle.fontSize = _espFontSize;
            float lineHeight = _espFontSize * 1.3f;

            // The camera may render at a different resolution than the window.
            float sx = Screen.width / (float)Mathf.Max(1, cam.pixelWidth);
            float sy = Screen.height / (float)Mathf.Max(1, cam.pixelHeight);
            Vector3 camPos = cam.transform.position;

            _espEntries.Clear();
            foreach (var creature in _espTargets)
            {
                if (creature == null) continue;

                Vector3 world = creature.Rig != null ? creature.Rig.worldCenterOfMass : creature.transform.position;
                float dist = Vector3.Distance(camPos, world);
                var entry = new EspEntry
                {
                    Creature = creature,
                    Distance = dist,
                    Color = dist < 30f ? new Color(1f, 0.35f, 0.3f)
                        : dist < 100f ? new Color(1f, 0.85f, 0.3f)
                        : new Color(0.75f, 0.95f, 1f),
                };

                if (TryGetScreenBox(cam, creature, sx, sy, out Rect box))
                {
                    entry.HasBox = true;
                    entry.Box = box;
                    entry.Anchor = new Vector2(box.center.x, box.yMin);
                }
                else
                {
                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z > 0f)
                    {
                        entry.Anchor = new Vector2(screen.x * sx, Screen.height - screen.y * sy);
                    }
                    else
                    {
                        entry.Behind = true;
                        entry.Anchor = EdgePointToward(cam, world);
                    }
                }
                _espEntries.Add(entry);
            }

            // All snaplines first, so none of them is drawn over another
            // creature's box or label.
            var top = new Vector2(Screen.width * 0.5f, 0f);
            foreach (var entry in _espEntries)
            {
                DrawLine(top, entry.Anchor, Color.black, 3f);
                DrawLine(top, entry.Anchor, entry.Color, 1.5f);
            }

            foreach (var entry in _espEntries)
            {
                if (entry.HasBox)
                {
                    var box = entry.Box;
                    DrawBox(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), Color.black, 4f);
                    DrawBox(box, entry.Color, 2f);
                }
                else if (entry.Behind)
                {
                    // Small marker where the line meets the edge.
                    var marker = new Rect(entry.Anchor.x - 5f, entry.Anchor.y - 5f, 10f, 10f);
                    DrawBox(new Rect(marker.x - 1f, marker.y - 1f, marker.width + 2f, marker.height + 2f), Color.black, 4f);
                    DrawBox(marker, entry.Color, 5f);
                }

                if (!_espNames.TryGetValue(entry.Creature, out var name))
                {
                    name = entry.Creature.GetName();
                    _espNames[entry.Creature] = name;
                }
                string text = $"{M(name)}\n{M($"{entry.Distance:0}m  HP {entry.Creature.Hp}")}";

                // Two lines of text, sitting on top of the box. Kept on screen,
                // which matters for labels at the side edges.
                float labelWidth = 300f;
                float labelX = Mathf.Clamp(entry.Anchor.x - labelWidth * 0.5f, 0f, Screen.width - labelWidth);
                var rect = new Rect(labelX, entry.Anchor.y - 6f - lineHeight * 2f, labelWidth, lineHeight * 2f);
                _espStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _espStyle);
                _espStyle.normal.textColor = entry.Color;
                GUI.Label(rect, text, _espStyle);
            }
        }
    }
}
