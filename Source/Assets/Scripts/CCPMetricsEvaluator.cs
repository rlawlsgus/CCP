using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;
using System;

public class CCPMetricsEvaluator : MonoBehaviour
{
    [Header("Configuration")]
    public GameObject agentPrefab;
    public ZaraGroupRotationSimulator simulator;
    public Transform obstaclesParent;
    public float sampleInterval = 0.8f;
    public float agentTimeout = 30.0f; // Agents older than this will be removed

    [Header("Metrics Settings")]
    public bool measureMetrics = true;
    public float maxScanDistance = 5.0f;
    public string outputFolder = "Assets/Analysis_Output_CCP";
    public string fileNamePrefix = "CCP_Metrics_Report";

    // Internal State
    private Dictionary<int, ICCPAgent> activeAgents = new Dictionary<int, ICCPAgent>();
    private Dictionary<int, float> agentSpawnTimes = new Dictionary<int, float>();
    private Collider[] obstacleColliders;
    
    // Metrics Data
    private List<float> stepAvgAgentDists = new List<float>();
    private List<float> stepAvgObstacleDists = new List<float>();

    // Aggregation: Key = Agent Count
    private Dictionary<int, List<float>> agentDistByCount = new Dictionary<int, List<float>>();
    private Dictionary<int, List<float>> obsDistByCount = new Dictionary<int, List<float>>();

    // Aggregation: Key = Age (seconds)
    private Dictionary<int, List<float>> neighborDistByAge = new Dictionary<int, List<float>>();
    private Dictionary<int, List<float>> obstacleDistByAge = new Dictionary<int, List<float>>();

    // Aggregation: Key = Goal Distance (meters)
    private Dictionary<int, List<float>> neighborDistByGoalDist = new Dictionary<int, List<float>>();
    private Dictionary<int, List<float>> obstacleDistByGoalDist = new Dictionary<int, List<float>>();

    // --- Agent Abstraction ---
    private interface ICCPAgent
    {
        Transform transform { get; }
        GameObject gameObject { get; }
        void SetPdmMode(bool active);
        void SetGoal(Vector3 goal);
        Vector3 GetGoal();
    }

    private class GoalOnlyWrapper : ICCPAgent
    {
        private Agent_GoalOnly_Training agent;
        private Vector3 _goal;
        public GoalOnlyWrapper(Agent_GoalOnly_Training a) { agent = a; }
        public Transform transform => agent.transform;
        public GameObject gameObject => agent.gameObject;
        public void SetPdmMode(bool active) { agent.pdmMode = active; }
        public void SetGoal(Vector3 goal) { _goal = goal; agent.SetGoal(goal); }
        public Vector3 GetGoal() => _goal;
    }

    private class TrainingWrapper : ICCPAgent
    {
        private Agent_Training agent;
        private Vector3 _goal;
        public TrainingWrapper(Agent_Training a) { agent = a; }
        public Transform transform => agent.transform;
        public GameObject gameObject => agent.gameObject;
        public void SetPdmMode(bool active) { agent.pdmMode = active; }
        public void SetGoal(Vector3 goal) { _goal = goal; agent.SetGoal(goal); }
        public Vector3 GetGoal() => _goal;
    }

    public List<GameObject> GetActiveAgentGameObjects()
    {
        List<GameObject> agents = new List<GameObject>();
        foreach (var kvp in activeAgents)
        {
            if (kvp.Value != null && kvp.Value.gameObject != null)
            {
                agents.Add(kvp.Value.gameObject);
            }
        }
        return agents;
    }

    IEnumerator Start()
    {
        // 1. Dependencies Check
        if (simulator == null) simulator = FindObjectOfType<ZaraGroupRotationSimulator>();
        if (simulator == null)
        {
            Debug.LogError("[CCPMetricsEvaluator] Simulator not found.");
            yield break;
        }

        if (obstaclesParent == null)
        {
            GameObject obsObj = GameObject.Find("Obstacles");
            if (obsObj != null) obstaclesParent = obsObj.transform;
        }

        if (obstaclesParent != null)
        {
            obstacleColliders = obstaclesParent.GetComponentsInChildren<Collider>();
        }
        else
        {
            obstacleColliders = new Collider[0];
        }

        // 2. Initialize Simulator
        // Ensure simulator is playing
        simulator.Play(); 
        
        // 3. Start Evaluation Loop
        if (measureMetrics)
        {
            StartCoroutine(EvaluationLoop());
        }
    }

    // Update is called once per frame
    void Update()
    {
        CheckAndSpawnAgents();
    }

    void CheckAndSpawnAgents()
    {
        if (simulator == null) return;

        // Check for active GT agents that don't have a corresponding CCP agent yet
        foreach (Transform child in simulator.transform)
        {
            // Only interested in active "ped_X" objects
            if (!child.gameObject.activeSelf) continue;
            if (!child.name.StartsWith("ped_")) continue;

            int id;
            if (!int.TryParse(child.name.Replace("ped_", ""), out id)) continue;

            // If we haven't spawned a CCP agent for this ID yet
            if (!activeAgents.ContainsKey(id))
            {
                SpawnCCPAgent(id, child);
            }
            
            // Hide GT agent to avoid visual clutter/z-fighting, 
            // but keep it active so Simulator logic continues.
            // Note: If Simulator resets active state, this might flicker.
            // Renderer disabling is safer than SetActive(false) if Simulator checks activeSelf.
            var renderers = child.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers) r.enabled = false;
        }
    }

    void SpawnCCPAgent(int id, Transform gtTransform)
    {
        Vector3 startPos = gtTransform.position;
        Quaternion startRot = gtTransform.rotation;
        
        GameObject agentObj = Instantiate(agentPrefab, startPos, startRot);
        agentObj.name = $"CCP_Agent_{id}";

        ICCPAgent ccpAgent = null;

        var goalAgent = agentObj.GetComponent<Agent_GoalOnly_Training>();
        if (goalAgent != null)
        {
            ccpAgent = new GoalOnlyWrapper(goalAgent);
        }
        else
        {
            var trainAgent = agentObj.GetComponent<Agent_Training>();
            if (trainAgent != null)
            {
                ccpAgent = new TrainingWrapper(trainAgent);
            }
        }

        if (ccpAgent == null)
        {
            Debug.LogError($"[CCPMetricsEvaluator] Prefab missing Agent_GoalOnly_Training or Agent_Training: {agentObj.name}");
            Destroy(agentObj);
            return;
        }

        // Configure Agent
        ccpAgent.SetPdmMode(true); // Enable standalone mode (bypassing Manager)
        
        Vector3 finalGoal = simulator.GetFinalWorldPosition(id);
        ccpAgent.SetGoal(finalGoal);

        activeAgents.Add(id, ccpAgent);
        agentSpawnTimes.Add(id, Time.time);
        // Debug.Log($"[CCPMetricsEvaluator] Spawned Agent {id} at {startPos}");
    }

    IEnumerator EvaluationLoop()
    {
        // Initial wait
        yield return null;

        while (true)
        {
            // Wait for interval
            yield return new WaitForSeconds(sampleInterval);

            // Filter active agents and check timeouts
            List<int> agentsToRemove = new List<int>();
            List<int> validAgentIDs = new List<int>();

            foreach (var kvp in activeAgents)
            {
                int id = kvp.Key;
                ICCPAgent agent = kvp.Value;

                // 1. Check if null (destroyed externally)
                if (agent == null || agent.gameObject == null)
                {
                    agentsToRemove.Add(id);
                    continue;
                }

                // 2. Check if timed out
                if (agentSpawnTimes.ContainsKey(id))
                {
                    float age = Time.time - agentSpawnTimes[id];
                    if (age > agentTimeout)
                    {
                        // Timeout! Destroy and mark for removal
                        // Debug.Log($"[CCPMetricsEvaluator] Agent {id} timed out (Age: {age:F1}s). Removing.");
                        Destroy(agent.gameObject);
                        agentsToRemove.Add(id);
                        continue;
                    }
                }

                // 3. Check if active (might be disabled if finished goal)
                if (agent.gameObject.activeInHierarchy)
                {
                    validAgentIDs.Add(id);
                }
            }

            // Clean up removed agents
            foreach (int id in agentsToRemove)
            {
                activeAgents.Remove(id);
                agentSpawnTimes.Remove(id);
            }

            if (validAgentIDs.Count > 0)
            {
                RecordMetrics(validAgentIDs);
            }
        }
    }

    void RecordMetrics(List<int> agentIDs)
    {
        int count = agentIDs.Count;
        if (count == 0) return;

        // Pre-fetch agent objects to avoid repeated dictionary lookups
        List<ICCPAgent> agents = new List<ICCPAgent>(count);
        foreach (int id in agentIDs) agents.Add(activeAgents[id]);

        // 1. Neighbor Dist
        float sumNeighbor = 0f;
        int validNeighbor = 0;

        for (int i = 0; i < count; i++)
        {
            float minD = float.MaxValue;
            bool found = false;
            Vector3 p1 = agents[i].transform.position;
            int id = agentIDs[i];

            for (int j = 0; j < count; j++)
            {
                if (i == j) continue;
                float d = Vector3.Distance(p1, agents[j].transform.position);
                if (d < minD)
                {
                    minD = d;
                    found = true;
                }
            }

            if (found && minD <= maxScanDistance)
            {
                sumNeighbor += minD;
                validNeighbor++;

                // --- NEW: Per Agent Bucket Recording (Neighbor) ---
                // Age Bucket
                if (agentSpawnTimes.TryGetValue(id, out float spawnTime))
                {
                    int age = Mathf.FloorToInt(Time.time - spawnTime);
                    if (!neighborDistByAge.ContainsKey(age)) neighborDistByAge[age] = new List<float>();
                    neighborDistByAge[age].Add(minD);
                }
                
                // Goal Dist Bucket
                float distToGoal = Vector3.Distance(p1, agents[i].GetGoal());
                int distBucket = Mathf.FloorToInt(distToGoal);
                if (!neighborDistByGoalDist.ContainsKey(distBucket)) neighborDistByGoalDist[distBucket] = new List<float>();
                neighborDistByGoalDist[distBucket].Add(minD);
            }
        }

        float avgNeighbor = (validNeighbor > 0) ? sumNeighbor / validNeighbor : 0f;
        if (validNeighbor > 0)
        {
            stepAvgAgentDists.Add(avgNeighbor);
            
            if (!agentDistByCount.ContainsKey(count)) agentDistByCount[count] = new List<float>();
            agentDistByCount[count].Add(avgNeighbor);
        }

        // 2. Obstacle Dist
        float sumObs = 0f;
        int validObs = 0;

        if (obstacleColliders != null && obstacleColliders.Length > 0)
        {
            for (int i = 0; i < count; i++)
            {
                ICCPAgent agent = agents[i];
                int id = agentIDs[i];
                float minD = float.MaxValue;
                bool found = false;
                Vector3 p = agent.transform.position;

                foreach (var col in obstacleColliders)
                {
                    if (col == null) continue;
                    Vector3 cp = col.ClosestPoint(p);
                    float d = Vector3.Distance(p, cp);
                    if (d < minD)
                    {
                        minD = d;
                        found = true;
                    }
                }

                if (found && minD <= maxScanDistance)
                {
                    sumObs += minD;
                    validObs++;

                    // --- NEW: Per Agent Bucket Recording (Obstacle) ---
                    // Age Bucket
                    if (agentSpawnTimes.TryGetValue(id, out float spawnTime))
                    {
                        int age = Mathf.FloorToInt(Time.time - spawnTime);
                        if (!obstacleDistByAge.ContainsKey(age)) obstacleDistByAge[age] = new List<float>();
                        obstacleDistByAge[age].Add(minD);
                    }

                    // Goal Dist Bucket
                    float distToGoal = Vector3.Distance(p, agent.GetGoal());
                    int distBucket = Mathf.FloorToInt(distToGoal);
                    if (!obstacleDistByGoalDist.ContainsKey(distBucket)) obstacleDistByGoalDist[distBucket] = new List<float>();
                    obstacleDistByGoalDist[distBucket].Add(minD);
                }
            }
        }

        float avgObs = (validObs > 0) ? sumObs / validObs : 0f;
        if (validObs > 0)
        {
            stepAvgObstacleDists.Add(avgObs);

            if (!obsDistByCount.ContainsKey(count)) obsDistByCount[count] = new List<float>();
            obsDistByCount[count].Add(avgObs);
        }
    }

    void SaveReport()
    {
        if (stepAvgAgentDists.Count == 0 && stepAvgObstacleDists.Count == 0) return;

        float totalAvgAgent = (stepAvgAgentDists.Count > 0) ? stepAvgAgentDists.Average() : 0f;
        float totalAvgObs = (stepAvgObstacleDists.Count > 0) ? stepAvgObstacleDists.Average() : 0f;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        sb.AppendLine("============================================");
        sb.AppendLine("       CCP CROWD EVALUATION REPORT          ");
        sb.AppendLine("============================================");
        sb.AppendLine($" Date                 : {DateTime.Now}");
        sb.AppendLine($" Agent Prefab         : {(agentPrefab != null ? agentPrefab.name : "None")}");
        sb.AppendLine($" Max Scan Distance    : {maxScanDistance} m");
        sb.AppendLine($" Interval             : {sampleInterval} sec");
        sb.AppendLine($" Timeout              : {agentTimeout} sec");
        sb.AppendLine($" Total Steps Analyzed : {Mathf.Max(stepAvgAgentDists.Count, stepAvgObstacleDists.Count)}");
        sb.AppendLine($" ------------------------------------------");
        sb.AppendLine($" OVERALL AVG NEIGHBOR DIST : {totalAvgAgent:F4}");
        sb.AppendLine($" OVERALL AVG OBSTACLE DIST : {totalAvgObs:F4}");
        
        // 1. By Agent Count
        sb.AppendLine("============================================");
        sb.AppendLine("       METRICS BY ACTIVE AGENT COUNT        ");
        sb.AppendLine("============================================");
        var countKeys = new HashSet<int>(agentDistByCount.Keys);
        countKeys.UnionWith(obsDistByCount.Keys);
        foreach (int c in countKeys.OrderBy(k => k))
        {
            string aStr = agentDistByCount.ContainsKey(c) ? $"{agentDistByCount[c].Average():F4}m" : "N/A";
            string oStr = obsDistByCount.ContainsKey(c) ? $"{obsDistByCount[c].Average():F4}m" : "N/A";
            sb.AppendLine($" Agents: {c,3} | Neighbor: {aStr,8} | Obstacle: {oStr,8}");
        }

        // 2. By Age
        sb.AppendLine("============================================");
        sb.AppendLine("       METRICS BY AGENT AGE (SEC)           ");
        sb.AppendLine("============================================");
        var ageKeys = new HashSet<int>(neighborDistByAge.Keys);
        ageKeys.UnionWith(obstacleDistByAge.Keys);
        foreach (int age in ageKeys.OrderBy(k => k))
        {
            string aStr = neighborDistByAge.ContainsKey(age) ? $"{neighborDistByAge[age].Average():F4}m" : "N/A";
            string oStr = obstacleDistByAge.ContainsKey(age) ? $"{obstacleDistByAge[age].Average():F4}m" : "N/A";
            sb.AppendLine($" Age: {age,3}s | Neighbor: {aStr,8} | Obstacle: {oStr,8}");
        }

        // 3. By Goal Distance
        sb.AppendLine("============================================");
        sb.AppendLine("       METRICS BY GOAL DISTANCE (M)         ");
        sb.AppendLine("============================================");
        var distKeys = new HashSet<int>(neighborDistByGoalDist.Keys);
        distKeys.UnionWith(obstacleDistByGoalDist.Keys);
        foreach (int d in distKeys.OrderBy(k => k))
        {
            string aStr = neighborDistByGoalDist.ContainsKey(d) ? $"{neighborDistByGoalDist[d].Average():F4}m" : "N/A";
            string oStr = obstacleDistByGoalDist.ContainsKey(d) ? $"{obstacleDistByGoalDist[d].Average():F4}m" : "N/A";
            sb.AppendLine($" Dist: {d,3}m | Neighbor: {aStr,8} | Obstacle: {oStr,8}");
        }

        sb.AppendLine("============================================");

        string content = sb.ToString();
        Debug.Log(content);

        try
        {
            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(outputFolder, $"{fileNamePrefix}_{timestamp}.txt");
            File.WriteAllText(path, content);
            Debug.Log($"[CCPMetricsEvaluator] Report saved: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to save report: {ex.Message}");
        }
    }

    void OnApplicationQuit()
    {
        if (measureMetrics)
        {
            SaveReport();
        }
    }
}