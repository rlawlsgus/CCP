using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MultiAgentGlobalFrameReplayManager : MonoBehaviour
{
    [Header("Folder Settings")]
    public string jsonlFolderPath =
        @"C:\Users\juyeong\Desktop\LAB_JY\01_WORK\01_PaperWork\2026_SIGGRAPH\01_Codes\DataStep3\Case2\case_continuous";

    [Header("Agent Spawn")]
    public GameObject agentPrefab;
    public bool sortFilesByName = true;

    [Header("Playback")]
    public bool playOnStart = true;

    [Tooltip("프레임 간 시간(초). 데이터가 20fps면 0.05")]
    public float timePerFrame = 0.05f;

    [Range(0.1f, 5f)]
    public float playSpeed = 1.0f;

    [Tooltip("localCurrent가 startForward 기준의 로컬 좌표면 ON")]
    public bool rotateOffsetsByStartForward = false;

    public float yOffset = 0f;

    [Header("Debug")]
    public int currentGlobalFrame = 0;

    // ================= Debug Mode Switches =================
    [Header("Debug Mode (bool switches)")]
    public bool debugEnabled = true;

    [Tooltip("ON: agent끼리만 표시(파랑)")]
    public bool modeAgentOnly = true;

    [Tooltip("ON: building만 표시(빨강)")]
    public bool modeBuildingOnly = false;

    [Tooltip("ON: obstacle만 표시(노랑)")]
    public bool modeObstacleOnly = false;

    [Tooltip("ON: vehicle만 표시(초록)")]
    public bool modeVehicleOnly = false;

    [Tooltip("ON: 거리 숫자만 표시(흰색). 이게 ON이면 위 모드들은 무시됨")]
    public bool modeDistanceOnly = false;

    // ===================== ✅ Per-type radii =====================
    [Header("Debug Radii (per-type)")]
    [Tooltip("NEAR 판정 반경 (Agent)")]
    public float nearRadiusAgent = 6.0f;
    [Tooltip("HIT 판정 반경 (Agent)")]
    public float hitRadiusAgent = 0.7f;

    [Tooltip("NEAR 판정 반경 (Building)")]
    public float nearRadiusBuilding = 6.0f;
    [Tooltip("HIT 판정 반경 (Building)")]
    public float hitRadiusBuilding = 0.7f;

    [Tooltip("NEAR 판정 반경 (Obstacle)")]
    public float nearRadiusObstacle = 6.0f;
    [Tooltip("HIT 판정 반경 (Obstacle)")]
    public float hitRadiusObstacle = 0.7f;

    [Tooltip("NEAR 판정 반경 (Vehicle)")]
    public float nearRadiusVehicle = 6.0f;
    [Tooltip("HIT 판정 반경 (Vehicle)")]
    public float hitRadiusVehicle = 0.7f;

    [Tooltip("OverlapSphere에 쓸 반경은 자동으로 (near들 중 최대)로 계산됨")]
    [SerializeField] private float _queryRadiusMaxNear = 6.0f;
    // =============================================================

    [Header("Debug Query")]
    [Tooltip("OverlapSphere에 사용할 레이어 마스크")]
    public LayerMask queryMask = ~0;

    [Tooltip("Trigger도 잡을지")]
    public QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Collide;

    [Tooltip("Transform.position 텔레포트 후 물리 쿼리 정확도를 위해 SyncTransforms 호출")]
    public bool syncTransformsBeforeQuery = true;

    [Tooltip("Gizmo/Label 위치를 위로 띄우기")]
    public float gizmoYOffset = 0.2f;

    [Tooltip("와이어 스피어도 같이 그릴지")]
    public bool drawWireSphere = true;

    [Header("Tag Names")]
    public string TAG_BUILDING = "Building";
    public string TAG_OBSTACLE = "Obstacle";
    public string TAG_VEHICLE = "Vehicle";
    // =======================================================

    // --- JSON 구조 (네 파일에 맞춤) ---
    [System.Serializable]
    public class ChunkDataRaw
    {
        public int chunk_index;
        public int start_index;
        public int[] globalFrames;
        public float[] startWorldPosition;
        public float[] startForward;
        public float[][] localCurrent;
        public float[] goalWorldPosition;
    }

    class AgentTrack
    {
        public string name;
        public GameObject go;
        public Transform tr;

        public Dictionary<int, Vector3> posByFrame = new Dictionary<int, Vector3>();
        public Dictionary<int, int> nextFrameOf = new Dictionary<int, int>();
        public Dictionary<int, int> prevFrameOf = new Dictionary<int, int>();

        public bool isActiveCached = false;

        // debug output
        public bool hasDebug = false;
        public string label = "";
        public Color color = Color.white;

        public float nearestAgentDist = -1f;
        public float nearestBuildingDist = -1f;
        public float nearestObstacleDist = -1f;
        public float nearestVehicleDist = -1f;

        public string nearestAgentName = "";
        public string nearestBuildingName = "";
        public string nearestObstacleName = "";
        public string nearestVehicleName = "";
    }

    private readonly List<AgentTrack> _agents = new List<AgentTrack>();
    private readonly Dictionary<Transform, AgentTrack> _agentByRoot = new Dictionary<Transform, AgentTrack>();

    private int _globalMin = int.MaxValue;
    private int _globalMax = int.MinValue;

    private readonly Collider[] _overlapBuf = new Collider[512];

#if UNITY_EDITOR
    private GUIStyle _labelStyle;
#endif

    void OnValidate()
    {
        nearRadiusAgent = Mathf.Max(0f, nearRadiusAgent);
        hitRadiusAgent = Mathf.Max(0f, hitRadiusAgent);

        nearRadiusBuilding = Mathf.Max(0f, nearRadiusBuilding);
        hitRadiusBuilding = Mathf.Max(0f, hitRadiusBuilding);

        nearRadiusObstacle = Mathf.Max(0f, nearRadiusObstacle);
        hitRadiusObstacle = Mathf.Max(0f, hitRadiusObstacle);

        nearRadiusVehicle = Mathf.Max(0f, nearRadiusVehicle);
        hitRadiusVehicle = Mathf.Max(0f, hitRadiusVehicle);

        RecomputeQueryRadiusMaxNear();
    }

    void RecomputeQueryRadiusMaxNear()
    {
        _queryRadiusMaxNear = Mathf.Max(
            nearRadiusAgent,
            Mathf.Max(nearRadiusBuilding, Mathf.Max(nearRadiusObstacle, nearRadiusVehicle))
        );
        _queryRadiusMaxNear = Mathf.Max(0.001f, _queryRadiusMaxNear);
    }

    void Start()
    {
        RecomputeQueryRadiusMaxNear();

        LoadAll();

        if (_agents.Count > 0 && _globalMax >= _globalMin)
        {
            currentGlobalFrame = _globalMin;
            ApplyGlobalFrame(_globalMin);

            if (playOnStart)
                StartCoroutine(CoPlayGlobalFrames());
        }
    }

    void LoadAll()
    {
        _agents.Clear();
        _agentByRoot.Clear();
        _globalMin = int.MaxValue;
        _globalMax = int.MinValue;

        string folder = jsonlFolderPath;
        if (!Path.IsPathRooted(folder))
            folder = Path.Combine(Application.dataPath, folder);

        if (!Directory.Exists(folder))
        {
            Debug.LogError($"Folder not found: {folder}");
            return;
        }

        var files = Directory.GetFiles(folder, "*.jsonl");
        if (files.Length == 0)
        {
            Debug.LogError($"No jsonl files in: {folder}");
            return;
        }

        if (sortFilesByName)
            files = files.OrderBy(f => Path.GetFileNameWithoutExtension(f)).ToArray();

        foreach (var file in files)
        {
            var agentName = Path.GetFileNameWithoutExtension(file);

            var a = new AgentTrack
            {
                name = agentName,
                go = SpawnAgent(agentName)
            };
            a.tr = a.go.transform;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ChunkDataRaw raw;
                    try { raw = JsonConvert.DeserializeObject<ChunkDataRaw>(line); }
                    catch { continue; }

                    if (raw == null || raw.globalFrames == null || raw.localCurrent == null) continue;
                    if (raw.globalFrames.Length != raw.localCurrent.Length) continue;

                    Vector3 anchor = ToV3(raw.startWorldPosition);
                    anchor.y += yOffset;

                    Vector3 fwd = ToV3(raw.startForward);
                    Quaternion rot = Quaternion.identity;
                    if (rotateOffsetsByStartForward && fwd != Vector3.zero)
                        rot = Quaternion.LookRotation(fwd);

                    for (int i = 0; i < raw.globalFrames.Length; i++)
                    {
                        int gf = raw.globalFrames[i];
                        Vector3 offset = ToV3(raw.localCurrent[i]);
                        if (rotateOffsetsByStartForward && fwd != Vector3.zero)
                            offset = rot * offset;

                        Vector3 worldPos = anchor + offset;
                        a.posByFrame[gf] = worldPos;

                        _globalMin = Mathf.Min(_globalMin, gf);
                        _globalMax = Mathf.Max(_globalMax, gf);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed reading {file}\n{e}");
                Destroy(a.go);
                continue;
            }

            BuildNeighborMaps(a);

            a.go.SetActive(false);
            a.isActiveCached = false;

            _agents.Add(a);
            _agentByRoot[a.tr] = a;
        }

        if (_globalMin == int.MaxValue) _globalMin = 0;
        if (_globalMax == int.MinValue) _globalMax = -1;

        Debug.Log($"Loaded {_agents.Count} agents. GlobalFrames range = [{_globalMin}..{_globalMax}]");
    }

    void BuildNeighborMaps(AgentTrack a)
    {
        a.nextFrameOf.Clear();
        a.prevFrameOf.Clear();

        if (a.posByFrame == null || a.posByFrame.Count == 0) return;

        var frames = a.posByFrame.Keys.OrderBy(x => x).ToList();
        for (int i = 0; i < frames.Count; i++)
        {
            int gf = frames[i];
            int prev = (i > 0) ? frames[i - 1] : int.MinValue;
            int next = (i + 1 < frames.Count) ? frames[i + 1] : int.MinValue;

            a.prevFrameOf[gf] = prev;
            a.nextFrameOf[gf] = next;
        }
    }

    GameObject SpawnAgent(string agentName)
    {
        GameObject go = agentPrefab != null
            ? Instantiate(agentPrefab, Vector3.zero, Quaternion.identity, transform)
            : GameObject.CreatePrimitive(PrimitiveType.Capsule);

        go.transform.SetParent(transform);
        go.name = agentName;

        foreach (var r in go.GetComponentsInChildren<AgentChunkReplayer>(true))
            r.enabled = false;

        return go;
    }

    IEnumerator CoPlayGlobalFrames()
    {
        float wait = timePerFrame / Mathf.Max(0.0001f, playSpeed);

        for (int gf = _globalMin; gf <= _globalMax; gf++)
        {
            currentGlobalFrame = gf;
            ApplyGlobalFrame(gf);
            yield return new WaitForSeconds(wait);
        }
    }

    void ApplyGlobalFrame(int gf)
    {
        // 1) 포즈 업데이트
        foreach (var a in _agents)
        {
            if (a == null || a.tr == null) continue;

            if (a.posByFrame.TryGetValue(gf, out var pos))
            {
                Vector3 dir = GetDirectionAtFrame(a, gf, pos);

                if (!a.isActiveCached)
                {
                    if (dir != Vector3.zero)
                        a.tr.rotation = Quaternion.LookRotation(dir);

                    a.tr.position = pos;
                    a.go.SetActive(true);
                    a.isActiveCached = true;
                }
                else
                {
                    a.tr.position = pos;

                    if (dir != Vector3.zero)
                        a.tr.rotation = Quaternion.Slerp(a.tr.rotation, Quaternion.LookRotation(dir), 15f * Time.deltaTime);
                }
            }
            else
            {
                if (a.isActiveCached)
                {
                    a.go.SetActive(false);
                    a.isActiveCached = false;
                }

                a.hasDebug = false;
                a.label = "";
            }
        }

        if (!debugEnabled) return;

        if (syncTransformsBeforeQuery)
            Physics.SyncTransforms();

        UpdateDebugInfo();
    }

    Vector3 GetDirectionAtFrame(AgentTrack a, int gf, Vector3 pos)
    {
        Vector3 dir = Vector3.zero;

        if (a.nextFrameOf.TryGetValue(gf, out int nextGf) && nextGf != int.MinValue
            && a.posByFrame.TryGetValue(nextGf, out var next))
        {
            dir = (next - pos).normalized;
        }
        else if (a.prevFrameOf.TryGetValue(gf, out int prevGf) && prevGf != int.MinValue
            && a.posByFrame.TryGetValue(prevGf, out var prev))
        {
            dir = (pos - prev).normalized;
        }

        return dir;
    }

    // ===================== Debug Core =====================
    void UpdateDebugInfo()
    {
        bool wantDistanceOnly = modeDistanceOnly;

        // 특정 대상만 표시 모드
        bool wantAgent = !wantDistanceOnly && modeAgentOnly;
        bool wantBuilding = !wantDistanceOnly && modeBuildingOnly;
        bool wantObstacle = !wantDistanceOnly && modeObstacleOnly;
        bool wantVehicle = !wantDistanceOnly && modeVehicleOnly;

        // 거리 모드면 전부 계산
        bool calcAgent = wantDistanceOnly || wantAgent;
        bool calcBuilding = wantDistanceOnly || wantBuilding;
        bool calcObstacle = wantDistanceOnly || wantObstacle;
        bool calcVehicle = wantDistanceOnly || wantVehicle;

        float qR = _queryRadiusMaxNear; // ✅ per-type near 중 최대 반경으로 한 번에 후보 수집

        for (int i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a == null || a.tr == null || !a.isActiveCached) continue;

            a.nearestAgentDist = -1f;
            a.nearestBuildingDist = -1f;
            a.nearestObstacleDist = -1f;
            a.nearestVehicleDist = -1f;

            a.nearestAgentName = "";
            a.nearestBuildingName = "";
            a.nearestObstacleName = "";
            a.nearestVehicleName = "";

            Vector3 p = a.tr.position;

            int n = Physics.OverlapSphereNonAlloc(
                p,
                qR,
                _overlapBuf,
                queryMask,
                triggerInteraction
            );

            float bestAgent = float.PositiveInfinity;
            float bestBld = float.PositiveInfinity;
            float bestObs = float.PositiveInfinity;
            float bestVeh = float.PositiveInfinity;

            string bestAgentName = "";
            string bestBldName = "";
            string bestObsName = "";
            string bestVehName = "";

            for (int k = 0; k < n; k++)
            {
                var c = _overlapBuf[k];
                if (c == null) continue;

                Transform ct = c.transform;
                if (ct == null) continue;

                // 자기 자신 제외
                if (ct == a.tr || ct.IsChildOf(a.tr)) continue;

                // 1) Agent 후보 (center distance)
                if (calcAgent && TryGetAgentOwner(ct, out var other) && other != null && other != a)
                {
                    float d = Vector3.Distance(p, other.tr.position);
                    if (d < bestAgent)
                    {
                        bestAgent = d;
                        bestAgentName = other.name;
                    }
                }

                // 2) Building 후보 (closest point distance)
                if (calcBuilding && HasTagInHierarchy(ct, TAG_BUILDING))
                {
                    float d = DistanceToCollider(p, c);
                    if (d < bestBld)
                    {
                        bestBld = d;
                        bestBldName = GetTaggedRootName(ct, TAG_BUILDING);
                        if (string.IsNullOrEmpty(bestBldName)) bestBldName = c.name;
                    }
                }

                // 3) Obstacle 후보 (closest point distance)
                if (calcObstacle && HasTagInHierarchy(ct, TAG_OBSTACLE))
                {
                    float d = DistanceToCollider(p, c);
                    if (d < bestObs)
                    {
                        bestObs = d;
                        bestObsName = GetTaggedRootName(ct, TAG_OBSTACLE);
                        if (string.IsNullOrEmpty(bestObsName)) bestObsName = c.name;
                    }
                }

                // 4) Vehicle 후보 (closest point distance)
                if (calcVehicle && HasTagInHierarchy(ct, TAG_VEHICLE))
                {
                    float d = DistanceToCollider(p, c);
                    if (d < bestVeh)
                    {
                        bestVeh = d;
                        bestVehName = GetTaggedRootName(ct, TAG_VEHICLE);
                        if (string.IsNullOrEmpty(bestVehName)) bestVehName = c.name;
                    }
                }
            }

            // ✅ per-type "near" 반경을 적용해서 저장(반경 밖이면 -1로 취급)
            if (bestAgent < float.PositiveInfinity && bestAgent <= nearRadiusAgent)
            {
                a.nearestAgentDist = bestAgent;
                a.nearestAgentName = bestAgentName;
            }

            if (bestBld < float.PositiveInfinity && bestBld <= nearRadiusBuilding)
            {
                a.nearestBuildingDist = bestBld;
                a.nearestBuildingName = bestBldName;
            }

            if (bestObs < float.PositiveInfinity && bestObs <= nearRadiusObstacle)
            {
                a.nearestObstacleDist = bestObs;
                a.nearestObstacleName = bestObsName;
            }

            if (bestVeh < float.PositiveInfinity && bestVeh <= nearRadiusVehicle)
            {
                a.nearestVehicleDist = bestVeh;
                a.nearestVehicleName = bestVehName;
            }

            // 라벨 생성: "대상이 하나도 없으면 빈 글씨"
            a.hasDebug = false;
            a.label = "";

            if (wantDistanceOnly)
            {
                List<string> lines = new List<string>(4);
                if (a.nearestAgentDist >= 0f) lines.Add($"Agent d={a.nearestAgentDist:0.00}");
                if (a.nearestBuildingDist >= 0f) lines.Add($"Bld d={a.nearestBuildingDist:0.00}");
                if (a.nearestObstacleDist >= 0f) lines.Add($"Obs d={a.nearestObstacleDist:0.00}");
                if (a.nearestVehicleDist >= 0f) lines.Add($"Veh d={a.nearestVehicleDist:0.00}");

                if (lines.Count > 0)
                {
                    a.hasDebug = true;
                    a.color = Color.white;
                    a.label = string.Join("\n", lines);
                }
            }
            else
            {
                // 여러 모드가 동시에 켜져 있으면 마지막으로 걸린 게 표시됨(원래 스타일 유지)

                if (wantAgent && a.nearestAgentDist >= 0f)
                {
                    a.hasDebug = true;
                    a.color = new Color(0.2f, 0.6f, 1.0f, 1f); // blue
                    string hitMark = (a.nearestAgentDist <= hitRadiusAgent) ? "HIT" : "NEAR";
                    a.label = $"{hitMark}: {a.nearestAgentName}\nd={a.nearestAgentDist:0.00}";
                }

                if (wantBuilding && a.nearestBuildingDist >= 0f)
                {
                    a.hasDebug = true;
                    a.color = Color.red;
                    string hitMark = (a.nearestBuildingDist <= hitRadiusBuilding) ? "HIT" : "NEAR";
                    a.label = $"{hitMark}: {a.nearestBuildingName}\nd={a.nearestBuildingDist:0.00}";
                }

                if (wantObstacle && a.nearestObstacleDist >= 0f)
                {
                    a.hasDebug = true;
                    a.color = Color.yellow;
                    string hitMark = (a.nearestObstacleDist <= hitRadiusObstacle) ? "HIT" : "NEAR";
                    a.label = $"{hitMark}: {a.nearestObstacleName}\nd={a.nearestObstacleDist:0.00}";
                }

                if (wantVehicle && a.nearestVehicleDist >= 0f)
                {
                    a.hasDebug = true;
                    a.color = Color.green;
                    string hitMark = (a.nearestVehicleDist <= hitRadiusVehicle) ? "HIT" : "NEAR";
                    a.label = $"{hitMark}: {a.nearestVehicleName}\nd={a.nearestVehicleDist:0.00}";
                }
            }
        }
    }

    bool TryGetAgentOwner(Transform t, out AgentTrack owner)
    {
        owner = null;
        var cur = t;
        while (cur != null)
        {
            if (_agentByRoot.TryGetValue(cur, out owner))
                return true;
            cur = cur.parent;
        }
        return false;
    }

    bool HasTagInHierarchy(Transform t, string tag)
    {
        var cur = t;
        while (cur != null)
        {
            var go = cur.gameObject;
            if (go != null && go.CompareTag(tag))
                return true;
            cur = cur.parent;
        }
        return false;
    }

    string GetTaggedRootName(Transform t, string tag)
    {
        // tag 달린 가장 위쪽(부모 방향) 오브젝트 이름을 반환
        Transform found = null;
        var cur = t;
        while (cur != null)
        {
            var go = cur.gameObject;
            if (go != null && go.CompareTag(tag))
                found = cur;

            cur = cur.parent;
        }
        return found != null ? found.name : "";
    }

    static float DistanceToCollider(Vector3 pos, Collider c)
    {
        Vector3 cp = c.ClosestPoint(pos);
        return Vector3.Distance(pos, cp);
    }
    // ======================================================

    void OnDrawGizmos()
    {
        if (!debugEnabled) return;
        if (_agents == null || _agents.Count == 0) return;

#if UNITY_EDITOR
        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                richText = false,
                fontSize = 12
            };
        }
#endif

        for (int i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a == null || a.tr == null || !a.isActiveCached) continue;
            if (!a.hasDebug) continue;

            Vector3 p = a.tr.position + Vector3.up * gizmoYOffset;

            if (drawWireSphere)
            {
                // ✅ 현재 표시 중인 모드에 따라 와이어 스피어 반경도 타입별로
                float r = 0.001f;
                if (modeDistanceOnly)
                {
                    // 거리모드면 그냥 "최소 hit 중 최대"로 표시(원하면 분리 가능)
                    r = Mathf.Max(hitRadiusAgent, Mathf.Max(hitRadiusBuilding, Mathf.Max(hitRadiusObstacle, hitRadiusVehicle)));
                }
                else if (modeAgentOnly) r = hitRadiusAgent;
                else if (modeBuildingOnly) r = hitRadiusBuilding;
                else if (modeObstacleOnly) r = hitRadiusObstacle;
                else if (modeVehicleOnly) r = hitRadiusVehicle;
                else
                {
                    r = Mathf.Max(hitRadiusAgent, Mathf.Max(hitRadiusBuilding, Mathf.Max(hitRadiusObstacle, hitRadiusVehicle)));
                }

                Gizmos.color = a.color;
                Gizmos.DrawWireSphere(p, Mathf.Max(0.001f, r));
            }

#if UNITY_EDITOR
            Handles.color = a.color;
            Handles.Label(p + Vector3.up * 0.25f, a.label, _labelStyle);
#endif
        }
    }

    static Vector3 ToV3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
