using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using System.Linq;
using System.Globalization; // 날짜 포맷용

[System.Serializable]
public class ChunkData
{
    public List<int> globalFrames;
    public float[] startWorldPosition;
    public float[][] localCurrent;
    public List<int> groups;
}

public class GlobalCrowdReplayer : MonoBehaviour
{
    [Header("Simulation Settings")]
    public int targetFPS = 30; // ★ 정수형 FPS 설정 (Time.captureFramerate용)
    public string runName = "MySimulationRun";

    [Header("Data Settings")]
    public string dataFolderPath = "DataStep3/case_continuous";
    public GameObject agentPrefab;

    [Header("Visualization")]
    public bool showGroupGizmos = true;
    public Color groupLineColor = Color.cyan;

    [Header("Runtime Info")]
    public bool isReady = false;
    public int currentGlobalFrame; // 이제 float가 아니라 int로 직접 제어
    public int minFrameFound = int.MaxValue;
    public int maxFrameFound = int.MinValue;

    private bool _isPlaying = false;
    private string _currentRunFolder; // 이번 실행의 저장 루트 폴더

    // --- 내부 구조 ---
    public class FrameData
    {
        public Pose pose;
        public List<int> groupIds;
    }

    private class AgentRuntime
    {
        public int agentId;
        public GameObject instance;
        public RecorderCounter recorder;
        public Dictionary<int, FrameData> timeline = new Dictionary<int, FrameData>();
        public int myStartFrame = int.MaxValue;
        public int myEndFrame = int.MinValue;
    }

    private List<AgentRuntime> _agents = new List<AgentRuntime>();
    private Dictionary<int, AgentRuntime> _agentLookup = new Dictionary<int, AgentRuntime>();

    void Awake()
    {
        // ★ [핵심] Zara 코드처럼 프레임레이트를 고정하여 결정론적(Deterministic)으로 실행
        Time.captureFramerate = targetFPS;
        Application.targetFrameRate = targetFPS;
        Debug.Log($"[System] Capture Framerate set to {targetFPS}. Recording will be frame-perfect.");
    }

    void Start()
    {
        // 저장 경로 설정 (PersistentDataPath/RunName_Timestamp)
        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _currentRunFolder = Path.Combine(Application.persistentDataPath, $"{runName}_{stamp}");
        Directory.CreateDirectory(_currentRunFolder);
        Debug.Log($"[System] Output Folder Created: {_currentRunFolder}");

        StartCoroutine(CoLoadAllFiles());
    }

    void OnDestroy()
    {
        Time.captureFramerate = 0; // 종료 시 해제
    }

    public Vector3? GetAgentPosition(int agentId)
    {
        if (_agentLookup.TryGetValue(agentId, out AgentRuntime agent))
        {
            if (agent.instance != null && agent.instance.activeSelf)
                return agent.instance.transform.position;
        }
        return null;
    }

    // --- 파일 로딩 (기존 로직 유지 + Recorder 초기화 시 경로 전달) ---
    IEnumerator CoLoadAllFiles()
    {
        isReady = false;
        _isPlaying = false;

        foreach (var agent in _agents) if (agent.instance != null) Destroy(agent.instance);
        _agents.Clear();
        _agentLookup.Clear();

        string path = dataFolderPath;
        if (!Path.IsPathRooted(path)) path = Path.Combine(Application.dataPath, "..", path);
        if (Path.HasExtension(path)) path = Path.GetDirectoryName(path);

        if (!Directory.Exists(path)) { Debug.LogError($"[오류] 폴더 없음: {path}"); yield break; }

        string[] files = Directory.GetFiles(path, "*.jsonl");
        Debug.Log($"[시스템] 로딩 시작 ({files.Length}개)...");

        foreach (string file in files) yield return StartCoroutine(CoLoadSingleAgent(file));

        Debug.Log($"[시스템] 로딩 완료. 총 에이전트: {_agents.Count}명");

        if (_agents.Count > 0)
        {
            currentGlobalFrame = minFrameFound; // 시작 프레임 설정
            isReady = true;
            _isPlaying = true;
        }
    }

    IEnumerator CoLoadSingleAgent(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string[] lines = File.ReadAllLines(filePath);
        int parsedId = -1;
        var parts = fileName.Split('_');
        if (parts.Length >= 2 && int.TryParse(parts[1], out int tempId)) parsedId = tempId;

        GameObject go = Instantiate(agentPrefab, transform);
        go.name = fileName;

        AgentRuntime newAgent = new AgentRuntime();
        newAgent.instance = go;
        newAgent.agentId = parsedId;

        newAgent.recorder = go.GetComponent<RecorderCounter>();
        if (newAgent.recorder == null) newAgent.recorder = go.AddComponent<RecorderCounter>();

        // ★ [수정] 초기화 시 생성한 저장 폴더 경로를 넘겨줌
        newAgent.recorder.Initialize(this, parsedId, _currentRunFolder);

        var settings = new JsonSerializerSettings { Error = (sender, args) => { args.ErrorContext.Handled = true; } };

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var chunk = JsonConvert.DeserializeObject<ChunkData>(line, settings);
                if (chunk == null) continue;

                Vector3 startPos = ToVector3(chunk.startWorldPosition);
                int count = Mathf.Min(chunk.globalFrames.Count, chunk.localCurrent.Length);
                List<int> gIds = new List<int>();
                if (chunk.groups != null) gIds.AddRange(chunk.groups);

                for (int i = 0; i < count; i++)
                {
                    int gFrame = chunk.globalFrames[i];
                    Vector3 offset = ToVector3(chunk.localCurrent[i]);
                    Vector3 currentPos = startPos + offset;

                    // 회전 계산
                    Quaternion rotation = Quaternion.identity;
                    if (i < count - 1)
                    {
                        Vector3 nextPos = startPos + ToVector3(chunk.localCurrent[i + 1]);
                        Vector3 dir = (nextPos - currentPos).normalized;
                        if (dir != Vector3.zero) rotation = Quaternion.LookRotation(dir);
                    }
                    else if (i > 0)
                    {
                        Vector3 prevPos = startPos + ToVector3(chunk.localCurrent[i - 1]);
                        Vector3 dir = (currentPos - prevPos).normalized;
                        if (dir != Vector3.zero) rotation = Quaternion.LookRotation(dir);
                    }

                    newAgent.timeline[gFrame] = new FrameData
                    {
                        pose = new Pose(currentPos, rotation),
                        groupIds = gIds
                    };

                    if (gFrame < minFrameFound) minFrameFound = gFrame;
                    if (gFrame > maxFrameFound) maxFrameFound = gFrame;
                    if (gFrame < newAgent.myStartFrame) newAgent.myStartFrame = gFrame;
                    if (gFrame > newAgent.myEndFrame) newAgent.myEndFrame = gFrame;
                }
            }
            catch { }
        }
        _agents.Add(newAgent);
        if (parsedId != -1 && !_agentLookup.ContainsKey(parsedId)) _agentLookup.Add(parsedId, newAgent);
        yield return null;
    }

    // --- 프레임 업데이트 (결정론적 방식) ---
    void Update()
    {
        if (!isReady || !_isPlaying) return;

        // ★ [핵심 수정] Time.deltaTime을 더하는 게 아니라, 그냥 1프레임씩 증가시킴
        // Time.captureFramerate가 설정되어 있으므로, 이 Update는 정확한 간격으로 호출된 것으로 간주됨.

        // 1. 위치 업데이트
        foreach (var agent in _agents)
        {
            if (agent.instance == null) continue;
            if (agent.timeline.TryGetValue(currentGlobalFrame, out FrameData currentData))
            {
                if (!agent.instance.activeSelf) agent.instance.SetActive(true);
                agent.instance.transform.SetPositionAndRotation(currentData.pose.position, currentData.pose.rotation);
            }
            else
            {
                if (agent.instance.activeSelf) agent.instance.SetActive(false);
            }
        }

        // 2. 분석 및 저장 (1프레임도 놓치지 않음)
        foreach (var agent in _agents)
        {
            if (agent.instance == null || !agent.instance.activeSelf) continue;

            if (currentGlobalFrame >= agent.myEndFrame)
            {
                agent.recorder.FinishRecording();
            }
            else if (agent.timeline.TryGetValue(currentGlobalFrame, out FrameData currentData))
            {
                int framesLeft = Mathf.Max(0, agent.myEndFrame - currentGlobalFrame);
                float timeLeftSeconds = (float)framesLeft / (float)targetFPS; // FPS 설정값 사용

                agent.recorder.AnalyzeFrame(currentData.groupIds, timeLeftSeconds);
            }
        }

        // 3. 프레임 증가
        currentGlobalFrame++;

        // 종료 조건 (옵션: 루프 돌지 않고 멈춤)
        if (currentGlobalFrame > maxFrameFound + 5) // 여유 있게 조금 더 돌고 멈춤
        {
            Debug.Log("[System] All frames processed.");
            _isPlaying = false;
            // 에디터라면 플레이 종료
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    // --- Gizmos ---
    void OnDrawGizmos()
    {
        if (!isReady || !showGroupGizmos) return;
        Gizmos.color = groupLineColor;
        foreach (var agent in _agents)
        {
            if (agent.instance == null || !agent.instance.activeSelf) continue;
            if (agent.timeline.TryGetValue(currentGlobalFrame, out FrameData data) && data.groupIds != null)
            {
                foreach (int nId in data.groupIds)
                {
                    if (nId == agent.agentId) continue;
                    if (_agentLookup.TryGetValue(nId, out AgentRuntime neighbor) && neighbor.instance.activeSelf)
                        Gizmos.DrawLine(agent.instance.transform.position, neighbor.instance.transform.position);
                }
            }
        }
    }

    Vector3 ToVector3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}