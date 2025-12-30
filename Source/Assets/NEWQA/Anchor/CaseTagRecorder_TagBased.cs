using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class CaseTagRecorder_NoAnchors : MonoBehaviour
{
    [Header("Input Folder (jsonl)")]
    public string jsonlFolderPath =
        @"C:\Users\juyeong\Desktop\LAB_JY\01_WORK\01_PaperWork\2026_SIGGRAPH\01_Codes\DataStep3\Case2\case_continuous";

    [Header("Replay Manager (must expose int currentGlobalFrame)")]
    public MonoBehaviour replayManager;

    [Header("FPS (for speed/accel only)")]
    public float timePerFrame = 0.05f;

    [Header("Tag Names - Collision/Proximity")]
    public string TAG_AGENT = "Agent";
    public string TAG_OBSTACLE = "Obstacle";
    public string TAG_BUILDING = "Building";
    public string TAG_ENTARANCE = "Entarance";
    public string TAG_VEHICLE = "Vehicle";

    [Header("Tag Names - Ground")]
    public string GROUND_SIDEWALK = "sidewalk";
    public string GROUND_CROSSWALK = "crosswalk";
    public string GROUND_ROAD = "road";
    public string GROUND_GRASS = "grass";
    public string GROUND_INSIDE = "inside";

    [Header("Ground Raycast")]
    public float rayStartHeight = 2.0f;
    public float rayLength = 10.0f;

    // ===================== ✅ Per-type radii =====================
    [Header("Proximity/Collision radii (per-type)")]
    public float nearRadiusAgent = 2.0f;
    public float hitRadiusAgent = 0.7f;

    public float nearRadiusObstacle = 2.0f;
    public float hitRadiusObstacle = 0.7f;

    public float nearRadiusBuilding = 2.0f;
    public float hitRadiusBuilding = 0.7f;

    public float nearRadiusEntarance = 2.0f;
    public float hitRadiusEntarance = 0.7f;

    public float nearRadiusVehicle = 2.0f;
    public float hitRadiusVehicle = 0.7f;

    // env 쿼리에 쓸 최대 반경(성능용 캐시)
    private float _envMaxNearRadius = 2.0f;
    private float _envMaxHitRadius = 0.7f;
    // ============================================================

    [Header("Trigger-only for Obstacle/Building/Entrance/Vehicle")]
    public bool onlyUseTriggerColliders = true;

    [Header("Group/Goal thresholds")]
    public float groupCloseDist = 2.0f;
    public float groupFarDist = 6.0f;
    public float goalCloseDist = 2.0f;
    public float goalFarDist = 10.0f;

    [Header("Output")]
    public string outputSubFolderName = "_TAGGED_OUT";
    public string outputSuffix = "_tagged";
    public bool overwriteOutputsOnStart = true;
    public bool recordOnlyFramesInJsonl = true;

    [Header("Agent name parsing")]
    public string agentNamePrefix = "agent_";

    // ===================== Camera Capture =====================
    [Header("Camera Capture (per-anchor start/end)")]
    public bool captureCameraImages = true;

    [Tooltip("Agent root 기준 상대 경로 (예: \"Rig 1/HeadAim/Camera\")")]
    public string agentCameraRelativePath = "Rig 1/HeadAim/Camera";

    [Tooltip("이미지 저장 폴더명 (output 폴더 아래)")]
    public string imagesFolderName = "images";

    public int captureWidth = 640;
    public int captureHeight = 360;

    [Tooltip("true면 JPG, false면 PNG")]
    public bool saveAsJPG = true;

    [Range(1, 100)]
    public int jpgQuality = 90;

    [Tooltip("이미지가 이미 있으면 다시 저장할지 여부")]
    public bool overwriteExistingImages = false;

    [Tooltip("Start 시 기존 images 폴더도 같이 지울지")]
    public bool clearImagesOnStart = false;
    // ==========================================================

    // ------------ JSON meta ----------
    [Serializable]
    class FrameMeta
    {
        public int[] groupIds;
        public Vector3 goalWorld;
        public bool hasGoal;
    }

    class AnchorInfo
    {
        public int anchorIndex;

        public string originalLine;
        public int[] frames;
        public int startGf;
        public int endGf;

        public bool startCaptured;
        public bool endCaptured;
        public string startImageRel;
        public string endImageRel;
    }

    class AgentMeta
    {
        public string agentFileBaseName;
        public int agentId;

        public Dictionary<int, FrameMeta> metaByFrame = new Dictionary<int, FrameMeta>();
        public List<AnchorInfo> anchors = new List<AnchorInfo>();

        public Dictionary<int, List<int>> startAnchorsByGf = new Dictionary<int, List<int>>();
        public Dictionary<int, List<int>> endAnchorsByGf = new Dictionary<int, List<int>>();

        public int nextAnchorToWrite = 0;
        public string outPath;

        // ✅ jsonl 기준 "이 에이전트 trajectory의 마지막 global frame"
        public int trajEndGf = int.MinValue;
    }

    // ------------ Recorded tag per frame ----------
    [Serializable]
    public class GroupMemberRel
    {
        public int id;
        public Vector3 relWorld;   // other - self (world). ✅ 회전 무관, "상대 위치"
        public float dist;
    }

    [Serializable]
    class FrameTag
    {
        public bool active;

        // ground
        public string ground;

        // nearest agent
        public int nearestAgentId;
        public float nearestAgentDist;
        public bool near_agent;
        public bool hit_agent;

        // others
        public float nearestObstacleDist; public bool near_obstacle; public bool hit_obstacle;
        public float nearestBuildingDist; public bool near_building; public bool hit_building;
        public float nearestEntaranceDist; public bool near_entarance; public bool hit_entarance;
        public float nearestVehicleDist; public bool near_vehicle; public bool hit_vehicle;

        // group/goal
        public float groupDist;
        public string groupState;

        // ✅ group: world center + (self-origin) relative
        public int groupActiveCount;
        public Vector3 groupCenterWorld;     // center world position (유지)
        public Vector3 groupCenterRelWorld;  // center - self (상대)

        public List<GroupMemberRel> groupMemberRels; // member - self (상대)

        public float goalDist;
        public string goalState;

        // kinematics
        public float speed;
        public float accel;
        public float turnDeg;
    }

    class AgentRuntime
    {
        public int id;
        public Transform tr;

        // camera
        public Camera cam;

        public bool hasPrev;
        public int prevGf;
        public Vector3 prevPos;
        public Vector3 prevVel;
        public Vector3 prevForward;

        public Dictionary<int, FrameTag> tagByFrame = new Dictionary<int, FrameTag>();
    }

    private readonly Dictionary<int, AgentMeta> _metaById = new Dictionary<int, AgentMeta>();
    private readonly Dictionary<int, AgentRuntime> _runtimeById = new Dictionary<int, AgentRuntime>();

    private int _globalMin = int.MaxValue;
    private int _globalMax = int.MinValue;

    private int _lastRecordedFrame = int.MinValue;

    private readonly Collider[] _nearBuf = new Collider[512];
    private readonly Collider[] _hitBuf = new Collider[512];

    private System.Reflection.FieldInfo _rmField;
    private System.Reflection.PropertyInfo _rmProp;

    private string _inputFolderAbs;
    private string _outputFolderAbs;
    private string _imagesFolderAbs;

    enum EnvKind { None, Obstacle, Building, Entarance, Vehicle }

    void OnValidate()
    {
        // clamp
        nearRadiusAgent = Mathf.Max(0f, nearRadiusAgent);
        hitRadiusAgent = Mathf.Max(0f, hitRadiusAgent);

        nearRadiusObstacle = Mathf.Max(0f, nearRadiusObstacle);
        hitRadiusObstacle = Mathf.Max(0f, hitRadiusObstacle);

        nearRadiusBuilding = Mathf.Max(0f, nearRadiusBuilding);
        hitRadiusBuilding = Mathf.Max(0f, hitRadiusBuilding);

        nearRadiusEntarance = Mathf.Max(0f, nearRadiusEntarance);
        hitRadiusEntarance = Mathf.Max(0f, hitRadiusEntarance);

        nearRadiusVehicle = Mathf.Max(0f, nearRadiusVehicle);
        hitRadiusVehicle = Mathf.Max(0f, hitRadiusVehicle);

        RecomputeEnvMaxRadii();
    }

    void RecomputeEnvMaxRadii()
    {
        _envMaxNearRadius = Mathf.Max(
            nearRadiusObstacle,
            Mathf.Max(nearRadiusBuilding, Mathf.Max(nearRadiusEntarance, nearRadiusVehicle))
        );

        _envMaxHitRadius = Mathf.Max(
            hitRadiusObstacle,
            Mathf.Max(hitRadiusBuilding, Mathf.Max(hitRadiusEntarance, hitRadiusVehicle))
        );

        _envMaxNearRadius = Mathf.Max(0.0001f, _envMaxNearRadius);
        _envMaxHitRadius = Mathf.Max(0.0001f, _envMaxHitRadius);
    }

    void Start()
    {
        RecomputeEnvMaxRadii();

        ResolveFolders();
        LoadMetaFromJsonlFolder();
        PrepareOutputPaths();

        if (overwriteOutputsOnStart)
            ClearExistingOutputs();

        StartCoroutine(CoInitRuntimeAgentsNextFrame());
    }

    void OnDisable() => Cleanup();
    void OnDestroy() => Cleanup();
    void OnApplicationQuit() => Cleanup();

    void Cleanup()
    {
        // anchor마다 열고 닫아서 writer를 유지하지 않음.
    }

    void ResolveFolders()
    {
        string folder = jsonlFolderPath;
        if (!Path.IsPathRooted(folder))
            folder = Path.Combine(Application.dataPath, folder);

        _inputFolderAbs = folder;
        _outputFolderAbs = Path.Combine(_inputFolderAbs, outputSubFolderName);
        Directory.CreateDirectory(_outputFolderAbs);

        _imagesFolderAbs = Path.Combine(_outputFolderAbs, imagesFolderName);
        Directory.CreateDirectory(_imagesFolderAbs);
    }

    IEnumerator CoInitRuntimeAgentsNextFrame()
    {
        // ✅ replay manager가 Start에서 에이전트 스폰할 수 있으니 한 프레임 대기
        yield return null;

        var all = FindObjectsOfType<Transform>(true);
        foreach (var t in all)
        {
            if (t == null) continue;
            if (!t.name.StartsWith(agentNamePrefix)) continue;

            int id = ParseAgentId(t.name);
            if (id < 0) continue;
            if (_runtimeById.ContainsKey(id)) continue;

            Camera cam = null;
            if (captureCameraImages)
            {
                var camTr = t.Find(agentCameraRelativePath);
                if (camTr != null)
                    cam = camTr.GetComponent<Camera>();
            }

            _runtimeById[id] = new AgentRuntime
            {
                id = id,
                tr = t,
                cam = cam,
                hasPrev = false,
                prevGf = int.MinValue
            };
        }

        Debug.Log($"[Recorder] runtimeAgents={_runtimeById.Count}");

        // ✅ runtime 준비가 늦어서 첫 gf를 소모해버린 경우 방지
        _lastRecordedFrame = int.MinValue;
    }

    void LateUpdate()
    {
        int gf = GetCurrentGlobalFrame();
        if (gf == int.MinValue) return;

        // ✅ runtime 준비 전에는 gf를 "소모"하지 않음 (첫 앵커 start 누락 방지)
        if (_runtimeById.Count == 0) return;

        if (gf == _lastRecordedFrame) return;
        _lastRecordedFrame = gf;

        RecordFrame(gf);

        if (captureCameraImages)
            CaptureAnchorImagesAtFrame(gf);

        WriteAnchorsUpToFrame(gf);
    }

    // ----------------- Record -----------------
    void RecordFrame(int gf)
    {
        foreach (var kv in _runtimeById)
        {
            var ar = kv.Value;
            if (ar == null || ar.tr == null) continue;

            Vector3 pos = ar.tr.position;
            bool active = ar.tr.gameObject.activeInHierarchy;

            AgentMeta am = null;
            FrameMeta meta = null;
            bool hasMeta = false;

            if (_metaById.TryGetValue(ar.id, out am))
                hasMeta = am.metaByFrame.TryGetValue(gf, out meta);

            bool needStore = !recordOnlyFramesInJsonl || hasMeta;

            // kinematics state는 매 프레임 업데이트
            if (!needStore)
            {
                UpdateKinematicsStateOnly(ar, pos, gf, active);
                continue;
            }

            var tag = new FrameTag
            {
                active = active,
                groupMemberRels = new List<GroupMemberRel>(),
                groupActiveCount = 0,
                groupCenterWorld = Vector3.zero,
                groupCenterRelWorld = Vector3.zero
            };

            if (!active)
            {
                ar.tagByFrame[gf] = tag;

                // inactive면 kinematics 리셋
                ar.hasPrev = false;
                ar.prevGf = gf;
                ar.prevPos = pos;
                ar.prevVel = Vector3.zero;
                ar.prevForward = ar.tr.forward;
                continue;
            }

            tag.ground = DetectGroundType(pos);

            (tag.nearestAgentId, tag.nearestAgentDist) = FindNearestAgent(ar.id, pos);
            tag.near_agent = (tag.nearestAgentId >= 0 && tag.nearestAgentDist >= 0f && tag.nearestAgentDist <= nearRadiusAgent);
            tag.hit_agent = (tag.nearestAgentId >= 0 && tag.nearestAgentDist >= 0f && tag.nearestAgentDist <= hitRadiusAgent);

            ComputeTaggedDistances(pos,
                out tag.nearestObstacleDist, out tag.near_obstacle, out tag.hit_obstacle,
                out tag.nearestBuildingDist, out tag.near_building, out tag.hit_building,
                out tag.nearestEntaranceDist, out tag.near_entarance, out tag.hit_entarance,
                out tag.nearestVehicleDist, out tag.near_vehicle, out tag.hit_vehicle
            );

            if (hasMeta)
            {
                // ✅ group: center(world) + rel(self-origin) + each member rel(self-origin)
                if (meta.groupIds != null && meta.groupIds.Length > 0 &&
                    TryComputeGroupInfo(meta.groupIds, selfId: ar.id, selfPos: pos,
                        out Vector3 centerWorld, out List<GroupMemberRel> rels, out int activeCount))
                {
                    tag.groupCenterWorld = centerWorld;          // 유지
                    tag.groupCenterRelWorld = centerWorld - pos; // ✅ self-origin relative

                    tag.groupDist = Vector3.Distance(pos, centerWorld);
                    tag.groupState = DistToState(tag.groupDist, groupCloseDist, groupFarDist);

                    tag.groupActiveCount = activeCount;
                    tag.groupMemberRels = rels ?? new List<GroupMemberRel>();
                }
                else
                {
                    tag.groupDist = -1f;
                    tag.groupState = "none";
                    tag.groupActiveCount = 0;
                    tag.groupMemberRels = new List<GroupMemberRel>();
                    tag.groupCenterWorld = Vector3.zero;
                    tag.groupCenterRelWorld = Vector3.zero;
                }

                if (meta.hasGoal)
                {
                    tag.goalDist = Vector3.Distance(pos, meta.goalWorld);
                    tag.goalState = DistToState(tag.goalDist, goalCloseDist, goalFarDist);
                }
                else
                {
                    tag.goalDist = -1f;
                    tag.goalState = "none";
                }
            }
            else
            {
                tag.groupDist = -1f; tag.groupState = "none";
                tag.groupActiveCount = 0;
                tag.groupMemberRels = new List<GroupMemberRel>();
                tag.groupCenterWorld = Vector3.zero;
                tag.groupCenterRelWorld = Vector3.zero;

                tag.goalDist = -1f; tag.goalState = "none";
            }

            ComputeKinematics(ar, pos, gf, tag);
            ar.tagByFrame[gf] = tag;
        }
    }

    // ✅ group center(world) + member rel(world-self) (rotation independent)
    bool TryComputeGroupInfo(int[] groupIds, int selfId, Vector3 selfPos,
        out Vector3 centerWorld, out List<GroupMemberRel> rels, out int activeCount)
    {
        centerWorld = Vector3.zero;
        rels = new List<GroupMemberRel>();
        activeCount = 0;

        if (groupIds == null || groupIds.Length == 0)
            return false;

        for (int i = 0; i < groupIds.Length; i++)
        {
            int gid = groupIds[i];
            if (gid == selfId) continue; // 혹시 self가 들어있으면 제외

            if (_runtimeById.TryGetValue(gid, out var other) &&
                other != null && other.tr != null && other.tr.gameObject.activeInHierarchy)
            {
                Vector3 d = other.tr.position - selfPos; // ✅ self-origin relative (월드 diff)
                rels.Add(new GroupMemberRel
                {
                    id = gid,
                    relWorld = d,
                    dist = d.magnitude
                });

                centerWorld += other.tr.position;
                activeCount++;
            }
        }

        if (activeCount == 0) return false;
        centerWorld /= activeCount;
        return true;
    }

    void UpdateKinematicsStateOnly(AgentRuntime ar, Vector3 pos, int gf, bool active)
    {
        if (!active)
        {
            ar.hasPrev = false;
            ar.prevGf = gf;
            ar.prevPos = pos;
            ar.prevVel = Vector3.zero;
            ar.prevForward = ar.tr.forward;
            return;
        }

        if (!ar.hasPrev)
        {
            ar.hasPrev = true;
            ar.prevGf = gf;
            ar.prevPos = pos;
            ar.prevVel = Vector3.zero;
            ar.prevForward = ar.tr.forward;
            return;
        }

        float dt = Mathf.Max(1e-6f, (gf - ar.prevGf) * timePerFrame);
        Vector3 vel = (pos - ar.prevPos) / dt;

        ar.prevGf = gf;
        ar.prevPos = pos;
        ar.prevVel = vel;
        ar.prevForward = ar.tr.forward;
    }

    void ComputeKinematics(AgentRuntime ar, Vector3 pos, int gf, FrameTag tag)
    {
        if (!ar.hasPrev)
        {
            ar.hasPrev = true;
            ar.prevGf = gf;
            ar.prevPos = pos;
            ar.prevVel = Vector3.zero;
            ar.prevForward = ar.tr.forward;

            tag.speed = 0f;
            tag.accel = 0f;
            tag.turnDeg = 0f;
            return;
        }

        float dt = Mathf.Max(1e-6f, (gf - ar.prevGf) * timePerFrame);

        Vector3 vel = (pos - ar.prevPos) / dt;
        tag.speed = vel.magnitude;

        Vector3 acc = (vel - ar.prevVel) / dt;
        tag.accel = acc.magnitude;

        Vector3 fwd = ar.tr.forward;
        tag.turnDeg = Vector3.Angle(ar.prevForward, fwd);

        ar.prevGf = gf;
        ar.prevPos = pos;
        ar.prevVel = vel;
        ar.prevForward = fwd;
    }

    // ----------------- Capture start/end images at exact frames -----------------
    void CaptureAnchorImagesAtFrame(int gf)
    {
        foreach (var kv in _metaById)
        {
            var am = kv.Value;
            if (am == null) continue;

            if (!_runtimeById.TryGetValue(am.agentId, out var runtime) || runtime == null)
                continue;

            if (am.startAnchorsByGf.TryGetValue(gf, out var startList))
            {
                for (int i = 0; i < startList.Count; i++)
                {
                    int aidx = startList[i];
                    if (aidx < 0 || aidx >= am.anchors.Count) continue;
                    var anchor = am.anchors[aidx];
                    if (anchor == null || anchor.startCaptured) continue;

                    CaptureOneAnchorImage(am, runtime, anchor, isStart: true, gf: gf);
                }
            }

            if (am.endAnchorsByGf.TryGetValue(gf, out var endList))
            {
                for (int i = 0; i < endList.Count; i++)
                {
                    int aidx = endList[i];
                    if (aidx < 0 || aidx >= am.anchors.Count) continue;
                    var anchor = am.anchors[aidx];
                    if (anchor == null || anchor.endCaptured) continue;

                    CaptureOneAnchorImage(am, runtime, anchor, isStart: false, gf: gf);
                }
            }
        }
    }

    void CaptureOneAnchorImage(AgentMeta am, AgentRuntime runtime, AnchorInfo anchor, bool isStart, int gf)
    {
        // ✅ tagByFrame 존재 유무에 의존하지 않고 "현재 활성 + 카메라 존재"로 판단
        bool isActiveNow = runtime != null && runtime.tr != null && runtime.tr.gameObject.activeInHierarchy;

        if (!isActiveNow || runtime == null || runtime.cam == null)
        {
            if (isStart)
            {
                anchor.startCaptured = true;
                anchor.startImageRel = "";
            }
            else
            {
                anchor.endCaptured = true;
                anchor.endImageRel = "";
            }
            return;
        }

        string ext = saveAsJPG ? "jpg" : "png";
        string agentFolder = Path.Combine(_imagesFolderAbs, $"{agentNamePrefix}{am.agentId}");
        Directory.CreateDirectory(agentFolder);

        string which = isStart ? "start" : "end";
        string fileName = $"anchor_{anchor.anchorIndex:D06}_{which}_gf_{gf:D06}.{ext}";
        string imgFullPath = Path.Combine(agentFolder, fileName);

        bool needWrite = overwriteExistingImages || !File.Exists(imgFullPath);
        if (needWrite)
        {
            bool ok = CaptureCameraToFile(runtime.cam, imgFullPath);
            if (!ok)
            {
                if (isStart)
                {
                    anchor.startCaptured = true;
                    anchor.startImageRel = "";
                }
                else
                {
                    anchor.endCaptured = true;
                    anchor.endImageRel = "";
                }
                return;
            }
        }

        string rel = MakeRelativePath(_outputFolderAbs, imgFullPath).Replace('\\', '/');

        if (isStart)
        {
            anchor.startCaptured = true;
            anchor.startImageRel = rel;
        }
        else
        {
            anchor.endCaptured = true;
            anchor.endImageRel = rel;
        }
    }

    bool CaptureCameraToFile(Camera cam, string fileFullPath)
    {
        if (cam == null) return false;

        int w = Mathf.Max(8, captureWidth);
        int h = Mathf.Max(8, captureHeight);

        RenderTexture rt = null;
        Texture2D tex = null;

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture prevTarget = cam.targetTexture;

        try
        {
            rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            cam.Render();

            RenderTexture.active = rt;

            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);

            byte[] bytes = saveAsJPG ? tex.EncodeToJPG(jpgQuality) : tex.EncodeToPNG();

            Directory.CreateDirectory(Path.GetDirectoryName(fileFullPath) ?? _imagesFolderAbs);
            File.WriteAllBytes(fileFullPath, bytes);

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recorder] CaptureCameraToFile failed: {fileFullPath}\n{e}");
            return false;
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            if (tex != null) Destroy(tex);
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    static string MakeRelativePath(string fromDir, string toPath)
    {
        try
        {
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toPath))
                return toPath;

            if (!fromDir.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
                !fromDir.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                fromDir += Path.DirectorySeparatorChar;
            }

            var fromUri = new Uri(fromDir);
            var toUri = new Uri(toPath);

            var relUri = fromUri.MakeRelativeUri(toUri);
            string rel = Uri.UnescapeDataString(relUri.ToString());
            return rel.Replace('/', Path.DirectorySeparatorChar);
        }
        catch
        {
            return toPath;
        }
    }

    // ----------------- Write per anchor end -----------------
    void WriteAnchorsUpToFrame(int gf)
    {
        foreach (var kv in _metaById)
        {
            var am = kv.Value;
            if (am == null) continue;

            while (am.nextAnchorToWrite < am.anchors.Count)
            {
                var anchor = am.anchors[am.nextAnchorToWrite];
                if (anchor == null)
                {
                    am.nextAnchorToWrite++;
                    continue;
                }

                if (gf < anchor.endGf) break;

                AppendOneAnchorLine(am, anchor);
                am.nextAnchorToWrite++;
            }
        }
    }

    // --------- JSON helpers (Vector3 self-reference loop 방지) ---------
    static JArray V3(Vector3 v) => new JArray(v.x, v.y, v.z);

    static JObject GroupMemberRelToJObject(GroupMemberRel r)
    {
        return new JObject
        {
            ["id"] = r.id,
            ["relWorld"] = V3(r.relWorld),
            ["dist"] = r.dist
        };
    }

    static JObject FrameTagToJObject(FrameTag t)
    {
        if (t == null) return new JObject { ["active"] = false };

        var o = new JObject
        {
            ["active"] = t.active,

            ["ground"] = t.ground ?? "unknown",

            ["nearestAgentId"] = t.nearestAgentId,
            ["nearestAgentDist"] = t.nearestAgentDist,
            ["near_agent"] = t.near_agent,
            ["hit_agent"] = t.hit_agent,

            ["nearestObstacleDist"] = t.nearestObstacleDist,
            ["near_obstacle"] = t.near_obstacle,
            ["hit_obstacle"] = t.hit_obstacle,

            ["nearestBuildingDist"] = t.nearestBuildingDist,
            ["near_building"] = t.near_building,
            ["hit_building"] = t.hit_building,

            ["nearestEntaranceDist"] = t.nearestEntaranceDist,
            ["near_entarance"] = t.near_entarance,
            ["hit_entarance"] = t.hit_entarance,

            ["nearestVehicleDist"] = t.nearestVehicleDist,
            ["near_vehicle"] = t.near_vehicle,
            ["hit_vehicle"] = t.hit_vehicle,

            ["groupDist"] = t.groupDist,
            ["groupState"] = t.groupState ?? "none",

            ["groupActiveCount"] = t.groupActiveCount,
            ["groupCenterWorld"] = V3(t.groupCenterWorld),
            ["groupCenterRelWorld"] = V3(t.groupCenterRelWorld),

            ["goalDist"] = t.goalDist,
            ["goalState"] = t.goalState ?? "none",

            ["speed"] = t.speed,
            ["accel"] = t.accel,
            ["turnDeg"] = t.turnDeg
        };

        var arr = new JArray();
        if (t.groupMemberRels != null)
        {
            for (int i = 0; i < t.groupMemberRels.Count; i++)
                arr.Add(GroupMemberRelToJObject(t.groupMemberRels[i]));
        }
        o["groupMemberRels"] = arr;

        return o;
    }
    // ----------------------------------------------------------------

    void AppendOneAnchorLine(AgentMeta am, AnchorInfo anchor)
    {
        try
        {
            using (var fs = new FileStream(am.outPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs))
            {
                JObject obj;
                try { obj = JObject.Parse(anchor.originalLine); }
                catch
                {
                    sw.WriteLine(anchor.originalLine);
                    return;
                }

                _runtimeById.TryGetValue(am.agentId, out var runtime);

                int start = anchor.startGf;
                int end = anchor.endGf;

                JObject startTagObj = new JObject { ["active"] = false };
                if (runtime != null && start != int.MinValue && runtime.tagByFrame.TryGetValue(start, out var ftStart))
                    startTagObj = FrameTagToJObject(ftStart);

                JObject endTagObj = new JObject { ["active"] = false };
                if (runtime != null && end != int.MinValue && runtime.tagByFrame.TryGetValue(end, out var ftEnd))
                    endTagObj = FrameTagToJObject(ftEnd);

                if (!string.IsNullOrEmpty(anchor.startImageRel))
                {
                    startTagObj["imagePath"] = anchor.startImageRel;
                    startTagObj["imageGf"] = start;
                }

                if (!string.IsNullOrEmpty(anchor.endImageRel))
                {
                    endTagObj["imagePath"] = anchor.endImageRel;
                    endTagObj["imageGf"] = end;
                }

                // ✅ jsonl 기준: "이 에이전트 trajectory 끝(trajEndGf)까지 남은 시간(초)"
                // remainingTrajSec = max(0, (trajEndGf - gf) * timePerFrame)
                if (am.trajEndGf != int.MinValue)
                {
                    startTagObj["trajEndGf"] = am.trajEndGf;
                    endTagObj["trajEndGf"] = am.trajEndGf;

                    if (start != int.MinValue)
                        startTagObj["remainingTrajSec"] = Mathf.Max(0f, (am.trajEndGf - start) * timePerFrame);
                    if (end != int.MinValue)
                        endTagObj["remainingTrajSec"] = Mathf.Max(0f, (am.trajEndGf - end) * timePerFrame);
                }

                obj["startFrameTag"] = startTagObj;
                obj["endFrameTag"] = endTagObj;

                obj.Remove("frameTags");

                sw.WriteLine(obj.ToString(Formatting.None));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recorder] Failed writing anchor line: agentId={am.agentId} out={am.outPath}\n{e}");
        }
    }

    // ----------------- Load meta/anchors -----------------
    void LoadMetaFromJsonlFolder()
    {
        _metaById.Clear();
        _globalMin = int.MaxValue;
        _globalMax = int.MinValue;

        if (!Directory.Exists(_inputFolderAbs))
        {
            Debug.LogError($"[Recorder] Folder not found: {_inputFolderAbs}");
            return;
        }

        var files = Directory.GetFiles(_inputFolderAbs, "*.jsonl")
            .Where(f =>
            {
                string fn = Path.GetFileNameWithoutExtension(f);
                return !fn.EndsWith(outputSuffix, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        if (files.Length == 0)
        {
            Debug.LogError($"[Recorder] No jsonl files in: {_inputFolderAbs}");
            return;
        }

        foreach (var file in files)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            int agentId = ParseAgentId(baseName);
            if (agentId < 0)
            {
                Debug.LogWarning($"[Recorder] Cannot parse agentId from file name: {baseName}");
                continue;
            }

            var am = new AgentMeta
            {
                agentFileBaseName = baseName,
                agentId = agentId,
                trajEndGf = int.MinValue
            };

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject obj = null;
                try { obj = JObject.Parse(line); }
                catch
                {
                    int idxBad = am.anchors.Count;
                    var bad = new AnchorInfo
                    {
                        anchorIndex = idxBad,
                        originalLine = line,
                        frames = Array.Empty<int>(),
                        startGf = int.MinValue,
                        endGf = int.MinValue,
                        startCaptured = true,
                        endCaptured = true,
                        startImageRel = "",
                        endImageRel = ""
                    };
                    am.anchors.Add(bad);
                    continue;
                }

                var gfs = obj["globalFrames"] as JArray;
                if (gfs == null || gfs.Count == 0)
                {
                    int idxNo = am.anchors.Count;
                    var noGf = new AnchorInfo
                    {
                        anchorIndex = idxNo,
                        originalLine = obj.ToString(Formatting.None),
                        frames = Array.Empty<int>(),
                        startGf = int.MinValue,
                        endGf = int.MinValue,
                        startCaptured = true,
                        endCaptured = true,
                        startImageRel = "",
                        endImageRel = ""
                    };
                    am.anchors.Add(noGf);
                    continue;
                }

                int[] frames = gfs.Select(x => (int)x).ToArray();
                int startGf = frames.Min();
                int endGf = frames.Max();

                // ✅ jsonl 기준: 이 에이전트 trajectory의 끝 프레임(최대 endGf)
                am.trajEndGf = (am.trajEndGf == int.MinValue) ? endGf : Mathf.Max(am.trajEndGf, endGf);

                int anchorIdx = am.anchors.Count;
                var anchor = new AnchorInfo
                {
                    anchorIndex = anchorIdx,
                    originalLine = obj.ToString(Formatting.None),
                    frames = frames,
                    startGf = startGf,
                    endGf = endGf,
                    startCaptured = false,
                    endCaptured = false,
                    startImageRel = "",
                    endImageRel = ""
                };
                am.anchors.Add(anchor);

                RegisterAnchorIndex(am.startAnchorsByGf, startGf, anchorIdx);
                RegisterAnchorIndex(am.endAnchorsByGf, endGf, anchorIdx);

                var groups = obj["groups"] as JArray;
                var goal = obj["goalWorldPosition"] as JArray;

                int[] groupIds = groups != null ? groups.Select(x => (int)x).ToArray() : Array.Empty<int>();

                bool hasGoal = goal != null && goal.Count >= 3;
                Vector3 goalPos = hasGoal
                    ? new Vector3((float)goal[0], (float)goal[1], (float)goal[2])
                    : Vector3.zero;

                foreach (var gf in frames)
                {
                    am.metaByFrame[gf] = new FrameMeta
                    {
                        groupIds = groupIds,
                        goalWorld = goalPos,
                        hasGoal = hasGoal
                    };

                    _globalMin = Mathf.Min(_globalMin, gf);
                    _globalMax = Mathf.Max(_globalMax, gf);
                }
            }

            _metaById[agentId] = am;
        }

        if (_globalMin == int.MaxValue) _globalMin = 0;
        if (_globalMax == int.MinValue) _globalMax = -1;

        Debug.Log($"[Recorder] Loaded meta for {_metaById.Count} agents. GlobalFrame range [{_globalMin}..{_globalMax}]");
    }

    void RegisterAnchorIndex(Dictionary<int, List<int>> dict, int gf, int anchorIndex)
    {
        if (gf == int.MinValue) return;
        if (!dict.TryGetValue(gf, out var list))
        {
            list = new List<int>();
            dict[gf] = list;
        }
        list.Add(anchorIndex);
    }

    void PrepareOutputPaths()
    {
        foreach (var kv in _metaById)
        {
            var am = kv.Value;
            if (am == null) continue;

            am.outPath = Path.Combine(_outputFolderAbs, am.agentFileBaseName + outputSuffix + ".jsonl");
            am.nextAnchorToWrite = 0;
        }
    }

    void ClearExistingOutputs()
    {
        try
        {
            foreach (var kv in _metaById)
            {
                var am = kv.Value;
                if (am == null) continue;

                if (File.Exists(am.outPath))
                    File.Delete(am.outPath);
            }

            if (captureCameraImages && clearImagesOnStart && Directory.Exists(_imagesFolderAbs))
            {
                Directory.Delete(_imagesFolderAbs, true);
                Directory.CreateDirectory(_imagesFolderAbs);
            }

            Debug.Log($"[Recorder] Cleared existing outputs in: {_outputFolderAbs}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Recorder] ClearExistingOutputs failed: {e}");
        }
    }

    // ----------------- Proximity/Hit -----------------
    bool ShouldConsiderCollider(Collider c)
    {
        if (c == null) return false;
        if (onlyUseTriggerColliders && !c.isTrigger) return false;
        return true;
    }

    EnvKind GetEnvKindFromHierarchy(Transform t)
    {
        bool hasObs = false, hasBld = false, hasEnt = false, hasVeh = false;

        while (t != null)
        {
            var go = t.gameObject;
            if (go != null)
            {
                if (go.CompareTag(TAG_OBSTACLE)) hasObs = true;
                if (go.CompareTag(TAG_BUILDING)) hasBld = true;
                if (go.CompareTag(TAG_ENTARANCE)) hasEnt = true;
                if (go.CompareTag(TAG_VEHICLE)) hasVeh = true;
            }
            t = t.parent;
        }

        if (hasVeh) return EnvKind.Vehicle;
        if (hasEnt) return EnvKind.Entarance;
        if (hasBld) return EnvKind.Building;
        if (hasObs) return EnvKind.Obstacle;
        return EnvKind.None;
    }

    EnvKind GetEnvKind(Collider c)
    {
        if (c == null) return EnvKind.None;
        return GetEnvKindFromHierarchy(c.transform);
    }

    void ComputeTaggedDistances(
        Vector3 pos,
        out float nearestObs, out bool nearObs, out bool hitObs,
        out float nearestBld, out bool nearBld, out bool hitBld,
        out float nearestEnt, out bool nearEnt, out bool hitEnt,
        out float nearestVeh, out bool nearVeh, out bool hitVeh
    )
    {
        nearestObs = nearestBld = nearestEnt = nearestVeh = float.PositiveInfinity;
        nearObs = nearBld = nearEnt = nearVeh = false;
        hitObs = hitBld = hitEnt = hitVeh = false;

        // ✅ per-type near: 한 번에 maxNear로 후보 수집
        int nNear = Physics.OverlapSphereNonAlloc(pos, _envMaxNearRadius, _nearBuf, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < nNear; i++)
        {
            var c = _nearBuf[i];
            if (!ShouldConsiderCollider(c)) continue;

            var kind = GetEnvKind(c);
            if (kind == EnvKind.None) continue;

            float d = DistanceToCollider(pos, c);

            switch (kind)
            {
                case EnvKind.Obstacle:
                    if (d < nearestObs) nearestObs = d;
                    break;
                case EnvKind.Building:
                    if (d < nearestBld) nearestBld = d;
                    break;
                case EnvKind.Entarance:
                    if (d < nearestEnt) nearestEnt = d;
                    break;
                case EnvKind.Vehicle:
                    if (d < nearestVeh) nearestVeh = d;
                    break;
            }
        }

        if (float.IsPositiveInfinity(nearestObs)) nearestObs = -1f;
        if (float.IsPositiveInfinity(nearestBld)) nearestBld = -1f;
        if (float.IsPositiveInfinity(nearestEnt)) nearestEnt = -1f;
        if (float.IsPositiveInfinity(nearestVeh)) nearestVeh = -1f;

        // ✅ per-type near threshold 적용
        nearObs = (nearestObs >= 0f && nearestObs <= nearRadiusObstacle);
        nearBld = (nearestBld >= 0f && nearestBld <= nearRadiusBuilding);
        nearEnt = (nearestEnt >= 0f && nearestEnt <= nearRadiusEntarance);
        nearVeh = (nearestVeh >= 0f && nearestVeh <= nearRadiusVehicle);

        // ✅ per-type hit: maxHit로 후보 수집 후, closest distance로 각 타입 threshold로 필터링
        int nHit = Physics.OverlapSphereNonAlloc(pos, _envMaxHitRadius, _hitBuf, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < nHit; i++)
        {
            var c = _hitBuf[i];
            if (!ShouldConsiderCollider(c)) continue;

            var kind = GetEnvKind(c);
            if (kind == EnvKind.None) continue;

            float d = DistanceToCollider(pos, c);

            switch (kind)
            {
                case EnvKind.Obstacle:
                    if (d <= hitRadiusObstacle) hitObs = true;
                    break;
                case EnvKind.Building:
                    if (d <= hitRadiusBuilding) hitBld = true;
                    break;
                case EnvKind.Entarance:
                    if (d <= hitRadiusEntarance) hitEnt = true;
                    break;
                case EnvKind.Vehicle:
                    if (d <= hitRadiusVehicle) hitVeh = true;
                    break;
            }

            if (hitObs && hitBld && hitEnt && hitVeh) break;
        }
    }

    static float DistanceToCollider(Vector3 pos, Collider c)
    {
        Vector3 cp = c.ClosestPoint(pos);
        return Vector3.Distance(pos, cp);
    }

    // ----------------- Ground -----------------
    string DetectGroundType(Vector3 worldPos)
    {
        Vector3 origin = worldPos + Vector3.up * rayStartHeight;

        var hits = Physics.RaycastAll(origin, Vector3.down, rayLength, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0) return "unknown";

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            string g = GetGroundTagFromHierarchy(h.collider.transform);
            if (g != "unknown") return g;
        }

        return "unknown";
    }

    string GetGroundTagFromHierarchy(Transform t)
    {
        while (t != null)
        {
            var go = t.gameObject;
            if (go.CompareTag(GROUND_SIDEWALK)) return GROUND_SIDEWALK;
            if (go.CompareTag(GROUND_CROSSWALK)) return GROUND_CROSSWALK;
            if (go.CompareTag(GROUND_ROAD)) return GROUND_ROAD;
            if (go.CompareTag(GROUND_GRASS)) return GROUND_GRASS;
            if (go.CompareTag(GROUND_INSIDE)) return GROUND_INSIDE;
            t = t.parent;
        }
        return "unknown";
    }

    // ----------------- Nearest agent / group -----------------
    (int id, float dist) FindNearestAgent(int selfId, Vector3 pos)
    {
        int nearestId = -1;
        float nearest = float.PositiveInfinity;

        foreach (var kv in _runtimeById)
        {
            int oid = kv.Key;
            if (oid == selfId) continue;

            var tr = kv.Value.tr;
            if (tr == null || !tr.gameObject.activeInHierarchy) continue;

            float d = Vector3.Distance(pos, tr.position);
            if (d < nearest)
            {
                nearest = d;
                nearestId = oid;
            }
        }

        if (nearestId < 0) return (-1, -1f);
        return (nearestId, nearest);
    }

    string DistToState(float d, float closeTh, float farTh)
    {
        if (d < 0) return "none";
        if (d <= closeTh) return "close";
        if (d >= farTh) return "far";
        return "mid";
    }

    // ----------------- Global frame -----------------
    int GetCurrentGlobalFrame()
    {
        if (replayManager == null) return int.MinValue;

        if (_rmField == null && _rmProp == null)
        {
            var t = replayManager.GetType();
            _rmField = t.GetField("currentGlobalFrame");
            if (_rmField == null) _rmProp = t.GetProperty("currentGlobalFrame");
        }

        if (_rmField != null) return (int)_rmField.GetValue(replayManager);
        if (_rmProp != null) return (int)_rmProp.GetValue(replayManager);
        return int.MinValue;
    }

    // ----------------- Utils -----------------
    int ParseAgentId(string name)
    {
        try
        {
            int idx = name.IndexOf(agentNamePrefix, StringComparison.Ordinal);
            if (idx < 0) return -1;

            string rest = name.Substring(idx + agentNamePrefix.Length);
            string num = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(num, out int id)) return id;
        }
        catch { }
        return -1;
    }

    // ----------------- Debug HUD -----------------
    [Header("Debug HUD")]
    public bool showDebugHUD = true;
    public bool debugOnlyActiveAgents = true;
    public int debugMaxLines = 20;
    public float debugFontSize = 14f;

    void OnGUI()
    {
        if (!showDebugHUD) return;

        int gf = GetCurrentGlobalFrame();
        GUIStyle st = new GUIStyle(GUI.skin.label);
        st.fontSize = (int)debugFontSize;
        st.normal.textColor = Color.white;

        float y = 10f;
        GUI.Label(new Rect(10, y, 1900, 30),
            $"[Recorder] gf={gf} range=[{_globalMin}..{_globalMax}] outFolder={_outputFolderAbs} imagesFolder={_imagesFolderAbs}",
            st);
        y += 22f;

        int shown = 0;
        foreach (var kv in _runtimeById.OrderBy(k => k.Key))
        {
            if (shown >= debugMaxLines) break;

            int id = kv.Key;
            var ar = kv.Value;
            if (ar == null || ar.tr == null) continue;

            bool active = ar.tr.gameObject.activeInHierarchy;
            if (debugOnlyActiveAgents && !active) continue;

            string line = $"agent {id} active={active} cam={(ar.cam ? "Y" : "N")} ";
            if (_metaById.TryGetValue(id, out var am))
            {
                line += $"anchorProgress={am.nextAnchorToWrite}/{am.anchors.Count} ";
                if (am.nextAnchorToWrite < am.anchors.Count)
                    line += $"next(start={am.anchors[am.nextAnchorToWrite].startGf}, end={am.anchors[am.nextAnchorToWrite].endGf}) ";

                if (am.trajEndGf != int.MinValue && gf != int.MinValue)
                    line += $"trajEndGf={am.trajEndGf} remTrajSec={Mathf.Max(0f, (am.trajEndGf - gf) * timePerFrame):0.00} ";
            }

            if (gf != int.MinValue && ar.tagByFrame.TryGetValue(gf, out var tag))
            {
                line += $"REC ✔ ground={tag.ground} ";
                line += $"nearA={tag.near_agent} hitA={tag.hit_agent} nA(id={tag.nearestAgentId}, d={tag.nearestAgentDist:0.00}) ";
                line += $"hit Obs/Bld/Ent/Veh=({tag.hit_obstacle}/{tag.hit_building}/{tag.hit_entarance}/{tag.hit_vehicle}) ";
                line += $"groupActive={tag.groupActiveCount} ";
            }
            else
            {
                line += "REC ✘";
            }

            GUI.Label(new Rect(10, y, 1900, 22), line, st);
            y += 20f;
            shown++;
        }
    }
}
