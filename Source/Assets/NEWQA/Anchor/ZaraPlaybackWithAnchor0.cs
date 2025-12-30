using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

public class ZaraPlaybackWithAnchor0 : MonoBehaviour
{
    // =========================
    // Input
    // =========================
    [Header("Input (TextAsset)")]
    public TextAsset trajectoriesTxt;   // id,x,y,frame,angle (csv lines)
    public TextAsset groupsTxt;         // "1 2 3" per line
    public TextAsset homographyTxt;     // optional 3x3

    // =========================
    // Playback (for NPCs only)
    // =========================
    [Header("Playback (NPCs Only)")]
    [Min(1f)] public float fps = 25f;
    public float playbackSpeed = 1f;
    public bool loop = true;
    public bool playOnStart = true;

    // =========================
    // World Mapping
    // =========================
    [Header("World Mapping")]
    public bool useHomography = true;
    public bool flipX = false;
    public bool flipZ = false;
    public float worldScale = 1f;
    public Vector3 worldOffset = Vector3.zero;

    // =========================
    // NPC Agents
    // =========================
    [Header("NPC Agents (id != anchorId)")]
    public GameObject npcPrefab;
    [Min(0.01f)] public float npcSize = 0.15f;
    public bool applyYawFromAngle = false;
    public float yawOffsetDeg = 0f;

    [Header("Animation (NPCs, optional)")]
    public bool driveAnimatorBlend = false;
    public string blendParamName = "Blend";
    public float speedForFullWalk = 1.2f;
    public float blendDampTime = 0.12f;
    public float speedEpsilon = 0.01f;

    // =========================
    // Groups Gizmos (optional)
    // =========================
    [Header("Group Gizmos")]
    public bool drawGroupRays = true;
    public bool drawOnlyWhenBothVisible = true;
    public float rayY = 0.05f;
    public bool alsoDrawAgentGizmos = true;
    public float gizmoAgentRadius = 0.05f;

    public enum GroupLinkMode { AllPairs, Chain }
    public GroupLinkMode linkMode = GroupLinkMode.AllPairs;

    // =========================
    // Anchor (id == anchorId)
    // =========================
    [Header("Anchor (Server Controlled)")]
    public int anchorId = 0;
    public GameObject anchorPrefab;
    public string anchorNameIfExisting = "ped_0";
    public bool spawnAnchorIfMissing = true;

    [Header("Anchor Goal")]
    public Transform goalOverride;
    public bool createGoalObjectIfMissing = true;
    public string goalObjectName = "AnchorGoal";

    [Header("Anchor Server Settings")]
    [SerializeField] private string serverUrl = "http://163.152.162.171:8000/predict";
    public int requestTimeoutSec = 5;

    [Header("Anchor Movement Settings")]
    public float anchorMoveSpeed = 2.0f;      // (현재 코드는 서버 좌표 그대로 찍는 방식이라 사실상 미사용)
    public float anchorTurnSpeed = 5.0f;      // (현재 코드는 step마다 LookRotation으로 즉시 회전)
    public float goalReachThreshold = 1.0f;
    public float moveDurationPerStep = 0.30f; // "서버 응답 1회(20step) 재생 총 시간"으로 사용 가능

    [Header("Anchor Coordinate Settings")]
    public bool flipGoalX = false;
    public bool flipGoalZ = false;
    public bool flipPredX = false;
    public bool flipPredZ = false;

    [Header("Anchor Remaining Time")]
    public float remainingTimeOverride = -1f; // >0 => fixed, else derived from frames
    private float _anchorRemainingTime = 0f;

    [Header("Anchor Camera Settings")]
    public Camera anchorCamera;
    public int imageWidth = 224;
    public int imageHeight = 224;

    // =========================
    // Anchor: Server Trajectory Playback
    // =========================
    [Header("Anchor Playback From Server (20 steps)")]
    public bool useFpsAsServerStepTime = true; // true면 stepDt=1/fps, false면 moveDurationPerStep/(실제 step 수)
    [Min(0.001f)] public float minStepDt = 0.001f;
    public float anchorYawOffsetDeg = 0f;      // 모델 forward 보정(90/-90/180 등)

    [Header("Anchor Stop Options")]
    public bool stopOnGoal = true;
    public bool deactivateAnchorOnGoal = true;

    // =========================
    // Anchor Prediction Scaling
    // =========================
    [Header("Anchor Prediction Scaling")]
    public float predScale = 1f;                 // 서버 오프셋 -> 유니티 월드 스케일 보정
    public float predEps = 1e-6f;                // (0,0) 판정 임계
    public bool predIsInAnchorLocalFrame = false; // 서버 오프셋이 앵커 로컬축 기준이면 true(보통은 false)

    // =========================
    // Internal Data Structures
    // =========================
    [Serializable]
    public class ServerResponseData
    {
        public float[][][] output;
        public float latency_sec;
        public float[] goal_position;
        public float remaining_time;
    }

    struct Sample
    {
        public int frame;
        public Vector2 xy;
        public float angleDeg;
    }

    sealed class Trajectory
    {
        public int id;
        public int[] frames;
        public Vector2[] xy;
        public float[] angles;

        public int MinFrame => frames[0];
        public int MaxFrame => frames[frames.Length - 1];

        public bool TryEvaluate(float frameF, out Vector2 pos, out float angDeg)
        {
            pos = default;
            angDeg = 0f;
            if (frames == null || frames.Length == 0) return false;
            if (frameF < frames[0] || frameF > frames[frames.Length - 1]) return false;

            int target = Mathf.FloorToInt(frameF);
            int idx = Array.BinarySearch(frames, target);
            if (idx >= 0)
            {
                pos = xy[idx];
                angDeg = angles[idx];
                return true;
            }

            idx = ~idx;
            int hi = Mathf.Clamp(idx, 0, frames.Length - 1);
            int lo = Mathf.Clamp(hi - 1, 0, frames.Length - 1);

            if (lo == hi)
            {
                pos = xy[lo];
                angDeg = angles[lo];
                return true;
            }

            float f0 = frames[lo];
            float f1 = frames[hi];
            float t = (Mathf.Approximately(f1, f0)) ? 0f : (frameF - f0) / (f1 - f0);

            pos = Vector2.LerpUnclamped(xy[lo], xy[hi], t);
            angDeg = Mathf.LerpAngle(angles[lo], angles[hi], t);
            return true;
        }
    }

    [Serializable]
    struct Matrix3x3
    {
        public float m00, m01, m02;
        public float m10, m11, m12;
        public float m20, m21, m22;

        public static Matrix3x3 Identity => new Matrix3x3(
            1, 0, 0,
            0, 1, 0,
            0, 0, 1
        );

        public Matrix3x3(
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22)
        {
            this.m00 = m00; this.m01 = m01; this.m02 = m02;
            this.m10 = m10; this.m11 = m11; this.m12 = m12;
            this.m20 = m20; this.m21 = m21; this.m22 = m22;
        }
    }

    // =========================
    // Internal State
    // =========================
    Matrix3x3 H = Matrix3x3.Identity;

    readonly Dictionary<int, Trajectory> trajById = new Dictionary<int, Trajectory>(256);

    // NPC objects only (id != anchorId)
    readonly Dictionary<int, GameObject> npcGoById = new Dictionary<int, GameObject>(256);
    readonly Dictionary<int, Animator> npcAnimById = new Dictionary<int, Animator>(256);
    readonly Dictionary<int, Vector3> npcPrevPosById = new Dictionary<int, Vector3>(256);

    // positions for gizmos (NPC + Anchor)
    readonly Dictionary<int, Vector3> currentWorldPos = new Dictionary<int, Vector3>(256);

    // groups
    readonly List<int[]> groups = new List<int[]>();
    readonly List<(int a, int b, int groupIndex)> groupEdges = new List<(int, int, int)>();

    int globalMinFrame = int.MaxValue;
    int globalMaxFrame = int.MinValue;
    float currentFrameF = 0f;
    bool isPlaying = false;

    // Anchor runtime
    GameObject anchorGO;
    Transform goalT;
    Vector3 goalWorld;
    bool anchorGoalReached = false;
    bool anchorRequestInFlight = false;

    RenderTexture anchorRT;
    Texture2D anchorTex;

    // =========================
    // Unity Lifecycle
    // =========================
    void Awake()
    {
        isPlaying = playOnStart;
    }

    void Start()
    {
        LoadAll();
        SpawnNPCs();          // id != anchorId만
        SetupAnchor();        // id == anchorId만
        ResetPlayback();

        if (anchorGO != null)
            StartCoroutine(AnchorLoop());
    }

    void Update()
    {
        if (!Application.isPlaying) return;

        // NPC playback only
        if (isPlaying)
        {
            currentFrameF += Time.deltaTime * fps * playbackSpeed;

            if (loop)
            {
                float len = Mathf.Max(1f, globalMaxFrame - globalMinFrame + 1);
                while (currentFrameF > globalMaxFrame) currentFrameF -= len;
                while (currentFrameF < globalMinFrame) currentFrameF += len;
            }
            else
            {
                if (currentFrameF >= globalMaxFrame)
                {
                    currentFrameF = globalMaxFrame;
                    isPlaying = false;
                }
                else if (currentFrameF < globalMinFrame)
                {
                    currentFrameF = globalMinFrame;
                }
            }
        }

        EvaluateAndApplyNPCTransforms();

        // ✅ remaining time은 "실제 움직일 때(20 step 재생)"만 감소해야 하므로
        // Update에서 매 프레임 깎지 않음.

        // 요청 중에는 rigidbody 있으면 정지 (선택)
        if (anchorGO != null && anchorRequestInFlight)
        {
            var rb = anchorGO.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    void OnDestroy()
    {
        if (anchorRT != null) anchorRT.Release();
        if (anchorTex != null) Destroy(anchorTex);
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (drawGroupRays)
        {
            foreach (var e in groupEdges)
            {
                if (drawOnlyWhenBothVisible)
                {
                    if (!currentWorldPos.TryGetValue(e.a, out var pa)) continue;
                    if (!currentWorldPos.TryGetValue(e.b, out var pb)) continue;

                    pa.y = rayY;
                    pb.y = rayY;

                    Gizmos.color = GroupColor(e.groupIndex);
                    Gizmos.DrawRay(pa, pb - pa);
                }
            }
        }

        if (alsoDrawAgentGizmos)
        {
            Gizmos.color = Color.white;
            foreach (var kv in currentWorldPos)
            {
                var p = kv.Value;
                p.y = rayY;
                Gizmos.DrawSphere(p, gizmoAgentRadius);
            }
        }

        if (anchorGO != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(goalWorld, 0.5f);
        }
    }

    // =========================
    // Loading
    // =========================
    void LoadAll()
    {
        trajById.Clear();
        groups.Clear();
        groupEdges.Clear();
        globalMinFrame = int.MaxValue;
        globalMaxFrame = int.MinValue;

        if (useHomography && homographyTxt != null)
            H = ParseHomography3x3(homographyTxt.text);
        else
            H = Matrix3x3.Identity;

        if (trajectoriesTxt == null)
        {
            Debug.LogError("[ZaraPlaybackWithAnchor0] trajectoriesTxt is null.");
            return;
        }

        ParseTrajectories(trajectoriesTxt.text);
        if (groupsTxt != null) ParseGroups(groupsTxt.text);
        BuildGroupEdges();

        if (globalMinFrame == int.MaxValue) globalMinFrame = 0;
        if (globalMaxFrame == int.MinValue) globalMaxFrame = 0;
    }

    void ParseTrajectories(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        var tmp = new Dictionary<int, List<Sample>>(256);

        using (var sr = new StringReader(text))
        {
            string line;
            int lineNo = 0;
            while ((line = sr.ReadLine()) != null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                try
                {
                    int id = int.Parse(parts[0].Trim(), inv);
                    float x = float.Parse(parts[1].Trim(), inv);
                    float y = float.Parse(parts[2].Trim(), inv);
                    int frame = int.Parse(parts[3].Trim(), inv);
                    float ang = float.Parse(parts[4].Trim(), inv);

                    if (!tmp.TryGetValue(id, out var list))
                    {
                        list = new List<Sample>(256);
                        tmp[id] = list;
                    }
                    list.Add(new Sample { frame = frame, xy = new Vector2(x, y), angleDeg = ang });

                    globalMinFrame = Mathf.Min(globalMinFrame, frame);
                    globalMaxFrame = Mathf.Max(globalMaxFrame, frame);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ZaraPlaybackWithAnchor0] Parse error line {lineNo}: {line}\n{ex.Message}");
                }
            }
        }

        foreach (var kv in tmp)
        {
            var list = kv.Value;
            list.Sort((a, b) => a.frame.CompareTo(b.frame));

            int n = list.Count;
            if (n <= 0) continue;

            var frames = new int[n];
            var xy = new Vector2[n];
            var ang = new float[n];

            for (int i = 0; i < n; i++)
            {
                frames[i] = list[i].frame;
                xy[i] = list[i].xy;
                ang[i] = list[i].angleDeg;
            }

            trajById[kv.Key] = new Trajectory
            {
                id = kv.Key,
                frames = frames,
                xy = xy,
                angles = ang
            };
        }
    }

    void ParseGroups(string text)
    {
        using (var sr = new StringReader(text))
        {
            string line;
            int lineNo = 0;
            while ((line = sr.ReadLine()) != null)
            {
                lineNo++;
                line = line.Trim();
                if (line.Length == 0) continue;

                var toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length < 2) continue;

                var ids = new int[toks.Length];
                bool ok = true;
                for (int i = 0; i < toks.Length; i++)
                {
                    if (!int.TryParse(toks[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ids[i]))
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok)
                {
                    Debug.LogWarning($"[ZaraPlaybackWithAnchor0] Bad group line {lineNo}: {line}");
                    continue;
                }
                groups.Add(ids);
            }
        }
    }

    void BuildGroupEdges()
    {
        groupEdges.Clear();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            if (g.Length < 2) continue;

            if (linkMode == GroupLinkMode.Chain)
            {
                for (int i = 0; i < g.Length - 1; i++)
                    groupEdges.Add((g[i], g[i + 1], gi));
            }
            else
            {
                for (int i = 0; i < g.Length; i++)
                    for (int j = i + 1; j < g.Length; j++)
                        groupEdges.Add((g[i], g[j], gi));
            }
        }
    }

    // =========================
    // Spawn NPCs (excluding anchorId)
    // =========================
    void SpawnNPCs()
    {
        foreach (var kv in npcGoById)
        {
            if (kv.Value != null) Destroy(kv.Value);
        }
        npcGoById.Clear();
        npcAnimById.Clear();
        npcPrevPosById.Clear();

        foreach (var kv in trajById)
        {
            int id = kv.Key;
            if (id == anchorId) continue;

            GameObject go;
            if (npcPrefab != null)
            {
                go = Instantiate(npcPrefab, transform);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(transform, worldPositionStays: true);
            }

            go.name = $"ped_{id}";
            go.transform.localScale = Vector3.one * npcSize;

            npcGoById[id] = go;

            var anim = go.GetComponentInChildren<Animator>();
            if (anim != null) npcAnimById[id] = anim;
        }
    }

    // =========================
    // Anchor Setup (id == anchorId)
    // =========================
    void SetupAnchor()
    {
        if (!trajById.TryGetValue(anchorId, out var traj) || traj.frames == null || traj.frames.Length == 0)
        {
            Debug.LogError($"[ZaraPlaybackWithAnchor0] anchorId={anchorId} trajectory not found.");
            return;
        }

        anchorGO = GameObject.Find(anchorNameIfExisting);

        if (anchorGO == null && anchorPrefab != null)
        {
            anchorGO = Instantiate(anchorPrefab, transform);
            anchorGO.name = $"ped_{anchorId}";
        }
        else if (anchorGO == null && spawnAnchorIfMissing)
        {
            anchorGO = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            anchorGO.transform.SetParent(transform, worldPositionStays: true);
            anchorGO.name = $"ped_{anchorId}";
        }

        if (anchorGO == null)
        {
            Debug.LogError("[ZaraPlaybackWithAnchor0] anchorGO is null (no prefab, no existing).");
            return;
        }

        // start position = first frame
        Vector2 startXY = traj.xy[0];
        Vector3 startWorld = MapToWorld(startXY);
        startWorld.y = anchorGO.transform.position.y;
        anchorGO.transform.position = startWorld;

        // goal position
        if (goalOverride != null)
        {
            goalT = goalOverride;
            goalWorld = goalT.position;
        }
        else
        {
            Vector2 endXY = traj.xy[traj.xy.Length - 1];
            goalWorld = MapToWorld(endXY);

            if (createGoalObjectIfMissing)
            {
                var g = GameObject.Find(goalObjectName);
                if (g == null) g = new GameObject(goalObjectName);
                goalT = g.transform;
                goalT.position = goalWorld;
            }
        }

        if (goalT != null) goalWorld = goalT.position;

        // remaining time
        if (remainingTimeOverride > 0f)
        {
            _anchorRemainingTime = remainingTimeOverride;
        }
        else
        {
            int startF = traj.frames[0];
            int endF = traj.frames[traj.frames.Length - 1];
            _anchorRemainingTime = Mathf.Max(0f, (endF - startF) / Mathf.Max(1f, fps));
        }

        // anchor camera
        if (anchorCamera == null)
            anchorCamera = anchorGO.GetComponentInChildren<Camera>();

        if (anchorCamera == null)
        {
            Debug.LogError("[ZaraPlaybackWithAnchor0] Anchor camera not found. Assign anchorCamera or add Camera under anchor.");
            return;
        }

        anchorRT = new RenderTexture(imageWidth, imageHeight, 24);
        anchorTex = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
    }

    // =========================
    // NPC Evaluate + Apply
    // =========================
    void EvaluateAndApplyNPCTransforms()
    {
        currentWorldPos.Clear();

        foreach (var kv in trajById)
        {
            int id = kv.Key;
            if (id == anchorId) continue;

            if (!npcGoById.TryGetValue(id, out var go) || go == null) continue;

            var traj = kv.Value;
            if (!traj.TryEvaluate(currentFrameF, out var rawXY, out var angDeg))
            {
                go.SetActive(false);
                npcPrevPosById.Remove(id);
                continue;
            }

            go.SetActive(true);

            Vector2 world2 = useHomography ? ApplyHomography(H, rawXY) : rawXY;
            Vector3 pos = ToUnityXZ(world2) * worldScale + worldOffset;
            pos.y = go.transform.position.y;

            if (driveAnimatorBlend && npcAnimById.TryGetValue(id, out var anim) && anim != null)
            {
                float speed = 0f;
                if (npcPrevPosById.TryGetValue(id, out var prevPos))
                {
                    float dt = Mathf.Max(1e-6f, Time.deltaTime);
                    speed = Vector3.Distance(prevPos, pos) / dt;
                }
                npcPrevPosById[id] = pos;

                float target = (speedForFullWalk <= 1e-6f) ? 0f : Mathf.Clamp01(speed / speedForFullWalk);
                if (speed < speedEpsilon) target = 0f;

                if (blendDampTime > 0f) anim.SetFloat(blendParamName, target, blendDampTime, Time.deltaTime);
                else anim.SetFloat(blendParamName, target);
            }

            go.transform.position = pos;

            if (applyYawFromAngle)
            {
                float yaw = angDeg;
                if (flipX) yaw = 180f - yaw;
                if (flipZ) yaw = -yaw;

                yaw += yawOffsetDeg;
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            currentWorldPos[id] = pos;
        }

        if (anchorGO != null)
            currentWorldPos[anchorId] = anchorGO.transform.position;
    }

    // =========================
    // Anchor Loop (Server)
    // =========================
    IEnumerator AnchorLoop()
    {
        if (anchorGO == null || anchorCamera == null) yield break;

        while (!anchorGoalReached)
        {
            if (stopOnGoal)
            {
                float d = Vector3.Distance(anchorGO.transform.position, goalWorld);
                if (d < goalReachThreshold)
                {
                    anchorGoalReached = true;
                    if (deactivateAnchorOnGoal) anchorGO.SetActive(false);
                    yield break;
                }
            }

            // capture
            byte[] img = CaptureAnchorView();
            if (img == null || img.Length == 0)
            {
                yield return new WaitForSeconds(0.2f);
                continue;
            }

            // goal_position: 현재 위치 기준 상대좌표(회전 무시)
            string goalLocalJson = GetGoalLocalPositionJson(goalWorld, anchorGO.transform.position);

            // remaining_time: "움직일 때만" 깎이도록 CoMoveAnchorByPrediction에서 관리됨
            string timeStr = _anchorRemainingTime.ToString("F2", CultureInfo.InvariantCulture);

            // request
            anchorRequestInFlight = true;
            string jsonResponse = null;
            yield return StartCoroutine(CoSendRequest(img, goalLocalJson, timeStr, (res) => jsonResponse = res));
            anchorRequestInFlight = false;

            if (string.IsNullOrEmpty(jsonResponse))
            {
                yield return new WaitForSeconds(0.2f);
                continue;
            }

            // move: 서버 20-step 전부 재생하고 나서 돌아옴
            yield return StartCoroutine(CoMoveAnchorByPrediction(jsonResponse));
        }
    }

    // =========================
    // Anchor: apply 20-step trajectory
    // =========================
    IEnumerator CoMoveAnchorByPrediction(string json)
    {
        ServerResponseData data = null;
        try { data = JsonConvert.DeserializeObject<ServerResponseData>(json); }
        catch (Exception e)
        {
            Debug.LogError($"[ZaraPlaybackWithAnchor0] Anchor JSON parse error: {e.Message}\n{json}");
            yield break;
        }

        if (data?.output == null || data.output.Length == 0) yield break;

        float[][] traj = data.output[0];
        if (traj == null || traj.Length == 0) yield break;

        // ✅ 이 배치 시작 시점의 월드 위치를 (0,0) 기준으로 둔다
        Vector3 basePos = anchorGO.transform.position;
        basePos.y = anchorGO.transform.position.y;

        // 로컬 프레임이면 "요청 시점 회전" 기준으로 변환(일관성)
        Quaternion baseRot = anchorGO.transform.rotation;

        // (0,0) 포함한다고 했으니, 첫 점이 (0,0)이면 스킵
        int startIndex = 0;
        if (traj[0] != null && traj[0].Length >= 2)
        {
            float x0 = flipPredX ? -traj[0][0] : traj[0][0];
            float z0 = flipPredZ ? -traj[0][1] : traj[0][1];
            if (x0 * x0 + z0 * z0 <= predEps) startIndex = 1;
        }

        int stepsToPlay = Mathf.Max(0, traj.Length - startIndex);
        if (stepsToPlay == 0) yield break;

        float stepDt;
        if (useFpsAsServerStepTime)
        {
            stepDt = 1f / Mathf.Max(1f, fps);
        }
        else
        {
            // moveDurationPerStep = "서버 응답 1회(20step) 재생 총 시간"
            stepDt = moveDurationPerStep / Mathf.Max(1, stepsToPlay);
        }
        stepDt = Mathf.Max(minStepDt, stepDt);

        // 디버그: 중간 멈춤 원인 확인용(원하면 꺼도 됨)
        // Debug.Log($"[Anchor] play steps={stepsToPlay}, stepDt={stepDt:F4}, remaining={_anchorRemainingTime:F2}");

        for (int i = startIndex; i < traj.Length; i++)
        {
            if (anchorGO == null) yield break;
            if (stopOnGoal && Vector3.Distance(anchorGO.transform.position, goalWorld) < goalReachThreshold)
            {
                anchorGoalReached = true;
                if (deactivateAnchorOnGoal) anchorGO.SetActive(false);
                yield break;
            }

            var p = traj[i];
            if (p == null || p.Length < 2)
            {
                // 비정상 step은 그냥 시간만 소비하지 말고 skip
                continue;
            }

            float dx = flipPredX ? -p[0] : p[0];
            float dz = flipPredZ ? -p[1] : p[1];

            // 서버 상대좌표(요청 시점 기준) -> 월드 오프셋
            Vector3 offset = new Vector3(dx, 0f, dz) * predScale;

            if (predIsInAnchorLocalFrame)
                offset = baseRot * offset; // rotation only (요청 시점 기준)

            // ✅ 핵심: "이번 배치의 basePos" + "서버가 준 상대 오프셋(누적)" = 목표 월드 위치
            Vector3 target = basePos + offset;
            target.y = anchorGO.transform.position.y;

            // ✅ 바라보기: 다음 타겟 방향
            Vector3 dir = target - anchorGO.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-10f)
            {
                Quaternion look = Quaternion.LookRotation(dir.normalized, Vector3.up);
                look = look * Quaternion.Euler(0f, anchorYawOffsetDeg, 0f);
                anchorGO.transform.rotation = look;
            }

            // ✅ 서버 값 그대로 위치 적용 (가속/속도 제한/보간 없음)
            anchorGO.transform.position = target;

            // ✅ remaining time은 "움직이는 동안"에만 감소
            if (_anchorRemainingTime > 0f)
                _anchorRemainingTime = Mathf.Max(0f, _anchorRemainingTime - stepDt);

            yield return new WaitForSeconds(stepDt);
        }
    }

    IEnumerator CoSendRequest(byte[] image, string goal, string time, Action<string> onComplete)
    {
        WWWForm form = new WWWForm();
        form.AddField("goal_position", goal);
        form.AddField("remaining_time", time);
        form.AddBinaryData("image", image, "capture.png", "image/png");

        using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
        {
            www.timeout = Mathf.Max(1, requestTimeoutSec);
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
                onComplete?.Invoke(www.downloadHandler.text);
            else
                onComplete?.Invoke(null);
        }
    }

    // =========================
    // Helpers: Goal local json
    // =========================
    string GetGoalLocalPositionJson(Vector3 goalWorldPos, Vector3 anchorWorldPos)
    {
        Vector3 offset = goalWorldPos - anchorWorldPos; // 회전 무시

        float gx = flipGoalX ? -offset.x : offset.x;
        float gz = flipGoalZ ? -offset.z : offset.z;

        return $"[{gx.ToString("F4", CultureInfo.InvariantCulture)},{gz.ToString("F4", CultureInfo.InvariantCulture)}]";
    }

    // =========================
    // Helpers: Capture
    // =========================
    byte[] CaptureAnchorView()
    {
        if (anchorCamera == null || anchorRT == null || anchorTex == null) return null;

        RenderTexture prev = RenderTexture.active;

        anchorCamera.targetTexture = anchorRT;
        anchorCamera.Render();

        RenderTexture.active = anchorRT;
        anchorTex.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
        anchorTex.Apply();

        anchorCamera.targetTexture = null;
        RenderTexture.active = prev;

        return anchorTex.EncodeToPNG();
    }

    // =========================
    // Public controls
    // =========================
    public void Play() => isPlaying = true;
    public void Pause() => isPlaying = false;

    public void ResetPlayback()
    {
        currentFrameF = globalMinFrame;
        EvaluateAndApplyNPCTransforms();
    }

    // =========================
    // Mapping / Homography
    // =========================
    Vector3 ToUnityXZ(Vector2 xy)
    {
        float x = flipX ? -xy.x : xy.x;
        float z = flipZ ? -xy.y : xy.y;
        return new Vector3(x, 0f, z);
    }

    Vector3 MapToWorld(Vector2 rawXY)
    {
        Vector2 world2 = useHomography ? ApplyHomography(H, rawXY) : rawXY;
        return ToUnityXZ(world2) * worldScale + worldOffset;
    }

    static Vector2 ApplyHomography(Matrix3x3 h, Vector2 uv)
    {
        float u = uv.x, v = uv.y;

        float x = h.m00 * u + h.m01 * v + h.m02;
        float y = h.m10 * u + h.m11 * v + h.m12;
        float w = h.m20 * u + h.m21 * v + h.m22;

        if (!Mathf.Approximately(w, 0f))
        {
            x /= w;
            y /= w;
        }
        return new Vector2(x, y);
    }

    static Matrix3x3 ParseHomography3x3(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<float[]>(3);

        using (var sr = new StringReader(text))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                var toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length < 3) continue;

                rows.Add(new[]
                {
                    float.Parse(toks[0], inv),
                    float.Parse(toks[1], inv),
                    float.Parse(toks[2], inv),
                });

                if (rows.Count == 3) break;
            }
        }

        if (rows.Count < 3) return Matrix3x3.Identity;

        return new Matrix3x3(
            rows[0][0], rows[0][1], rows[0][2],
            rows[1][0], rows[1][1], rows[1][2],
            rows[2][0], rows[2][1], rows[2][2]
        );
    }

    static Color GroupColor(int groupIndex)
    {
        float h = Mathf.Repeat(groupIndex * 0.137f, 1f);
        return Color.HSVToRGB(h, 0.85f, 1f);
    }
}
