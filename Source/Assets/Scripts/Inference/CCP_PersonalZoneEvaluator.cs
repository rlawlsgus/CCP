using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CCP_PersonalZoneEvaluator : MonoBehaviour
{
    public static CCP_PersonalZoneEvaluator Instance;

    [Header("Evaluation Settings")]
    public float personalZoneRadius = 1.5f;
    public Transform agentsRoot;

    [Header("Logging Settings")]
    public string saveSubFolder = "MetricsLogs";
    public bool verbose = true;

    [Header("Sampling Settings")]
    public float samplingInterval = 0.04f;

    [Serializable]
    public class PersonalZoneMetricsReport
    {
        public string sceneName;
        public string date;
        public string time;
        public int totalSamples;
        public float personalZoneRadius;
        public float averageViolatorsPerAgent;
        public float violationRate;
        public List<ViolatorCountEntry> histogramEntries;
    }

    [Serializable]
    public class ViolatorCountEntry { public int violatorCount; public int frameCount; }

    private Dictionary<int, int> _violatorsCountHistogram = new Dictionary<int, int>();
    private List<int> _allViolationCounts = new List<int>();
    private int _totalFramesSampled = 0;
    private int _framesWithViolations = 0;
    private int _lastProcessedFrame = -1;
    private float _accumulatedSimTime = 0f;
    private bool _hasExported = false;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    void LateUpdate()
    {
        if (Time.timeScale > 0) _accumulatedSimTime += Time.deltaTime;
        int currentFrame = Mathf.FloorToInt(_accumulatedSimTime / samplingInterval + 0.001f);
        if (currentFrame > _lastProcessedFrame)
        {
            for (int f = _lastProcessedFrame + 1; f <= currentFrame; f++) SamplePersonalZoneViolations();
            _lastProcessedFrame = currentFrame;
        }
    }

    void SamplePersonalZoneViolations()
    {
        var agentsT = (agentsRoot != null) ? agentsRoot.GetComponentsInChildren<Agent_Training>() : FindObjectsOfType<Agent_Training>();
        var agentsGO = (agentsRoot != null) ? agentsRoot.GetComponentsInChildren<Agent_GoalOnly_Training>() : FindObjectsOfType<Agent_GoalOnly_Training>();
        List<GameObject> allAgents = new List<GameObject>();
        foreach (var a in agentsT) if (a.gameObject.activeInHierarchy) allAgents.Add(a.gameObject);
        foreach (var a in agentsGO) if (a.gameObject.activeInHierarchy) allAgents.Add(a.gameObject);

        if (allAgents.Count == 0) return;
        int frameTotalViolators = 0; bool frameHasViolation = false;

        foreach (var agent in allAgents)
        {
            if (agent == null) continue;
            int agentViolators = 0; Vector3 pos = agent.transform.position;
            foreach (var other in allAgents)
            {
                if (other == null || other == agent) continue;
                if (IsSameGroup(agent, other)) continue;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(other.transform.position.x, other.transform.position.z)) < personalZoneRadius) agentViolators++;
            }
            if (agentViolators > 0) { frameHasViolation = true; frameTotalViolators += agentViolators; }
            if (!_violatorsCountHistogram.ContainsKey(agentViolators)) _violatorsCountHistogram[agentViolators] = 0;
            _violatorsCountHistogram[agentViolators]++;
            _allViolationCounts.Add(agentViolators);
        }
        _totalFramesSampled++;
        if (frameHasViolation) _framesWithViolations++;
    }

    private bool IsSameGroup(GameObject a, GameObject b)
    {
        var at = a.GetComponent<Agent_Training>();
        var ago = a.GetComponent<Agent_GoalOnly_Training>();
        List<Transform> members = (at != null) ? at.groupMembers : (ago != null ? ago.groupMembers : null);
        return members != null && members.Contains(b.transform);
    }

    public void ExportToJson()
    {
        if (_allViolationCounts.Count == 0 || _hasExported) return;
        _hasExported = true;
        PersonalZoneMetricsReport report = new PersonalZoneMetricsReport
        {
            sceneName = SceneManager.GetActiveScene().name,
            date = DateTime.Now.ToString("yyyy-MM-dd"),
            time = DateTime.Now.ToString("HH-mm-ss"),
            totalSamples = _allViolationCounts.Count,
            personalZoneRadius = personalZoneRadius,
            averageViolatorsPerAgent = (float)_allViolationCounts.Average(),
            violationRate = _totalFramesSampled > 0 ? (float)_framesWithViolations / _totalFramesSampled : 0f,
            histogramEntries = _violatorsCountHistogram.Select(kvp => new ViolatorCountEntry { violatorCount = kvp.Key, frameCount = kvp.Value }).OrderBy(e => e.violatorCount).ToList()
        };
        string path = Path.Combine(Application.dataPath, "..", saveSubFolder);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"{report.sceneName}_{report.date}_{report.time}_PersonalZoneMetrics.json"), JsonUtility.ToJson(report, true));
    }

    void OnApplicationQuit() { ExportToJson(); }
}
