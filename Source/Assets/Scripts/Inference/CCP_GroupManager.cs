using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CCP_GroupManager : MonoBehaviour
{
    public static CCP_GroupManager Instance;

    [Header("Logging Settings")]
    public string saveSubFolder = "MetricsLogs";
    public bool verbose = true;

    [Header("Sampling Settings")]
    public float samplingInterval = 0.04f;

    private List<float> allAgentToCentroidDistances = new List<float>();
    private int lastProcessedFrame = -1;
    private float _accumulatedSimTime = 0f;
    private bool hasExported = false;

    [Serializable]
    public class GroupMetricsReport
    {
        public string sceneName;
        public string date;
        public string time;
        public int totalSamples;
        public float meanDistanceToCentroid;
        public float stdDevDistanceToCentroid;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    void LateUpdate()
    {
        if (Time.timeScale > 0) _accumulatedSimTime += Time.deltaTime;
        int currentFrame = Mathf.FloorToInt(_accumulatedSimTime / samplingInterval + 0.001f);
        if (currentFrame > lastProcessedFrame)
        {
            for (int f = lastProcessedFrame + 1; f <= currentFrame; f++) SampleGroupDistances();
            lastProcessedFrame = currentFrame;
        }
    }

    void SampleGroupDistances()
    {
        var agentsT = FindObjectsOfType<Agent_Training>();
        var agentsGO = FindObjectsOfType<Agent_GoalOnly_Training>();
        List<GameObject> allAgents = new List<GameObject>();
        foreach (var a in agentsT) if (a.gameObject.activeInHierarchy) allAgents.Add(a.gameObject);
        foreach (var a in agentsGO) if (a.gameObject.activeInHierarchy) allAgents.Add(a.gameObject);

        if (allAgents.Count == 0) return;
        HashSet<GameObject> processed = new HashSet<GameObject>();

        foreach (var go in allAgents)
        {
            if (go == null || processed.Contains(go)) continue;
            
            var at = go.GetComponent<Agent_Training>();
            var ago = go.GetComponent<Agent_GoalOnly_Training>();
            List<Transform> members = (at != null) ? at.groupMembers : (ago != null ? ago.groupMembers : null);
            
            if (members == null || members.Count == 0) continue;

            HashSet<GameObject> groupSet = new HashSet<GameObject> { go };
            Queue<GameObject> queue = new Queue<GameObject>();
            queue.Enqueue(go);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                var cAt = curr.GetComponent<Agent_Training>();
                var cAgo = curr.GetComponent<Agent_GoalOnly_Training>();
                List<Transform> cMembers = (cAt != null) ? cAt.groupMembers : (cAgo != null ? cAgo.groupMembers : null);
                
                if (cMembers == null) continue;
                foreach (var m in cMembers)
                {
                    if (m != null && !groupSet.Contains(m.gameObject))
                    {
                        groupSet.Add(m.gameObject);
                        queue.Enqueue(m.gameObject);
                    }
                }
            }

            foreach (var member in groupSet) processed.Add(member);

            if (groupSet.Count > 1)
            {
                Vector3 centroid = Vector3.zero;
                foreach (var member in groupSet) centroid += member.transform.position;
                centroid /= groupSet.Count;
                foreach (var member in groupSet)
                {
                    float dist = Vector2.Distance(new Vector2(member.transform.position.x, member.transform.position.z), new Vector2(centroid.x, centroid.z));
                    allAgentToCentroidDistances.Add(dist);
                }
            }
        }
    }

    public void ExportToJson()
    {
        if (allAgentToCentroidDistances.Count == 0 || hasExported) return;
        hasExported = true;
        float mean = allAgentToCentroidDistances.Average();
        GroupMetricsReport report = new GroupMetricsReport
        {
            sceneName = SceneManager.GetActiveScene().name,
            date = DateTime.Now.ToString("yyyy-MM-dd"),
            time = DateTime.Now.ToString("HH-mm-ss"),
            totalSamples = allAgentToCentroidDistances.Count,
            meanDistanceToCentroid = mean,
            stdDevDistanceToCentroid = (float)Math.Sqrt(allAgentToCentroidDistances.Sum(v => Math.Pow(v - mean, 2)) / allAgentToCentroidDistances.Count)
        };
        string path = Path.Combine(Application.dataPath, "..", saveSubFolder);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"{report.sceneName}_{report.date}_{report.time}_GroupMetrics.json"), JsonUtility.ToJson(report, true));
    }

    void OnApplicationQuit() { ExportToJson(); }
}
