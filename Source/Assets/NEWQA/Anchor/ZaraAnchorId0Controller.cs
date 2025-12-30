using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// ZaraGroupSimulator가 만든 ped_0(또는 anchorId)을 서버(Anchor)로 제어.
/// - 서버 요청 중에는 정지(이동 코루틴 실행 X)
/// - 시작 위치: trajectoriesTxt에서 id의 최소 frame 위치로 세팅(optional)
/// - goal 위치: trajectoriesTxt에서 id의 최대 frame 위치로 세팅(optional) 또는 goalOverride 사용
/// - goal_position 전송: (goalWorld - currentWorld)로 계산한 goalLocalPosition(회전 무시, 평행이동 기준)
/// - ZaraGroupSimulator가 해당 id를 더 이상 업데이트하지 않도록 내부 딕셔너리에서 제거(reflection)
/// </summary>
[DisallowMultipleComponent]
public class ZaraAnchorId0Controller : MonoBehaviour
{
    // -------------------- Bind --------------------
    [Header("Bind To Zara Simulator")]
    public ZaraGroupSimulator simulator;          // 비우면 자동 검색
    public bool autoFindSimulator = true;
    public int anchorId = 0;                      // 제어할 agent id
    public bool onlyRunWhenNameMatchesId = true;  // 오브젝트 이름 ped_0 등에서 id 파싱하여 일치할 때만 동작
    public bool detachFromSimulatorControl = true;

    // -------------------- Start/Goal --------------------
    [Header("Start/Goal Assignment")]
    public bool setStartFromTrajectory = true;
    public bool setGoalFromTrajectoryEnd = true;
    public Transform goalOverride;                // 지정하면 이 Transform 위치를 goal로 사용
    public bool createGoalObjectIfMissing = true;
    public string autoGoalObjectName = "AnchorGoal_id0";

    // -------------------- Server --------------------
    [Header("Server Settings")]
    [SerializeField] private string serverUrl = "http://163.152.162.171:8000/predict";
    public int requestTimeoutSec = 5;

    // -------------------- Movement --------------------
    [Header("Movement Settings")]
    public float moveSpeed = 2.0f;
    public float turnSpeed = 5.0f;
    public float goalReachThreshold = 1.0f;
    public float moveDurationPerStep = 0.30f;     // 서버 응답 1회마다 이동 시간

    // -------------------- Input Coord (server expects local x,z) --------------------
    [Header("Coordinate Settings")]
    public bool flipGoalX = false;                // goal_position의 x 부호 뒤집기(학습/서버 기준)
    public bool flipGoalZ = false;                // goal_position의 z 부호 뒤집기(학습/서버 기준)
    public bool flipPredX = false;                // 서버 output x 부호 뒤집기
    public bool flipPredZ = false;                // 서버 output z 부호 뒤집기

    // -------------------- Remaining Time --------------------
    [Header("Remaining Time")]
    public float remainingTimeOverride = -1f;     // >0이면 고정, 아니면 (endFrame-startFrame)/fps
    private float _currentRemainingTime = 0f;

    // -------------------- Camera --------------------
    [Header("Camera Settings")]
    public Camera agentCamera;
    public int imageWidth = 224;
    public int imageHeight = 224;

    // -------------------- Internal --------------------
    private RenderTexture _renderTexture;
    private Texture2D _texture2D;

    private Vector3 _globalGoalPosition;
    private Vector3 _predictedWorldTarget;

    private bool _isGoalReached = false;
    private bool _requestInFlight = false;

    // simulator mapping snapshot
    private bool _useHomography;
    private bool _simFlipX;
    private bool _simFlipZ;
    private float _worldScale;
    private Vector3 _worldOffset;
    private Matrix3x3 _H = Matrix3x3.Identity;

    private Transform _goalTransform;
    private int _parsedId = -1;

    // -------------------- Server response struct --------------------
    [Serializable]
    public class ServerResponseData
    {
        public float[][][] output;
        public float latency_sec;
        public float[] goal_position;
        public float remaining_time;
    }

    void Awake()
    {
        if (onlyRunWhenNameMatchesId)
        {
            _parsedId = ParseIdFromName(gameObject.name);
            if (_parsedId != anchorId)
            {
                enabled = false;
                return;
            }
        }

        // 중복 제어 방지(기존 AnchorClient가 붙어있다면 끔)
        var legacy = GetComponent<AnchorClient>();
        if (legacy != null) legacy.enabled = false;
    }

    IEnumerator Start()
    {
        if (!enabled) yield break;

        if (simulator == null && autoFindSimulator)
            simulator = FindObjectOfType<ZaraGroupSimulator>();

        if (simulator == null)
        {
            Debug.LogError("[ZaraAnchorId0Controller] ZaraGroupSimulator를 찾지 못했습니다.");
            yield break;
        }

        SnapshotSimulatorMapping(simulator);

        if (_useHomography && simulator.homographyTxt != null)
            _H = ParseHomography3x3(simulator.homographyTxt.text);

        // simulator가 anchorId를 덮어쓰지 않도록 분리
        if (detachFromSimulatorControl)
            DetachIdFromSimulator(simulator, anchorId);

        // start/goal from trajectories
        bool ok = TryGetStartEndForId(simulator.trajectoriesTxt, anchorId,
            out int startFrame, out Vector2 startXY,
            out int endFrame, out Vector2 endXY);

        Vector3 startWorld = transform.position;
        Vector3 goalWorld = transform.position + transform.forward * 5f;

        if (ok)
        {
            if (setStartFromTrajectory)
                startWorld = MapToWorld(startXY);

            if (setGoalFromTrajectoryEnd && goalOverride == null)
                goalWorld = MapToWorld(endXY);
        }
        else
        {
            Debug.LogWarning("[ZaraAnchorId0Controller] trajectoriesTxt에서 anchorId 데이터를 찾지 못했습니다. 현재 위치/임시 goal 사용.");
        }

        // start 적용
        if (setStartFromTrajectory && ok)
            transform.position = startWorld;

        // goal Transform 결정/생성
        _goalTransform = goalOverride;
        if (_goalTransform == null)
        {
            if (createGoalObjectIfMissing)
            {
                var go = new GameObject(autoGoalObjectName);
                go.transform.position = goalWorld;
                _goalTransform = go.transform;
            }
        }
        else
        {
            // override가 지정되어 있으면 그 위치를 goal로
            goalWorld = _goalTransform.position;
        }

        _globalGoalPosition = (_goalTransform != null) ? _goalTransform.position : goalWorld;

        // remaining time 설정
        if (remainingTimeOverride > 0f)
        {
            _currentRemainingTime = remainingTimeOverride;
        }
        else if (ok)
        {
            float fps = Mathf.Max(1f, simulator.fps);
            _currentRemainingTime = Mathf.Max(0f, (endFrame - startFrame) / fps);
        }
        else
        {
            _currentRemainingTime = 30f;
        }

        InitializeCamera();

        StartCoroutine(CoLifeCycleLoop());
        yield break;
    }

    void Update()
    {
        if (!enabled) return;
        if (_isGoalReached) return;

        if (_currentRemainingTime > 0f)
            _currentRemainingTime = Mathf.Max(0f, _currentRemainingTime - Time.deltaTime);

        // 요청 중에는 정지 유지(물리 영향 방지)
        if (_requestInFlight)
        {
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    void OnDestroy()
    {
        if (_renderTexture != null) _renderTexture.Release();
        if (_texture2D != null) Destroy(_texture2D);
    }

    IEnumerator CoLifeCycleLoop()
    {
        while (!_isGoalReached)
        {
            // goal 도달 체크
            float distToGoal = Vector3.Distance(transform.position, _globalGoalPosition);
            if (distToGoal < goalReachThreshold)
            {
                _isGoalReached = true;
                gameObject.SetActive(false);
                yield break;
            }

            // 캡처
            byte[] imageBytes = CaptureView();
            if (imageBytes == null || imageBytes.Length == 0)
            {
                yield return new WaitForSeconds(0.2f);
                continue;
            }

            // goalLocalPosition 전송(회전 무시 / world offset)
            string goalLocalJson = GetGoalLocalPositionJson(_globalGoalPosition);

            string timeStr = _currentRemainingTime.ToString("F2", CultureInfo.InvariantCulture);

            _requestInFlight = true;
            string jsonResponse = null;
            yield return StartCoroutine(CoSendRequest(imageBytes, goalLocalJson, timeStr, (res) => jsonResponse = res));
            _requestInFlight = false;

            if (string.IsNullOrEmpty(jsonResponse))
            {
                yield return new WaitForSeconds(0.2f);
                continue;
            }

            yield return StartCoroutine(CoMoveBasedOnPrediction(jsonResponse));
        }
    }

    IEnumerator CoMoveBasedOnPrediction(string json)
    {
        ServerResponseData data = null;
        try
        {
            data = JsonConvert.DeserializeObject<ServerResponseData>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ZaraAnchorId0Controller] JSON parse error: {e.Message}");
            yield break;
        }

        if (data?.output == null || data.output.Length == 0) yield break;

        float[][] trajectory = data.output[0];
        if (trajectory == null || trajectory.Length == 0) yield break;

        float[] lastPoint = trajectory[trajectory.Length - 1];
        if (lastPoint == null || lastPoint.Length < 2) yield break;

        float predX = flipPredX ? -lastPoint[0] : lastPoint[0];
        float predZ = flipPredZ ? -lastPoint[1] : lastPoint[1];

        // 서버 output은 "로컬 offset"이라고 가정 -> 월드로 변환
        Vector3 localTargetPos = new Vector3(predX, 0f, predZ);
        _predictedWorldTarget = transform.TransformPoint(localTargetPos);
        _predictedWorldTarget.y = transform.position.y;

        float elapsed = 0f;
        while (elapsed < moveDurationPerStep)
        {
            Vector3 dir = _predictedWorldTarget - transform.position;
            dir.y = 0f;

            if (dir.sqrMagnitude > 1e-8f)
            {
                Vector3 dirN = dir.normalized;
                Quaternion targetRot = Quaternion.LookRotation(dirN);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
                transform.position += transform.forward * moveSpeed * Time.deltaTime;
            }

            elapsed += Time.deltaTime;
            yield return null;
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

    // -------------------- Goal Local Position (Python goalLocalPosition 방식) --------------------
    string GetGoalLocalPositionJson(Vector3 goalWorld)
    {
        // goalLocalPosition = goalWorld - currentWorld (회전 무시)
        Vector3 offset = goalWorld - transform.position;

        float gx = flipGoalX ? -offset.x : offset.x;
        float gz = flipGoalZ ? -offset.z : offset.z;

        return $"[{gx.ToString("F4", CultureInfo.InvariantCulture)},{gz.ToString("F4", CultureInfo.InvariantCulture)}]";
    }

    // -------------------- Camera --------------------
    void InitializeCamera()
    {
        if (agentCamera == null) agentCamera = GetComponentInChildren<Camera>();
        if (agentCamera == null)
        {
            Debug.LogError("[ZaraAnchorId0Controller] agentCamera를 찾지 못했습니다.");
            return;
        }

        _renderTexture = new RenderTexture(imageWidth, imageHeight, 24);
        _texture2D = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
    }

    byte[] CaptureView()
    {
        if (agentCamera == null || _renderTexture == null || _texture2D == null) return null;

        RenderTexture prev = RenderTexture.active;
        agentCamera.targetTexture = _renderTexture;
        agentCamera.Render();

        RenderTexture.active = _renderTexture;
        _texture2D.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
        _texture2D.Apply();

        agentCamera.targetTexture = null;
        RenderTexture.active = prev;

        return _texture2D.EncodeToPNG();
    }

    // -------------------- Zara trajectory parse (id -> start/end) --------------------
    static bool TryGetStartEndForId(TextAsset trajectoriesTxt, int id,
        out int startFrame, out Vector2 startXY,
        out int endFrame, out Vector2 endXY)
    {
        startFrame = int.MaxValue;
        endFrame = int.MinValue;
        startXY = default;
        endXY = default;

        if (trajectoriesTxt == null || string.IsNullOrEmpty(trajectoriesTxt.text))
            return false;

        var inv = CultureInfo.InvariantCulture;
        using var sr = new StringReader(trajectoriesTxt.text);
        string line;

        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 5) continue;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, inv, out int pid)) continue;
            if (pid != id) continue;

            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, inv, out float x)) continue;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, inv, out float y)) continue;
            if (!int.TryParse(parts[3].Trim(), NumberStyles.Integer, inv, out int frame)) continue;

            if (frame < startFrame)
            {
                startFrame = frame;
                startXY = new Vector2(x, y);
            }
            if (frame > endFrame)
            {
                endFrame = frame;
                endXY = new Vector2(x, y);
            }
        }

        return endFrame >= startFrame && startFrame != int.MaxValue;
    }

    // -------------------- Mapping (same as ZaraGroupSimulator) --------------------
    void SnapshotSimulatorMapping(ZaraGroupSimulator sim)
    {
        _useHomography = sim.useHomography;
        _simFlipX = sim.flipX;
        _simFlipZ = sim.flipZ;
        _worldScale = sim.worldScale;
        _worldOffset = sim.worldOffset;
    }

    Vector3 MapToWorld(Vector2 rawXY)
    {
        Vector2 world2 = _useHomography ? ApplyHomography(_H, rawXY) : rawXY;
        Vector3 xz = ToUnityXZ(world2, _simFlipX, _simFlipZ);
        return xz * _worldScale + _worldOffset;
    }

    static Vector3 ToUnityXZ(Vector2 xy, bool flipX, bool flipZ)
    {
        float x = flipX ? -xy.x : xy.x;
        float z = flipZ ? -xy.y : xy.y;
        return new Vector3(x, 0f, z);
    }

    static Vector2 ApplyHomography(Matrix3x3 h, Vector2 uv)
    {
        float u = uv.x, v = uv.y;
        float x = h.m00 * u + h.m01 * v + h.m02;
        float y = h.m10 * u + h.m11 * v + h.m12;
        float w = h.m20 * u + h.m21 * v + h.m22;

        if (!Mathf.Approximately(w, 0f))
        {
            x /= w; y /= w;
        }
        return new Vector2(x, y);
    }

    static Matrix3x3 ParseHomography3x3(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        float[] vals = new float[9];
        int count = 0;

        using var sr = new StringReader(text);
        string line;
        while ((line = sr.ReadLine()) != null && count < 9)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            var toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < toks.Length && count < 9; i++)
            {
                if (float.TryParse(toks[i], NumberStyles.Float, inv, out float f))
                    vals[count++] = f;
            }
        }

        if (count < 9) return Matrix3x3.Identity;

        return new Matrix3x3(
            vals[0], vals[1], vals[2],
            vals[3], vals[4], vals[5],
            vals[6], vals[7], vals[8]
        );
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

    // -------------------- Detach from Simulator (reflection) --------------------
    static void DetachIdFromSimulator(ZaraGroupSimulator sim, int id)
    {
        // simulator 내부: goById / animById / prevPosById / prevFrameById / currentWorldPos
        RemoveKey(sim, "goById", id);
        RemoveKey(sim, "animById", id);
        RemoveKey(sim, "prevPosById", id);
        RemoveKey(sim, "prevFrameById", id);
        RemoveKey(sim, "currentWorldPos", id);

        // trajById까지 제거하면 완전 분리(원하면 아래도 켜기)
        // RemoveKey(sim, "trajById", id);
    }

    static void RemoveKey(object obj, string fieldName, int key)
    {
        try
        {
            var f = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return;

            var dictObj = f.GetValue(obj) as System.Collections.IDictionary;
            if (dictObj == null) return;

            if (dictObj.Contains(key))
                dictObj.Remove(key);
        }
        catch
        {
            // reflection 실패는 조용히 무시
        }
    }

    static int ParseIdFromName(string name)
    {
        // ped_0, ped_12 등 -> 끝 숫자 파싱
        var m = Regex.Match(name, @"(\d+)$");
        if (!m.Success) return -1;
        return int.TryParse(m.Groups[1].Value, out int id) ? id : -1;
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !enabled) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(_globalGoalPosition, 0.5f);
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(transform.position, _predictedWorldTarget);
    }
}
