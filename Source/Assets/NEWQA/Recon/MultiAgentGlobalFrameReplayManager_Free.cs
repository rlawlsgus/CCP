using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MultiAgentGlobalFrameReplayManager_Free : MonoBehaviour
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

    [Range(0.1f, 10f)]
    public float playSpeed = 1.0f;

    [Tooltip("Time.timeScale 영향 없이 재생하려면 ON")]
    public bool useRealtimeWait = true;

    [Tooltip("✅ globalFrames에 실제로 존재하는 프레임만 재생")]
    public bool playOnlyExistingFrames = true;

    // =================== WORLD-RELATIVE RECON (NO ROTATION) ===================
    [Header("RECON (World-relative, NO rotation)")]
    [Tooltip("✅ worldPos = anchor + (localCurrent - local0). local0는 chunk 시작 프레임 값(옵션)")]
    public bool subtractLocalAtChunkStart = true;

    [Tooltip("✅ chunk가 바뀌면 '직전 chunk의 끝 위치'를 새 anchor로 삼아 이어붙임 (점프 제거)")]
    public bool chainAnchorsAcrossChunks = true;

    [Tooltip("✅ 에이전트가 프레임 누락/비활성 후 다시 나타나면 체인 리셋(데이터 anchor로 복귀)")]
    public bool resetChainWhenInactive = true;

    [Tooltip("데이터 anchor와 이전 위치가 너무 멀면(텔레포트/리셋) stitch 안 하고 데이터 anchor 사용 (XZ 기준, m)")]
    public float stitchMaxErrorXZ = 0.75f;

    [Tooltip("anchor(startWorldPosition)에 더해줄 y offset")]
    public float yOffset = 0f;
    // ========================================================================

    // ================= OPTIONAL VISUAL FACING (no effect on movement) =================
    [Header("Visual Facing (optional, no effect on movement)")]
    [Tooltip("✅ 이동 방향(velocity)으로 캐릭터를 회전시킴. 이동(포지션)에는 영향 없음")]
    public bool faceMoveDirection = false;

    [Tooltip("회전 스무딩 강도. 클수록 빨리 돈다")]
    public float turnSharpness = 12f;

    [Tooltip("chunk 바뀔 때 velocity가 튀는 걸 막기 위해 이전 위치를 리셋")]
    public bool resetFacingOnChunkChange = true;

    [Tooltip("velocity 갱신 최소 이동거리(너무 작으면 방향 업데이트 안 함)")]
    public float minMoveForDir = 1e-5f;
    // ==============================================================================

    [Header("Debug")]
    public int currentGlobalFrame = 0;
    public int timelineIndex = 0;

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

    // ===================== Per-type radii =====================
    [Header("Debug Radii (per-type)")]
    public float nearRadiusAgent = 6.0f;
    public float hitRadiusAgent = 0.7f;

    public float nearRadiusBuilding = 6.0f;
    public float hitRadiusBuilding = 0.7f;

    public float nearRadiusObstacle = 6.0f;
    public float hitRadiusObstacle = 0.7f;

    public float nearRadiusVehicle = 6.0f;
    public float hitRadiusVehicle = 0.7f;

    [Tooltip("OverlapSphere에 쓸 반경은 자동으로 (near들 중 최대)로 계산됨")]
    [SerializeField] private float _queryRadiusMaxNear = 6.0f;

    [Header("Debug Query")]
    public LayerMask queryMask = ~0;

    public bool syncTransformsBeforeQuery = true;

    public float gizmoYOffset = 0.2f;
    public bool drawWireSphere = true;

    [Header("Tag Names")]
    public string TAG_BUILDING = "Building";
    public string TAG_OBSTACLE = "Obstacle";
    public string TAG_VEHICLE = "Vehicle";

    // ===================== SphereCast Debug Settings =====================
    [Header("SphereCast Debug (Collider-based)")]
    [Tooltip("SphereCast 시작점 y 오프셋(에이전트 허리/가슴쯤). 위/아래 길쭉한 객체에서 옆면을 보게 하는 핵심")]
    public float sphereCastOriginYOffset = 0.9f;

    [Tooltip("SphereCast probe 반경(에이전트 몸통/센서 두께). 너무 작으면 miss, 너무 크면 너무 일찍 hit")]
    public float sphereCastProbeRadius = 0.25f;

    [Tooltip("SphereCast가 타겟 콜라이더를 못 맞추면(오목 메쉬 등) fallback으로 HorizontalDistance(closestpoint) 사용")]
    public bool fallbackToHorizontalDistanceIfMiss = true;

    // --- JSON 구조 ---
    [Serializable]
    public class ChunkDataRaw
    {
        public int chunk_index;
        public int start_index;
        public int[] globalFrames;
        public float[] startWorldPosition;

        // 있어도 movement에는 사용 안 함 (회전 무시)
        public float[] startForward;

        // ✅ world 좌표축 기준 상대 이동(회전 없이 누적)
        public float[][] localCurrent;

        public float[] goalWorldPosition;
    }

    class AgentTrack
    {
        public string name;
        public GameObject go;
        public Transform tr;

        // ✅ agent collider (HIT 겹침 판정용)
        public Collider mainCollider;

        // frame -> localCurrent
        public Dictionary<int, Vector3> localByFrame = new Dictionary<int, Vector3>();

        // frame -> chunkStartFrame
        public Dictionary<int, int> chunkStartOfFrame = new Dictionary<int, int>();

        // chunkStartFrame -> data anchor(world)
        public Dictionary<int, Vector3> anchorByChunkStart = new Dictionary<int, Vector3>();

        // neighbors
        public Dictionary<int, int> nextFrameOf = new Dictionary<int, int>();
        public Dictionary<int, int> prevFrameOf = new Dictionary<int, int>();

        public bool isActiveCached = false;

        // ===== runtime (chain) =====
        public int activeChunkStart = int.MinValue;
        public Vector3 runtimeAnchorPos = Vector3.zero;
        public Vector3 runtimeLocal0 = Vector3.zero;

        public bool wasActiveLastFrame = false;
        public Vector3 lastWorldPos = Vector3.zero;

        // ===== optional visual facing =====
        public bool hasPrevWorld = false;
        public Vector3 prevWorldPos = Vector3.zero;
        public int prevWorldGf = int.MinValue;
        // =============================

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

    private readonly HashSet<int> _allFrames = new HashSet<int>();
    private List<int> _timeline = new List<int>();

    private readonly Collider[] _overlapBuf = new Collider[512];

    // ✅ SphereCast hits buffer (NonAlloc)
    private RaycastHit[] _sphereHitBuf = new RaycastHit[256];

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

        sphereCastProbeRadius = Mathf.Max(0.0001f, sphereCastProbeRadius);
        stitchMaxErrorXZ = Mathf.Max(0f, stitchMaxErrorXZ);

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
        BuildTimeline();

        Debug.Log($"[Replay] agents={_agents.Count} timelineCount={_timeline.Count} range=[{_globalMin}..{_globalMax}] playOnlyExistingFrames={playOnlyExistingFrames}");

        if (_timeline.Count > 0)
        {
            timelineIndex = 0;
            currentGlobalFrame = _timeline[0];

            ApplyGlobalFrame(currentGlobalFrame);

            if (playOnStart)
                StartCoroutine(CoPlayTimeline());
        }
        else
        {
            Debug.LogError("[Replay] No frames loaded. Check file schema / folder path.");
        }
    }

    void BuildTimeline()
    {
        if (playOnlyExistingFrames)
            _timeline = _allFrames.OrderBy(x => x).ToList();
        else
        {
            _timeline = new List<int>();
            for (int gf = _globalMin; gf <= _globalMax; gf++)
                _timeline.Add(gf);
        }
    }

    void LoadAll()
    {
        _agents.Clear();
        _agentByRoot.Clear();
        _allFrames.Clear();

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

            EnsureAgentMainCollider(a);

            int totalLines = 0, okLines = 0, badLines = 0, framesLoaded = 0;
            string firstBad = null;

            try
            {
                foreach (var rawLine0 in File.ReadLines(file))
                {
                    totalLines++;
                    if (string.IsNullOrWhiteSpace(rawLine0)) continue;

                    string rawLine = rawLine0.Trim().TrimStart('\ufeff');
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;

                    string line = rawLine;
                    if (!line.StartsWith("{")) line = "{" + line;
                    if (!line.EndsWith("}")) line = line + "}";

                    ChunkDataRaw raw;
                    try { raw = JsonConvert.DeserializeObject<ChunkDataRaw>(line); }
                    catch
                    {
                        badLines++;
                        if (firstBad == null) firstBad = rawLine.Substring(0, Mathf.Min(200, rawLine.Length));
                        continue;
                    }

                    if (raw == null) { badLines++; continue; }
                    if (raw.globalFrames == null || raw.localCurrent == null) { badLines++; continue; }
                    if (raw.globalFrames.Length == 0 || raw.localCurrent.Length == 0) { badLines++; continue; }
                    if (raw.globalFrames.Length != raw.localCurrent.Length) { badLines++; continue; }
                    if (raw.startWorldPosition == null || raw.startWorldPosition.Length < 3) { badLines++; continue; }

                    okLines++;
                    framesLoaded += raw.globalFrames.Length;

                    // ✅ data anchor (yOffset 반영)
                    Vector3 dataAnchor = ToV3(raw.startWorldPosition, Vector3.zero, lockY: true, forcedY: yOffset);

                    int chunkStartGf = raw.globalFrames[0];
                    a.anchorByChunkStart[chunkStartGf] = dataAnchor;

                    for (int i = 0; i < raw.globalFrames.Length; i++)
                    {
                        int gf = raw.globalFrames[i];

                        // localCurrent는 y=0 강제 (필요하면 lockY=false로 바꿔)
                        Vector3 lc = ToV3(raw.localCurrent[i], Vector3.zero, lockY: true, forcedY: 0f);

                        a.localByFrame[gf] = lc;
                        a.chunkStartOfFrame[gf] = chunkStartGf;

                        _allFrames.Add(gf);
                        _globalMin = Mathf.Min(_globalMin, gf);
                        _globalMax = Mathf.Max(_globalMax, gf);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed reading {file}\n{e}");
                Destroy(a.go);
                continue;
            }

            Debug.Log($"[ReplayLoad] {Path.GetFileName(file)} lines={totalLines} ok={okLines} bad={badLines} framesLoaded={framesLoaded}"
                      + (firstBad != null ? $"\n  firstBad: {firstBad}" : ""));

            BuildNeighborMaps(a);

            InitializeAgentPoseToFirstDataAnchor(a);

            a.go.SetActive(false);
            a.isActiveCached = false;

            _agents.Add(a);
            _agentByRoot[a.tr] = a;
        }

        if (_globalMin == int.MaxValue) _globalMin = 0;
        if (_globalMax == int.MinValue) _globalMax = -1;

        Debug.Log($"[Replay] Loaded {_agents.Count} agents. GlobalFrames range = [{_globalMin}..{_globalMax}]");
    }

    void EnsureAgentMainCollider(AgentTrack a)
    {
        if (a == null || a.go == null) return;

        var col = a.go.GetComponentInChildren<Collider>(true);
        if (col == null)
        {
            var cc = a.go.AddComponent<CapsuleCollider>();
            cc.center = new Vector3(0f, 1.0f, 0f);
            cc.height = 2.0f;
            cc.radius = Mathf.Max(0.05f, sphereCastProbeRadius);
            col = cc;
        }

        a.mainCollider = col;
    }

    void InitializeAgentPoseToFirstDataAnchor(AgentTrack a)
    {
        if (a == null || a.tr == null) return;
        if (a.anchorByChunkStart == null || a.anchorByChunkStart.Count == 0) return;

        int firstChunkStart = a.anchorByChunkStart.Keys.Min();
        Vector3 anc = a.anchorByChunkStart[firstChunkStart];

        a.tr.position = anc;
        // ✅ 회전은 건드리지 않음(원하면 identity로 고정해도 됨)
        // a.tr.rotation = Quaternion.identity;

        a.lastWorldPos = a.tr.position;
        a.wasActiveLastFrame = false;

        a.hasPrevWorld = false;
        a.prevWorldGf = int.MinValue;

        a.activeChunkStart = int.MinValue;
    }

    void BuildNeighborMaps(AgentTrack a)
    {
        a.nextFrameOf.Clear();
        a.prevFrameOf.Clear();

        if (a.localByFrame == null || a.localByFrame.Count == 0) return;

        var frames = a.localByFrame.Keys.OrderBy(x => x).ToList();
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

        int start = 0;
        int count = 6;
        int endExclusive = Mathf.Min(go.transform.childCount, start + count);
        for (int i = start; i < endExclusive; i++)
            go.transform.GetChild(i).gameObject.SetActive(false);

        if (endExclusive > start)
        {
            int idx = UnityEngine.Random.Range(start, endExclusive);
            go.transform.GetChild(idx).gameObject.SetActive(true);
        }

        foreach (var r in go.GetComponentsInChildren<AgentChunkReplayer>(true))
            r.enabled = false;

        return go;
    }

    IEnumerator CoPlayTimeline()
    {
        float wait = timePerFrame / Mathf.Max(0.0001f, playSpeed);
        Debug.Log($"[Replay] Play start timelineCount={_timeline.Count} wait={wait}");

        for (int i = 0; i < _timeline.Count; i++)
        {
            timelineIndex = i;
            int gf = _timeline[i];

            currentGlobalFrame = gf;
            ApplyGlobalFrame(gf);

            if (useRealtimeWait) yield return new WaitForSecondsRealtime(wait);
            else yield return new WaitForSeconds(wait);
        }

        Debug.Log("[Replay] Play done");
    }

    void ApplyGlobalFrame(int gf)
    {
        foreach (var a in _agents)
        {
            if (a == null || a.tr == null) continue;

            // ===== no data at this gf =====
            if (!a.localByFrame.TryGetValue(gf, out var lcNow))
            {
                if (a.isActiveCached)
                {
                    a.go.SetActive(false);
                    a.isActiveCached = false;
                }

                if (resetChainWhenInactive)
                {
                    a.activeChunkStart = int.MinValue;
                }

                a.wasActiveLastFrame = false;

                // facing reset
                a.hasPrevWorld = false;
                a.prevWorldGf = int.MinValue;

                a.hasDebug = false;
                a.label = "";
                continue;
            }

            // activate
            if (!a.isActiveCached)
            {
                a.go.SetActive(true);
                a.isActiveCached = true;
            }

            // frame이 속한 chunkStart
            if (!a.chunkStartOfFrame.TryGetValue(gf, out int chunkStart))
                chunkStart = gf;

            bool chunkChanged = (a.activeChunkStart != chunkStart);

            // data anchor
            a.anchorByChunkStart.TryGetValue(chunkStart, out var dataAnchor);

            if (chunkChanged)
            {
                bool isFirstChunkEver = (a.activeChunkStart == int.MinValue);

                bool chainOk = chainAnchorsAcrossChunks
                               && !isFirstChunkEver
                               && (!resetChainWhenInactive || a.wasActiveLastFrame);

                a.activeChunkStart = chunkStart;

                // local0 보정
                a.runtimeLocal0 = subtractLocalAtChunkStart ? lcNow : Vector3.zero;

                // chunk 시작 relLocal (보통 0)
                Vector3 relStart = lcNow - a.runtimeLocal0;

                // ✅ stitch anchor: 새 chunk 첫 프레임 worldPos가 lastWorldPos랑 이어지도록
                Vector3 anchorChosen = dataAnchor;

                if (chainOk)
                {
                    // 데이터 anchor와 이전 위치 차이가 너무 큰 경우는 stitch 하지 않음(텔레포트/리셋)
                    float errXZ = Vector2.Distance(
                        new Vector2(dataAnchor.x, dataAnchor.z),
                        new Vector2(a.lastWorldPos.x, a.lastWorldPos.z)
                    );

                    if (errXZ <= stitchMaxErrorXZ)
                        anchorChosen = a.lastWorldPos - relStart;
                    else
                        anchorChosen = dataAnchor;
                }

                a.runtimeAnchorPos = anchorChosen;

                if (resetFacingOnChunkChange)
                {
                    a.hasPrevWorld = false;
                    a.prevWorldGf = int.MinValue;
                }
            }

            // ===== RECON (NO ROTATION) =====
            Vector3 relLocal = lcNow - a.runtimeLocal0;
            Vector3 worldPos = a.runtimeAnchorPos + relLocal;

            // ===== OPTIONAL VISUAL FACING ONLY =====
            if (faceMoveDirection)
            {
                if (!a.hasPrevWorld)
                {
                    a.hasPrevWorld = true;
                    a.prevWorldPos = worldPos;
                    a.prevWorldGf = gf;
                }
                else
                {
                    int gap = Mathf.Max(1, gf - a.prevWorldGf);
                    float dt = gap * timePerFrame;

                    Vector3 vel = (worldPos - a.prevWorldPos);
                    vel.y = 0f;

                    if (vel.sqrMagnitude > (minMoveForDir * minMoveForDir))
                    {
                        Quaternion targetRot = Quaternion.LookRotation(vel.normalized, Vector3.up);
                        float alpha = 1f - Mathf.Exp(-turnSharpness * Mathf.Max(0.0001f, dt));
                        a.tr.rotation = Quaternion.Slerp(a.tr.rotation, targetRot, alpha);
                    }

                    a.prevWorldPos = worldPos;
                    a.prevWorldGf = gf;
                }
            }

            a.tr.position = worldPos;

            a.lastWorldPos = worldPos;
            a.wasActiveLastFrame = true;
        }

        if (!debugEnabled) return;

        if (syncTransformsBeforeQuery)
            Physics.SyncTransforms();

        UpdateDebugInfo();
    }

    // ===================== Debug Core (SphereCast 기반) =====================
    void UpdateDebugInfo()
    {
        QueryTriggerInteraction qti = QueryTriggerInteraction.Collide;

        bool wantDistanceOnly = modeDistanceOnly;

        bool wantAgent = !wantDistanceOnly && modeAgentOnly;
        bool wantBuilding = !wantDistanceOnly && modeBuildingOnly;
        bool wantObstacle = !wantDistanceOnly && modeObstacleOnly;
        bool wantVehicle = !wantDistanceOnly && modeVehicleOnly;

        bool calcAgent = wantDistanceOnly || wantAgent;
        bool calcBuilding = wantDistanceOnly || wantBuilding;
        bool calcObstacle = wantDistanceOnly || wantObstacle;
        bool calcVehicle = wantDistanceOnly || wantVehicle;

        float qR = _queryRadiusMaxNear;

        for (int i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a == null || a.tr == null || !a.isActiveCached) continue;

            if (a.mainCollider == null) EnsureAgentMainCollider(a);

            a.nearestAgentDist = -1f;
            a.nearestBuildingDist = -1f;
            a.nearestObstacleDist = -1f;
            a.nearestVehicleDist = -1f;

            a.nearestAgentName = "";
            a.nearestBuildingName = "";
            a.nearestObstacleName = "";
            a.nearestVehicleName = "";

            Vector3 p = a.tr.position;

            int n = Physics.OverlapSphereNonAlloc(p, qR, _overlapBuf, queryMask, qti);

            float bestAgent = float.PositiveInfinity;
            float bestBld = float.PositiveInfinity;
            float bestObs = float.PositiveInfinity;
            float bestVeh = float.PositiveInfinity;

            string bestAgentName = "";
            string bestBldName = "";
            string bestObsName = "";
            string bestVehName = "";

            Vector3 castOrigin = p + Vector3.up * sphereCastOriginYOffset;

            for (int k = 0; k < n; k++)
            {
                var c = _overlapBuf[k];
                if (c == null) continue;

                Transform ct = c.transform;
                if (ct == null) continue;

                if (ct == a.tr || ct.IsChildOf(a.tr)) continue;

                // ---------- Agent ----------
                if (calcAgent && TryGetAgentOwner(ct, out var other) && other != null && other != a)
                {
                    if (other.mainCollider == null) EnsureAgentMainCollider(other);

                    bool overlapping = IsOverlapping(a.mainCollider, other.mainCollider);
                    float d = 0f;

                    if (!overlapping)
                    {
                        bool ok = SphereCastDistanceToColliderXZ(
                            castOrigin,
                            sphereCastProbeRadius,
                            other.mainCollider,
                            nearRadiusAgent,
                            queryMask,
                            qti,
                            out d
                        );

                        if (!ok && fallbackToHorizontalDistanceIfMiss)
                        {
                            d = HorizontalDistanceToCollider(p, other.mainCollider);
                            ok = true;
                        }

                        if (!ok) continue;
                    }

                    if (d < bestAgent)
                    {
                        bestAgent = d;
                        bestAgentName = other.name;
                    }

                    continue;
                }

                // ---------- Building ----------
                if (calcBuilding && HasTagInHierarchy(ct, TAG_BUILDING))
                {
                    float approx = HorizontalDistanceToBoundsXZ(p, c.bounds);
                    if (approx > nearRadiusBuilding + sphereCastProbeRadius) continue;

                    bool overlapping = IsOverlapping(a.mainCollider, c);
                    float d = 0f;

                    if (!overlapping)
                    {
                        bool ok = SphereCastDistanceToColliderXZ(
                            castOrigin,
                            sphereCastProbeRadius,
                            c,
                            nearRadiusBuilding,
                            queryMask,
                            qti,
                            out d
                        );

                        if (!ok && fallbackToHorizontalDistanceIfMiss)
                        {
                            d = HorizontalDistanceToCollider(p, c);
                            ok = true;
                        }

                        if (!ok) continue;
                    }

                    if (d < bestBld)
                    {
                        bestBld = d;
                        bestBldName = GetTaggedRootName(ct, TAG_BUILDING);
                        if (string.IsNullOrEmpty(bestBldName)) bestBldName = c.name;
                    }

                    continue;
                }

                // ---------- Obstacle ----------
                if (calcObstacle && HasTagInHierarchy(ct, TAG_OBSTACLE))
                {
                    float approx = HorizontalDistanceToBoundsXZ(p, c.bounds);
                    if (approx > nearRadiusObstacle + sphereCastProbeRadius) continue;

                    bool overlapping = IsOverlapping(a.mainCollider, c);
                    float d = 0f;

                    if (!overlapping)
                    {
                        bool ok = SphereCastDistanceToColliderXZ(
                            castOrigin,
                            sphereCastProbeRadius,
                            c,
                            nearRadiusObstacle,
                            queryMask,
                            qti,
                            out d
                        );

                        if (!ok && fallbackToHorizontalDistanceIfMiss)
                        {
                            d = HorizontalDistanceToCollider(p, c);
                            ok = true;
                        }

                        if (!ok) continue;
                    }

                    if (d < bestObs)
                    {
                        bestObs = d;
                        bestObsName = GetTaggedRootName(ct, TAG_OBSTACLE);
                        if (string.IsNullOrEmpty(bestObsName)) bestObsName = c.name;
                    }

                    continue;
                }

                // ---------- Vehicle ----------
                if (calcVehicle && HasTagInHierarchy(ct, TAG_VEHICLE))
                {
                    float approx = HorizontalDistanceToBoundsXZ(p, c.bounds);
                    if (approx > nearRadiusVehicle + sphereCastProbeRadius) continue;

                    bool overlapping = IsOverlapping(a.mainCollider, c);
                    float d = 0f;

                    if (!overlapping)
                    {
                        bool ok = SphereCastDistanceToColliderXZ(
                            castOrigin,
                            sphereCastProbeRadius,
                            c,
                            nearRadiusVehicle,
                            queryMask,
                            qti,
                            out d
                        );

                        if (!ok && fallbackToHorizontalDistanceIfMiss)
                        {
                            d = HorizontalDistanceToCollider(p, c);
                            ok = true;
                        }

                        if (!ok) continue;
                    }

                    if (d < bestVeh)
                    {
                        bestVeh = d;
                        bestVehName = GetTaggedRootName(ct, TAG_VEHICLE);
                        if (string.IsNullOrEmpty(bestVehName)) bestVehName = c.name;
                    }

                    continue;
                }
            }

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
                if (wantAgent && a.nearestAgentDist >= 0f)
                {
                    a.hasDebug = true;
                    a.color = new Color(0.2f, 0.6f, 1.0f, 1f);
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
                float r = 0.001f;
                if (modeDistanceOnly)
                    r = Mathf.Max(hitRadiusAgent, Mathf.Max(hitRadiusBuilding, Mathf.Max(hitRadiusObstacle, hitRadiusVehicle)));
                else if (modeAgentOnly) r = hitRadiusAgent;
                else if (modeBuildingOnly) r = hitRadiusBuilding;
                else if (modeObstacleOnly) r = hitRadiusObstacle;
                else if (modeVehicleOnly) r = hitRadiusVehicle;
                else
                    r = Mathf.Max(hitRadiusAgent, Mathf.Max(hitRadiusBuilding, Mathf.Max(hitRadiusObstacle, hitRadiusVehicle)));

                Gizmos.color = a.color;
                Gizmos.DrawWireSphere(p, Mathf.Max(0.001f, r));
            }

#if UNITY_EDITOR
            Handles.color = a.color;
            Handles.Label(p + Vector3.up * 0.25f, a.label, _labelStyle);
#endif
        }
    }

    // ===================== Collider helpers =====================

    bool SphereCastDistanceToColliderXZ(
        Vector3 origin,
        float radius,
        Collider target,
        float maxDistance,
        LayerMask mask,
        QueryTriggerInteraction qti,
        out float bestDistance)
    {
        bestDistance = float.PositiveInfinity;
        if (target == null) return false;

        Vector3 tc = target.bounds.center;
        Vector3 to = new Vector3(tc.x - origin.x, 0f, tc.z - origin.z);
        float len2 = to.sqrMagnitude;
        if (len2 < 1e-10f) return false;

        Vector3 dir = to / Mathf.Sqrt(len2);

        int hitCount = Physics.SphereCastNonAlloc(origin, radius, dir, _sphereHitBuf, maxDistance, mask, qti);
        bool found = false;

        for (int i = 0; i < hitCount; i++)
        {
            var h = _sphereHitBuf[i];
            if (h.collider == null) continue;

            if (h.collider == target)
            {
                if (h.distance < bestDistance)
                {
                    bestDistance = h.distance;
                    found = true;
                }
            }
        }

        return found;
    }

    static bool IsOverlapping(Collider a, Collider b)
    {
        if (!a || !b) return false;

        return Physics.ComputePenetration(
            a, a.transform.position, a.transform.rotation,
            b, b.transform.position, b.transform.rotation,
            out _, out _
        );
    }

    static float HorizontalDistanceToCollider(Vector3 pos, Collider c)
    {
        var b = c.bounds;
        float y = Mathf.Clamp(pos.y, b.min.y, b.max.y);
        Vector3 query = new Vector3(pos.x, y, pos.z);

        Vector3 cp = c.ClosestPoint(query);

        Vector2 p2 = new Vector2(pos.x, pos.z);
        Vector2 cp2 = new Vector2(cp.x, cp.z);
        return Vector2.Distance(p2, cp2);
    }

    static float HorizontalDistanceToBoundsXZ(Vector3 pos, Bounds b)
    {
        float dx = 0f;
        if (pos.x < b.min.x) dx = b.min.x - pos.x;
        else if (pos.x > b.max.x) dx = pos.x - b.max.x;

        float dz = 0f;
        if (pos.z < b.min.z) dz = b.min.z - pos.z;
        else if (pos.z > b.max.z) dz = pos.z - b.max.z;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static Vector3 ToV3(float[] arr, Vector3 fallback, bool lockY = false, float forcedY = 0f)
    {
        if (arr == null || arr.Length < 3) return fallback;
        float y = lockY ? forcedY : arr[1];
        return new Vector3(arr[0], y, arr[2]);
    }
}
