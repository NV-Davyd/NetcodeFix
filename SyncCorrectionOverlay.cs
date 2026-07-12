using System.Collections.Generic;
using CarX;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetcodeFix
{
    internal static class SyncCorrectionOverlay
    {
        private const float StaleAfterSeconds = 1f;
        private const float BoxLineWidth = 0.035f;
        private const float CorrectionLineWidth = 0.025f;

        private static readonly Color TargetColor = new Color(0.12f, 0.45f, 1f, 0.95f);
        private static readonly Color PhysicsColor = new Color(0.25f, 1f, 0.35f, 0.8f);
        private static readonly Color CorrectionColor = new Color(0.2f, 0.85f, 1f, 0.95f);

        private static readonly Dictionary<int, SyncBox> Boxes = new Dictionary<int, SyncBox>();
        private static readonly List<int> StaleIds = new List<int>();
        private static readonly Vector3[] BoxCorners = new Vector3[8];
        private static readonly Vector3[] BoxPath = new Vector3[16];
        private static readonly int[] BoxPathIndices =
        {
            0, 1, 3, 2, 0, 4, 5, 1, 5, 7, 3, 7, 6, 2, 6, 4
        };

        private static UnityEngine.Material _lineMaterial;
        private static bool _shaderWarningLogged;
        private static bool _captureLogged;

        public static void Initialize()
        {
            Kino.Log.Info("[NetcodeFix] Sync overlay renderer initialized.");
        }

        public static void Tick()
        {
            if (!Settings.DrawSyncOverlay)
            {
                ClearBoxes();
                return;
            }

            RemoveStaleBoxes();
        }

        public static void Capture(NetGameSyncPosSystem.PosSyncInfo syncInfo, Car car, Rigidbody rigidbody)
        {
            if (!Settings.DrawSyncOverlay || syncInfo?.host == null || car == null || rigidbody == null || !EnsureMaterial())
            {
                return;
            }

            try
            {
                int networkId = syncInfo.host.NetworkID;
                if (!Boxes.TryGetValue(networkId, out SyncBox box) || box.Rigidbody != rigidbody)
                {
                    box?.Destroy();
                    box = new SyncBox(networkId, rigidbody, car.bounds);
                    Boxes[networkId] = box;
                }

                box.LocalBounds = car.bounds;
                box.TargetPosition = rigidbody.position + syncInfo.fwdPosError + syncInfo.sidePosError;
                box.TargetRotation = rigidbody.rotation * syncInfo.syncRotError;
                box.LastSeen = Time.unscaledTime;
                box.UpdateRenderers();

                if (!_captureLogged)
                {
                    _captureLogged = true;
                    Kino.Log.Info("[NetcodeFix] Sync overlay created its first remote-car line renderers.");
                }
            }
            catch
            {
                // A network car can disappear while its scene objects are being updated.
            }
        }

        public static void Shutdown()
        {
            ClearBoxes();
            StaleIds.Clear();

            if (_lineMaterial != null)
            {
                UnityEngine.Object.Destroy(_lineMaterial);
                _lineMaterial = null;
            }

            _captureLogged = false;
        }

        private static bool EnsureMaterial()
        {
            if (_lineMaterial != null)
            {
                return true;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                if (!_shaderWarningLogged)
                {
                    Kino.Log.Warning("[NetcodeFix] Sync overlay shader was not found.");
                    _shaderWarningLogged = true;
                }

                return false;
            }

            _lineMaterial = new UnityEngine.Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            return true;
        }

        private static LineRenderer CreateLineRenderer(string name, Color color, float width, int positionCount)
        {
            GameObject gameObject = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            LineRenderer line = gameObject.AddComponent<LineRenderer>();
            line.sharedMaterial = _lineMaterial;
            line.useWorldSpace = true;
            line.positionCount = positionCount;
            line.startColor = color;
            line.endColor = color;
            line.widthMultiplier = width;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            line.allowOcclusionWhenDynamic = false;
            line.sortingOrder = short.MaxValue;
            return line;
        }

        private static void SetBox(LineRenderer line, Vector3 position, Quaternion rotation, Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            BoxCorners[0] = center + new Vector3(-extents.x, -extents.y, -extents.z);
            BoxCorners[1] = center + new Vector3( extents.x, -extents.y, -extents.z);
            BoxCorners[2] = center + new Vector3(-extents.x,  extents.y, -extents.z);
            BoxCorners[3] = center + new Vector3( extents.x,  extents.y, -extents.z);
            BoxCorners[4] = center + new Vector3(-extents.x, -extents.y,  extents.z);
            BoxCorners[5] = center + new Vector3( extents.x, -extents.y,  extents.z);
            BoxCorners[6] = center + new Vector3(-extents.x,  extents.y,  extents.z);
            BoxCorners[7] = center + new Vector3( extents.x,  extents.y,  extents.z);

            for (int i = 0; i < BoxPath.Length; i++)
            {
                BoxPath[i] = position + rotation * BoxCorners[BoxPathIndices[i]];
            }

            line.SetPositions(BoxPath);
        }

        private static void RemoveStaleBoxes()
        {
            float now = Time.unscaledTime;
            StaleIds.Clear();

            foreach (KeyValuePair<int, SyncBox> entry in Boxes)
            {
                if (entry.Value.Rigidbody == null || now - entry.Value.LastSeen > StaleAfterSeconds)
                {
                    StaleIds.Add(entry.Key);
                }
            }

            foreach (int networkId in StaleIds)
            {
                Boxes[networkId].Destroy();
                Boxes.Remove(networkId);
            }
        }

        private static void ClearBoxes()
        {
            foreach (SyncBox box in Boxes.Values)
            {
                box.Destroy();
            }

            Boxes.Clear();
        }

        private sealed class SyncBox
        {
            public readonly Rigidbody Rigidbody;
            public Bounds LocalBounds;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public float LastSeen;

            private readonly LineRenderer _physicsBox;
            private readonly LineRenderer _targetBox;
            private readonly LineRenderer _correctionLine;

            public SyncBox(int networkId, Rigidbody rigidbody, Bounds localBounds)
            {
                Rigidbody = rigidbody;
                LocalBounds = localBounds;
                _physicsBox = CreateLineRenderer("NetcodeFix.PhysicsBox." + networkId, PhysicsColor, BoxLineWidth, BoxPath.Length);
                _targetBox = CreateLineRenderer("NetcodeFix.TargetBox." + networkId, TargetColor, BoxLineWidth, BoxPath.Length);
                _correctionLine = CreateLineRenderer("NetcodeFix.CorrectionLine." + networkId, CorrectionColor, CorrectionLineWidth, 2);
            }

            public void UpdateRenderers()
            {
                SetBox(_physicsBox, Rigidbody.position, Rigidbody.rotation, LocalBounds);
                SetBox(_targetBox, TargetPosition, TargetRotation, LocalBounds);

                Vector3 localCenter = LocalBounds.center;
                _correctionLine.SetPosition(0, Rigidbody.position + Rigidbody.rotation * localCenter);
                _correctionLine.SetPosition(1, TargetPosition + TargetRotation * localCenter);
            }

            public void Destroy()
            {
                if (_physicsBox != null) UnityEngine.Object.Destroy(_physicsBox.gameObject);
                if (_targetBox != null) UnityEngine.Object.Destroy(_targetBox.gameObject);
                if (_correctionLine != null) UnityEngine.Object.Destroy(_correctionLine.gameObject);
            }
        }
    }
}
