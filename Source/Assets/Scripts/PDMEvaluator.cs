using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;

public class PDMEvaluator : MonoBehaviour
{
    [Header("Setup")]
    public GameObject agentPrefab; // Prefab with Agent_GoalOnly_Training
    public ZaraGroupRotationSimulator simulator;
    public float resetInterval = 0.8f;
    
    [Header("Map Generation")]
    public bool generateMap = true;
    public string mapFileName = "PDMTrajectoryMap.png";
    public int mapResolution = 2048;
    public float mapSize = 100f;
    public Color gtColor = Color.blue;
    public Color agentColor = Color.red;
    public Color obstacleColor = new Color(1f, 0.5f, 0f); // Orange

    // Obstacles
    private Transform obstaclesRoot;
    private List<BoxCollider> obstacleColliders = new List<BoxCollider>();

    // --- Abstraction for Agents ---
    private interface IPDMAgent
    {
        void SetPdmMode(bool active);
        void SetGoal(Vector3 goal);
        void ForceReset(Vector3 pos, Quaternion rot, Vector3 vel);
    }

    private class GoalOnlyWrapper : IPDMAgent
    {
        private Agent_GoalOnly_Training agent;
        public GoalOnlyWrapper(Agent_GoalOnly_Training a) { agent = a; }
        public void SetPdmMode(bool active) { agent.pdmMode = active; }
        public void SetGoal(Vector3 goal) { agent.SetGoal(goal); }
        public void ForceReset(Vector3 pos, Quaternion rot, Vector3 vel) { agent.ForceReset(pos, rot, vel); }
    }

    private class TrainingWrapper : IPDMAgent
    {
        private Agent_Training agent;
        public TrainingWrapper(Agent_Training a) { agent = a; }
        public void SetPdmMode(bool active) { agent.pdmMode = active; }
        public void SetGoal(Vector3 goal) { agent.SetGoal(goal); }
        public void ForceReset(Vector3 pos, Quaternion rot, Vector3 vel) { agent.ForceReset(pos, rot, vel); }
    }

    // Internal tracking
    private class AgentPair
    {
        public Transform gtTransform;
        public IPDMAgent agent;
        public GameObject agentObj;
        public List<Vector3> gtPath = new List<Vector3>();
        public List<Vector3> agentPath = new List<Vector3>();
        public Vector3 prevGtPos; 
        
        // Per-interval tracking
        public float currentIntervalDiffSum;
        public int currentIntervalFrameCount;
    }

    private Dictionary<int, AgentPair> pairs = new Dictionary<int, AgentPair>();
    private float resetTimer = 0f;
    private float agentCheckTimer = 0f;
    public float agentCheckInterval = 0.5f;
    
    // Metrics
    private List<float> adeList = new List<float>(); // Average Displacement Error per interval
    private List<float> fdeList = new List<float>(); // Final Displacement Error per interval

    IEnumerator Start()
    {
        // Find Obstacles
        GameObject obsObj = GameObject.Find("Obstacles");
        if (obsObj != null)
        {
            obstaclesRoot = obsObj.transform;
            obstacleColliders = obstaclesRoot.GetComponentsInChildren<BoxCollider>().ToList();
            Debug.Log($"[PDMEvaluator] Found {obstacleColliders.Count} obstacle colliders.");
        }

        if (simulator == null) simulator = FindObjectOfType<ZaraGroupRotationSimulator>();
        
        // Wait a frame to ensure Simulator has spawned its agents
        yield return null; 
        
        ScanForNewAgents();
        
        Debug.Log($"[PDMEvaluator] Created {pairs.Count} pairs. Starting evaluation.");
    }

    void ScanForNewAgents()
    {
        // Find existing agents created by ZaraGroupRotationSimulator or others
        // Optimization: We could limit this scan frequency if needed
        GameObject[] allObjects = FindObjectsOfType<GameObject>();
        foreach (var go in allObjects)
        {
            if (go.name.StartsWith("ped_"))
            {
                if (int.TryParse(go.name.Substring(4), out int id))
                {
                    if (!pairs.ContainsKey(id))
                    {
                        CreatePair(id, go.transform);
                    }
                }
            }
        }
    }

    void CreatePair(int id, Transform gtTransform)
    {
        // Spawn Agent at GT position
        GameObject agentObj = Instantiate(agentPrefab, gtTransform.position, gtTransform.rotation);
        agentObj.name = $"CCP_Agent_{id}";
        
        IPDMAgent pdmAgent = null;

        var goalAgent = agentObj.GetComponent<Agent_GoalOnly_Training>();
        if (goalAgent != null)
        {
            pdmAgent = new GoalOnlyWrapper(goalAgent);
        }
        else
        {
            var trainAgent = agentObj.GetComponent<Agent_Training>();
            if (trainAgent != null)
            {
                pdmAgent = new TrainingWrapper(trainAgent);
            }
        }

        if (pdmAgent == null)
        {
            Debug.LogError("Agent Prefab missing Agent_GoalOnly_Training or Agent_Training component!");
            return;
        }
        
        // --- 1. Set PDM Mode and Goal ---
        pdmAgent.SetPdmMode(true);
        
        // Get Goal from Simulator
        Vector3 finalGoal = simulator.GetFinalWorldPosition(id);
        pdmAgent.SetGoal(finalGoal);

        // --- 2. Disable Manager Dependency if possible ---
        // Since we modified Agent_GoalOnly_Training to skip manager logic if pdmMode is true,
        // this should be safe. 
        
        AgentPair pair = new AgentPair
        {
            gtTransform = gtTransform,
            agent = pdmAgent,
            agentObj = agentObj,
            prevGtPos = gtTransform.position // Initialize
        };
        
        pairs.Add(id, pair);
    }

    void FixedUpdate()
    {
        agentCheckTimer += Time.fixedDeltaTime;
        if (agentCheckTimer >= agentCheckInterval)
        {
            ScanForNewAgents();
            agentCheckTimer = 0f;
        }

        if (pairs.Count == 0) return;

        resetTimer += Time.fixedDeltaTime;
        bool isResetFrame = false;

        if (resetTimer >= resetInterval)
        {
            isResetFrame = true;
            resetTimer = 0f;
        }

        foreach (var pair in pairs.Values)
        {
            if (pair.gtTransform == null) continue;

            // Update active state based on GT agent
            bool gtActive = pair.gtTransform.gameObject.activeInHierarchy;
            Vector3 currentGtPos = pair.gtTransform.position;

            // Calculate GT Velocity
            // Velocity = (CurrentPos - PrevPos) / DeltaTime
            Vector3 gtVelocity = (currentGtPos - pair.prevGtPos) / Time.fixedDeltaTime;

            if (pair.agentObj.activeSelf != gtActive)
            {
                // If it's becoming inactive, record the final position one last time
                if (!gtActive && generateMap)
                {
                    RecordPath(pair.gtPath, currentGtPos);
                    if (pair.agentObj != null)
                        RecordPath(pair.agentPath, pair.agentObj.transform.position);
                }

                // Capture velocity BEFORE activating/deactivating if needed, but if inactive it's 0.
                // If activating, we want the velocity it had (which is 0 or preserved).
                Vector3 preservedVel = Vector3.zero;
                Rigidbody rb = pair.agentObj.GetComponent<Rigidbody>();
                if (rb != null) preservedVel = rb.velocity;

                pair.agentObj.SetActive(gtActive);
                
                // When reactivating, sync position immediately
                if (gtActive)
                {
                    // Use the agent's own velocity (likely 0 if it was inactive, or preserved)
                    pair.agent.ForceReset(currentGtPos, pair.gtTransform.rotation, preservedVel);
                    // Reset metrics for this agent on reactivation
                    pair.currentIntervalDiffSum = 0f;
                    pair.currentIntervalFrameCount = 0;
                }
            }
            
            // Update PrevPos for next frame
            pair.prevGtPos = currentGtPos;

            if (!gtActive) continue;

            // Use XZ plane for distance
            Vector3 gtPos = currentGtPos;
            Vector3 agentPos = pair.agentObj.transform.position;
            float dist = Vector3.Distance(new Vector3(gtPos.x, 0, gtPos.z), new Vector3(agentPos.x, 0, agentPos.z));

            // Accumulate for current interval
            pair.currentIntervalDiffSum += dist;
            pair.currentIntervalFrameCount++;

            // 1. Reset Logic (Progressive Reset) & Metrics Calculation
            if (isResetFrame)
            {
                // --- Metrics Calculation ---
                // FDE: The distance at this exact reset frame (final frame of interval)
                fdeList.Add(dist);

                // ADE: Average distance over this specific interval
                if (pair.currentIntervalFrameCount > 0)
                {
                    float avgIntervalDiff = pair.currentIntervalDiffSum / pair.currentIntervalFrameCount;
                    adeList.Add(avgIntervalDiff);
                }

                // Reset accumulators
                pair.currentIntervalDiffSum = 0f;
                pair.currentIntervalFrameCount = 0;

                // --- Reset Agent ---
                // Force sync position but KEEP agent's current velocity
                Vector3 currentAgentVel = Vector3.zero;
                Rigidbody rb = pair.agentObj.GetComponent<Rigidbody>();
                if (rb != null) currentAgentVel = rb.velocity;

                pair.agent.ForceReset(currentGtPos, pair.gtTransform.rotation, currentAgentVel);
                
                // Mark a break in the agent's path for visualization
                if (generateMap)
                {
                    pair.agentPath.Add(Vector3.negativeInfinity);
                }
            }
            // If NOT reset frame, just record path
            else if (generateMap)
            {
                RecordPath(pair.gtPath, gtPos);
                RecordPath(pair.agentPath, agentPos);
            }
        }
    }

    void RecordPath(List<Vector3> path, Vector3 currentPos)
    {
        Vector3 flatPos = new Vector3(currentPos.x, 0, currentPos.z);
        
        if (path.Count == 0)
        {
            path.Add(flatPos);
            return;
        }

        Vector3 lastPoint = path[path.Count - 1];
        
        if (float.IsNegativeInfinity(lastPoint.x))
        {
            path.Add(flatPos);
            return;
        }

        if (Vector3.Distance(lastPoint, flatPos) > 0.05f) // Slightly tighter threshold
        {
            path.Add(flatPos);
        }
    }

    void OnApplicationQuit()
    {
        if (adeList.Count > 0)
        {
            float avgAde = adeList.Average();
            float stdAde = CalculateStdDev(adeList, avgAde);
            float ci95Ade = 1.96f * (stdAde / Mathf.Sqrt(adeList.Count));
            Debug.Log($"[PDM Report] ADE (Average Displacement Error): {avgAde:F4} ± {ci95Ade:F4} meters (95% CI, over {adeList.Count} intervals)");
        }
        
        if (fdeList.Count > 0)
        {
            float avgFde = fdeList.Average();
            float stdFde = CalculateStdDev(fdeList, avgFde);
            float ci95Fde = 1.96f * (stdFde / Mathf.Sqrt(fdeList.Count));
            Debug.Log($"[PDM Report] FDE (Final Displacement Error): {avgFde:F4} ± {ci95Fde:F4} meters (95% CI, over {fdeList.Count} intervals)");
        }

        if (generateMap)
        {
            GenerateMap();
        }
    }

    float CalculateStdDev(List<float> values, float mean)
    {
        if (values.Count <= 1) return 0;
        double sum = values.Sum(v => Math.Pow(v - mean, 2));
        return (float)Math.Sqrt(sum / values.Count);
    }

    void GenerateMap()
    {
        Texture2D texture = new Texture2D(mapResolution, mapResolution);
        Color[] resetColor = new Color[mapResolution * mapResolution];
        for (int i = 0; i < resetColor.Length; i++) resetColor[i] = Color.white;
        texture.SetPixels(resetColor);

        // Use the Evaluator's position as the center
        float minX = transform.position.x - mapSize / 2f;
        float minZ = transform.position.z - mapSize / 2f;

        // Draw Obstacles
        DrawColliders(texture, obstacleColliders, obstacleColor, minX, minZ);

        foreach (var pair in pairs.Values)
        {
            DrawPath(texture, pair.gtPath, gtColor, minX, minZ);
            DrawPath(texture, pair.agentPath, agentColor, minX, minZ);

            // Draw End Points (Blue for GT, Red for Agent)
            DrawEndPoint(texture, pair.gtPath, gtColor, minX, minZ);
            DrawEndPoint(texture, pair.agentPath, agentColor, minX, minZ);
        }

        texture.Apply();
        byte[] bytes = texture.EncodeToPNG();
        string path = Path.Combine(Application.dataPath, mapFileName);
        File.WriteAllBytes(path, bytes);
        Debug.Log($"PDM Map saved to {path} (Center: {transform.position.x}, {transform.position.z})");
    }

    void DrawColliders(Texture2D texture, List<BoxCollider> colliders, Color color, float minX, float minZ)
    {
        foreach (var col in colliders)
        {
            if (col == null) continue;

            // Get corners in world space (XZ plane approximation)
            Transform t = col.transform;
            Vector3 center = col.center;
            Vector3 size = col.size;

            // Local corners (bottom face)
            Vector3 p1 = t.TransformPoint(center + new Vector3(-size.x, -size.y, -size.z) * 0.5f);
            Vector3 p2 = t.TransformPoint(center + new Vector3(size.x, -size.y, -size.z) * 0.5f);
            Vector3 p3 = t.TransformPoint(center + new Vector3(size.x, -size.y, size.z) * 0.5f);
            Vector3 p4 = t.TransformPoint(center + new Vector3(-size.x, -size.y, size.z) * 0.5f);

            // Convert to pixels
            Vector2 px1 = WorldToPixel(p1, minX, minZ);
            Vector2 px2 = WorldToPixel(p2, minX, minZ);
            Vector2 px3 = WorldToPixel(p3, minX, minZ);
            Vector2 px4 = WorldToPixel(p4, minX, minZ);

            DrawLine(texture, px1, px2, color);
            DrawLine(texture, px2, px3, color);
            DrawLine(texture, px3, px4, color);
            DrawLine(texture, px4, px1, color);
        }
    }

    void DrawEndPoint(Texture2D tex, List<Vector3> path, Color col, float minX, float minZ)
    {
        if (path == null || path.Count == 0) return;
        
        // Find last valid point
        Vector3 last = Vector3.negativeInfinity;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (!float.IsNegativeInfinity(path[i].x))
            {
                last = path[i];
                break;
            }
        }

        if (!float.IsNegativeInfinity(last.x))
        {
            Vector2 pixel = WorldToPixel(last, minX, minZ);
            DrawCircle(tex, (int)pixel.x, (int)pixel.y, 5, col); // Radius 5
        }
    }

    void DrawPath(Texture2D tex, List<Vector3> path, Color col, float minX, float minZ)
    {
        if (path.Count < 2) return;
        
        Vector2 prev = Vector2.zero;
        bool hasPrev = false;

        for(int i=0; i<path.Count; i++)
        {
            Vector3 pt = path[i];
            
            // Check for break
            if (float.IsNegativeInfinity(pt.x))
            {
                hasPrev = false;
                continue;
            }

            Vector2 cur = WorldToPixel(pt, minX, minZ);

            if (hasPrev)
            {
                DrawLine(tex, prev, cur, col);
            }

            prev = cur;
            hasPrev = true;
        }
    }

    Vector2 WorldToPixel(Vector3 pos, float minX, float minZ)
    {
        float u = (pos.x - minX) / mapSize;
        float v = (pos.z - minZ) / mapSize;
        return new Vector2(u * (mapResolution - 1), v * (mapResolution - 1));
    }

    void DrawLine(Texture2D tex, Vector2 p1, Vector2 p2, Color col)
    {
        int x0 = (int)p1.x; int y0 = (int)p1.y;
        int x1 = (int)p2.x; int y1 = (int)p2.y;
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while(true)
        {
            DrawBrush(tex, x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    void DrawBrush(Texture2D tex, int x, int y, Color col)
    {
        int thickness = 3; // Line thickness
        int half = thickness / 2;
        for (int i = -half; i <= half; i++)
        {
            for (int j = -half; j <= half; j++)
            {
                if (x + i >= 0 && x + i < tex.width && y + j >= 0 && y + j < tex.height)
                {
                    tex.SetPixel(x + i, y + j, col);
                }
            }
        }
    }

    void DrawCircle(Texture2D tex, int cx, int cy, int r, Color col)
    {
        for (int x = -r; x <= r; x++)
        {
            for (int y = -r; y <= r; y++)
            {
                if (x*x + y*y <= r*r)
                {
                    int px = cx + x;
                    int py = cy + y;
                    if (px >= 0 && px < tex.width && py >= 0 && py < tex.height)
                        tex.SetPixel(px, py, col);
                }
            }
        }
    }
}