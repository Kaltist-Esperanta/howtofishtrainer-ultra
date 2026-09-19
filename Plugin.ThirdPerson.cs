// AI-generated code: written by Claude (Anthropic) through Claude Code. See README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace HowToFishTrainer
{
    // Third-person view with a local stand-in body.
    //
    // The game destroys the local player's own full-body model at spawn
    // (Player.InitializePlayer destroys _otherObjects for the owner), so there
    // is nothing to see from behind. The stand-in is a copy of the player
    // prefab built under an inactive parent, so none of its scripts ever wake
    // (no network registration, no player setup), then stripped to meshes. It
    // stands where other players see you, faces the yaw your client reports
    // (including the spin) and wears your skin. Its legs don't animate: the leg
    // IK is driven by network data this copy doesn't have.
    //
    // The camera is only moved while it renders (URP begin/end camera
    // callbacks). Aiming, interaction, aim assist and projectiles keep reading
    // the first-person camera position during Update/LateUpdate, so third
    // person doesn't change what you hit.
    public partial class Plugin
    {
        private bool _thirdPerson;
        private float _thirdPersonDistance = 3f;
        private const float ShoulderOffset = 0.5f;
        private const float HeightOffset = 0.25f;

        private bool ThirdPersonActive => _thirdPerson && _gateOpen && _wantCheatsEnabled;

        private GameObject _standIn;
        private Transform _standInBody;
        private Transform _standInHead;
        private Player _standInFor;
        private bool _standInFailed;

        private Vector3 _thirdPersonCamPos;
        private bool _thirdPersonPoseValid;

        private Camera _movedCamera;
        private Vector3 _savedCamPos;
        private readonly List<Renderer> _hiddenForRender = new List<Renderer>();

        private static readonly string[] SkinRendererFields =
        {
            "_bodyRenderer", "_leftHand", "_rightHand", "_hatRenderer", "_accessoryRenderer", "_outfitRenderer",
        };

        private void InitThirdPerson()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void ShutdownThirdPerson()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            DestroyStandIn();
        }

        // Runs in LateUpdate, after PlayerCamera has placed the first-person
        // camera for this frame.
        private void UpdateThirdPerson()
        {
            _thirdPersonPoseValid = false;

            var player = Player.LocalPlayer;
            var cam = GameInfo.CurCamera;
            if (!ThirdPersonActive || player == null || cam == null)
            {
                if (_standIn != null) _standIn.SetActive(false);
                return;
            }

            Transform ct = cam.transform;
            Vector3 head = ct.position;
            Vector3 desired = head - ct.forward * _thirdPersonDistance + ct.right * ShoulderOffset + Vector3.up * HeightOffset;

            // Pull in rather than end up inside terrain or the boat.
            Vector3 offset = desired - head;
            float length = offset.magnitude;
            int blockers = (int)GameInfo.LevelLayer | (int)GameInfo.BoatLayer;
            if (length > 0.01f && Physics.SphereCast(head, 0.2f, offset / length, out RaycastHit hit, length,
                    blockers, QueryTriggerInteraction.Ignore))
            {
                desired = head + offset / length * Mathf.Max(0.1f, hit.distance - 0.1f);
            }
            _thirdPersonCamPos = desired;
            _thirdPersonPoseValid = true;

            if (_standIn == null || _standInFor != player) BuildStandIn(player);
            if (_standIn == null) return;
            _standIn.SetActive(true);

            // The same angles the network patch reports, so this matches what
            // others see, spin and pitch included.
            Vector3 camEuler = player.Camera.CamTransform.localEulerAngles;
            _standInBody.SetPositionAndRotation(player.Transform.position,
                Quaternion.Euler(0f, ReportedYaw(camEuler.y), 0f));
            if (_standInHead != null) _standInHead.localRotation = Quaternion.Euler(ReportedPitch(camEuler.x), 0f, 0f);
        }

        // ---- Stand-in body -----------------------------------------------------
        private void BuildStandIn(Player player)
        {
            DestroyStandIn();
            if (_standInFailed) return;

            GameObject holder = null;
            try
            {
                var prefab = GameInfo.PlayerPrefab;
                if (prefab == null) throw new InvalidOperationException("player prefab not available");
                Transform prefabRoot = prefab.transform;

                var localObjects = AccessTools.Field(typeof(Player), "_localObjects").GetValue(prefab) as List<GameObject>;
                var otherObjects = AccessTools.Field(typeof(Player), "_otherObjects").GetValue(prefab) as List<GameObject>;
                var other = AccessTools.Field(typeof(Player), "_other").GetValue(prefab) as OtherPlayer;
                var prefabSkin = AccessTools.Field(typeof(Player), "_playerSkin").GetValue(prefab) as PlayerSkin;

                // Sibling-index paths taken on the prefab and resolved on the
                // copy; Instantiate keeps child order. All are resolved before
                // anything is destroyed, since that shifts sibling indices.
                var localPaths = (localObjects ?? new List<GameObject>()).Where(o => o != null)
                    .Select(o => IndexPath(prefabRoot, o.transform)).ToList();
                var otherPaths = (otherObjects ?? new List<GameObject>()).Where(o => o != null)
                    .Select(o => IndexPath(prefabRoot, o.transform)).ToList();
                int[] bodyPath = other != null ? IndexPath(prefabRoot, other.Transform) : null;
                int[] headPath = other != null && other.CamProxy != null ? IndexPath(prefabRoot, other.CamProxy) : null;
                var skinPaths = SkinRendererFields.ToDictionary(name => name, name =>
                {
                    var renderer = prefabSkin != null
                        ? AccessTools.Field(typeof(PlayerSkin), name)?.GetValue(prefabSkin) as Renderer
                        : null;
                    return renderer != null ? IndexPath(prefabRoot, renderer.transform) : null;
                });

                // Under an inactive parent the copy never runs Awake.
                holder = new GameObject("TrainerStandIn");
                holder.SetActive(false);
                GameObject copy = Instantiate(prefab.gameObject, holder.transform, false);
                Transform copyRoot = copy.transform;

                var localTransforms = localPaths.Select(p => Resolve(copyRoot, p)).Where(t => t != null).ToList();
                var otherTransforms = otherPaths.Select(p => Resolve(copyRoot, p)).Where(t => t != null).ToList();
                Transform body = Resolve(copyRoot, bodyPath) ?? copyRoot;
                Transform headT = Resolve(copyRoot, headPath);
                var skinRenderers = skinPaths.ToDictionary(kv => kv.Key,
                    kv => Resolve(copyRoot, kv.Value)?.GetComponent<SkinnedMeshRenderer>());

                // First-person parts (camera, HUD, arms) go; the body others see
                // is switched on, as InitializePlayer does for remote players.
                foreach (var t in localTransforms) DestroyImmediate(t.gameObject);
                foreach (var t in otherTransforms) t.gameObject.SetActive(true);

                StripToMeshes(copy);
                if (copy.GetComponentsInChildren<MonoBehaviour>(true).Length > 0)
                {
                    throw new InvalidOperationException("scripts left on the copy after stripping");
                }

                ApplySkin(player.Skin, skinRenderers);

                _standIn = holder;
                _standInBody = body;
                _standInHead = headT;
                _standInFor = player;
                Logger.LogInfo("Third-person body built.");
            }
            catch (Exception e)
            {
                Logger.LogError("Could not build the third-person body; third person continues without it: " + e);
                _standInFailed = true;
                if (holder != null) DestroyImmediate(holder);
                DestroyStandIn();
            }
        }

        private void DestroyStandIn()
        {
            if (_standIn != null) Destroy(_standIn);
            _standIn = null;
            _standInBody = null;
            _standInHead = null;
            _standInFor = null;
        }

        // Leaves only transforms and mesh renderers. A component another one
        // requires can't be removed first, so scripts go before colliders
        // before rigidbodies before the rest, over a few passes.
        private static void StripToMeshes(GameObject root)
        {
            foreach (var joint in root.GetComponentsInChildren<Joint>(true)) DestroyImmediate(joint);

            for (int pass = 0; pass < 6; pass++)
            {
                var remaining = root.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && !IsMeshComponent(c))
                    .OrderBy(c => c is MonoBehaviour ? 0 : c is Collider ? 1 : c is Rigidbody ? 2 : 3)
                    .ToList();
                if (remaining.Count == 0) return;
                foreach (var c in remaining) DestroyImmediate(c);
            }
        }

        private static bool IsMeshComponent(Component c)
        {
            return c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer;
        }

        // Same calls PlayerSkin.InitializeOther makes for a remote player.
        private static void ApplySkin(PlayerSkin skin, Dictionary<string, SkinnedMeshRenderer> r)
        {
            if (skin == null) return;

            foreach (var name in new[] { "_bodyRenderer", "_leftHand", "_rightHand" })
            {
                if (r[name] != null) ShaderManager.SetPlayerColors(r[name], skin.SkinColor);
            }
            if (r["_hatRenderer"] != null)
            {
                r["_hatRenderer"].sharedMesh = SkinManager.GetHat(skin.HatMeshIndex);
                ShaderManager.SetPlayerColors(r["_hatRenderer"], skin.SkinColor, skin.HatColor, skin.HatColor2, skin.HatColor3);
            }
            if (r["_accessoryRenderer"] != null)
            {
                r["_accessoryRenderer"].sharedMesh = SkinManager.GetAccessory(skin.AccessoryMeshIndex);
                ShaderManager.SetPlayerColors(r["_accessoryRenderer"], skin.SkinColor, skin.AccessoryColor, skin.AccessoryColor2, skin.AccessoryColor3);
            }
            if (r["_outfitRenderer"] != null)
            {
                r["_outfitRenderer"].sharedMesh = SkinManager.GetOutfit(skin.OutfitMeshIndex);
                ShaderManager.SetPlayerColors(r["_outfitRenderer"], skin.SkinColor, skin.OutfitColor, skin.OutfitColor2, skin.OutfitColor3);
            }
        }

        private static int[] IndexPath(Transform root, Transform t)
        {
            var path = new List<int>();
            while (t != null && t != root)
            {
                path.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            if (t != root) return null;
            path.Reverse();
            return path.ToArray();
        }

        private static Transform Resolve(Transform root, int[] path)
        {
            if (path == null) return null;
            Transform t = root;
            foreach (int i in path)
            {
                if (i >= t.childCount) return null;
                t = t.GetChild(i);
            }
            return t;
        }

        // ---- Render-time camera move ------------------------------------------
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!_thirdPersonPoseValid || cam == null || cam != GameInfo.CurCamera) return;

            _movedCamera = cam;
            _savedCamPos = cam.transform.position;
            cam.transform.position = _thirdPersonCamPos;
            SetFirstPersonHandsVisible(false);
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam == null || cam != _movedCamera) return;

            cam.transform.position = _savedCamPos;
            _movedCamera = null;
            SetFirstPersonHandsVisible(true);
        }

        // The first-person hands float in front of the head; the stand-in has
        // its own. The held item stays visible.
        private void SetFirstPersonHandsVisible(bool visible)
        {
            if (!visible)
            {
                _hiddenForRender.Clear();
                var skin = Player.LocalPlayer != null ? Player.LocalPlayer.Skin : null;
                if (skin == null) return;

                foreach (var name in new[] { "_localLeftHand", "_localRightHand" })
                {
                    var hand = AccessTools.Field(typeof(PlayerSkin), name)?.GetValue(skin) as Renderer;
                    if (hand != null && hand.enabled)
                    {
                        hand.enabled = false;
                        _hiddenForRender.Add(hand);
                    }
                }
            }
            else
            {
                foreach (var hand in _hiddenForRender)
                {
                    if (hand != null) hand.enabled = true;
                }
                _hiddenForRender.Clear();
            }
        }

        // For drawing (ESP) that has to line up with the third-person image.
        private bool BeginThirdPersonProjection(Camera cam, out Vector3 saved)
        {
            saved = cam.transform.position;
            if (!_thirdPersonPoseValid || cam != GameInfo.CurCamera) return false;
            cam.transform.position = _thirdPersonCamPos;
            return true;
        }
    }
}
