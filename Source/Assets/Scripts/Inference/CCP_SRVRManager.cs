using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using UnityEngine.SceneManagement;

[Serializable]
public class CCP_SRVRMetricsReport
{
    public string sceneName;
    public string date;
    public string time;
    public int totalAgents;
    public int successCount;
    public float goalSuccessRate;
    public float averageSafeRate;
    public float avgDangerZoneViolationRate;
    public float avgStaticObstacleCollisionRate;
    public float avgVehicleCollisionRate;
    public float avgDoorCollisionRate;
    public float avgAgentCollisionRate;
    public float avgTravelTime;
    public float stdDevTravelTime;
    public int totalGroupsDetected;
    public float avgInterGroupDistance;
}

public class CCP_SRVRManager : MonoBehaviour
{
    public static CCP_SRVRManager Instance { get; private set; }

    [Header("Agent Settings")]
    public float searchInterval = 0.5f;
    public float agentRadius = 0.4f;

    [Header("Environment Settings")]
    public Transform dangerZonesRoot;
    public Transform obstaclesRoot;

    [Header("Logging Settings")]
    public string saveSubFolder = "MetricsLogs";
    public bool verbose = true;
    public bool logHitTags = false;
    private bool _hasExported = false;

    [Header("Inference Options")]
    public bool stopOnCollision = false;

    [Header("Trajectory Map Settings")]
    public bool enableTrajectoryMap = true;
    public bool drawOnlyTail = false;
    public float trajectoryTailSeconds = 5.0f;
    public int mapResolution = 2048;
    public float mapWidth = 100f;
    public float mapHeight = 100f;
    public Color normalPathColor = Color.red;
    public Color dangerPathColor = Color.yellow;
    public Color dangerZoneOutlineColor = Color.blue;
    public Color obstacleOutlineColor = new Color(1f, 0.5f, 0f); 
    public string mapFileName = "SocialTrajectoryMap_CCP.png";
    public float minRecordDistance = 0.1f;
    public int endPointRadius = 10;

    public struct TrajectoryPoint
    {
        public Vector3 position;
        public bool isDanger;
        public float timestamp;
        public TrajectoryPoint(Vector3 pos, bool danger, float time)
        {
            position = pos;
            isDanger = danger;
            timestamp = time;
        }
    }

    public class AgentTrackingData
    {
        public float totalTime;
        public float dangerZoneTime;
        public float staticObstacleTime;
        public float vehicleCollisionTime;
        public float doorCollisionTime;
        public float agentCollisionTime;

        public Collider agentCollider;
        public GameObject agentObject;
        public bool hasReachedGoal;
        public bool isFinished;

        public List<TrajectoryPoint> trajectory = new List<TrajectoryPoint>();
        public float groupDistanceSum;
        public int groupDistanceSamples;
    }

    private Dictionary<Transform, AgentTrackingData> trackingData = new Dictionary<Transform, AgentTrackingData>();
    private HashSet<Collider> dangerZoneColliders = new HashSet<Collider>();
    private HashSet<Collider> obstacleColliders = new HashSet<Collider>();
    private float searchTimer = 0f;

    [Header("Detection Tags")]
    public string agentTag = "AgentCollider";
    public string obstacleTag = "Obstacle";
    public string buildingTag = "Building";
    public string vehicleTag = "Vehicle";
    public string doorTag = "Door";

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        if (dangerZonesRoot == null) dangerZonesRoot = GameObject.Find("Danger Zones")?.transform;
        if (dangerZonesRoot != null) dangerZoneColliders = new HashSet<Collider>(dangerZonesRoot.GetComponentsInChildren<Collider>());
        if (obstaclesRoot == null) obstaclesRoot = GameObject.Find("Obstacles")?.transform;
        if (obstaclesRoot != null) obstacleColliders = new HashSet<Collider>(obstaclesRoot.GetComponentsInChildren<Collider>());
    }

    void Update()
    {
        searchTimer += Time.unscaledDeltaTime;
        if (searchTimer >= searchInterval) { SearchForAgents(); searchTimer = 0f; }
        Physics.SyncTransforms();
        UpdateAgentStats();
    }

    private void SearchForAgents()
    {
        var agentsT = FindObjectsOfType<Agent_Training>(true);
        var agentsGO = FindObjectsOfType<Agent_GoalOnly_Training>(true);

        List<GameObject> allAgents = new List<GameObject>();
        foreach (var a in agentsT) allAgents.Add(a.gameObject);
        foreach (var a in agentsGO) allAgents.Add(a.gameObject);

        foreach (var go in allAgents)
        {
            Transform t = go.transform;
            if (trackingData.ContainsKey(t)) continue;

            Collider col = t.GetComponentInChildren<Collider>();
            if (col == null)
            {
                SphereCollider sc = go.AddComponent<SphereCollider>();
                sc.radius = agentRadius;
                sc.isTrigger = true;
                col = sc;
            }

            AgentTrackingData newData = new AgentTrackingData { agentObject = go, agentCollider = col };
            trackingData.Add(t, newData);
        }
    }

    private void UpdateAgentStats()
    {
        float dt = Time.deltaTime;
        if (dt <= 0) return;

        foreach (var kvp in trackingData)
        {
            Transform agentTransform = kvp.Key;
            AgentTrackingData data = kvp.Value;
            if (agentTransform == null || data.isFinished) continue;

            bool reached = false;
            var atComp = data.agentObject.GetComponent<Agent_Training>();
            var agoComp = data.agentObject.GetComponent<Agent_GoalOnly_Training>();
            if (atComp != null) reached = atComp.reachedGoal;
            else if (agoComp != null) reached = agoComp.reachedGoal;

            if (reached) { data.hasReachedGoal = true; data.isFinished = true; continue; }
            if (!agentTransform.gameObject.activeInHierarchy) continue;

            data.totalTime += dt;
            Vector3 agentColPos = data.agentCollider.transform.position;

            bool hitDanger = false;
            foreach (var col in dangerZoneColliders)
                if (col != null && col.enabled && IsOverlapping(data.agentCollider, col)) { hitDanger = true; break; }
            if (hitDanger) data.dangerZoneTime += dt;

            bool hitOther = false;
            Collider[] nearby = Physics.OverlapSphere(agentColPos, agentRadius + 0.5f);
            foreach (var col in nearby)
            {
                if (col == null || col == data.agentCollider || col.transform.IsChildOf(agentTransform)) continue;
                if (IsOverlapping(data.agentCollider, col))
                {
                    string tag = col.tag;
                    bool isAgent = tag == agentTag || tag == "Agent" || col.GetComponentInParent<Agent_Training>() != null || col.GetComponentInParent<Agent_GoalOnly_Training>() != null;
                    bool isVehicle = tag == vehicleTag;
                    bool isDoor = tag == doorTag;
                    bool isObstacle = tag == obstacleTag || tag == buildingTag || obstacleColliders.Contains(col);

                    if (isAgent) { data.agentCollisionTime += dt; hitOther = true; }
                    else if (isVehicle) { data.vehicleCollisionTime += dt; hitOther = true; }
                    else if (isDoor) { data.doorCollisionTime += dt; hitOther = true; }
                    else if (isObstacle) { data.staticObstacleTime += dt; hitOther = true; }
                }
            }

            if (stopOnCollision && hitOther)
            {
                data.isFinished = true;
                data.hasReachedGoal = false;
                if (data.agentObject != null)
                {
                    data.agentObject.SetActive(false);
                }
                if (verbose) Debug.Log($"[CCP_SRVRManager] Agent {agentTransform.name} stopped due to collision.");
                continue;
            }

            List<Transform> members = null;
            if (atComp != null) members = atComp.groupMembers;
            else if (agoComp != null) members = agoComp.groupMembers;

            if (members != null && members.Count > 0)
            {
                float distSum = 0f; int count = 0;
                foreach (var member in members)
                {
                    if (member != null && member.gameObject.activeInHierarchy)
                    {
                        distSum += Vector3.Distance(agentTransform.position, member.position);
                        count++;
                    }
                }
                if (count > 0) { data.groupDistanceSum += (distSum / count); data.groupDistanceSamples++; }
            }

            if (enableTrajectoryMap)
            {
                Vector3 currentPos = agentTransform.position;
                if (data.trajectory.Count == 0 || Vector3.Distance(data.trajectory.Last().position, currentPos) >= minRecordDistance)
                    data.trajectory.Add(new TrajectoryPoint(currentPos, hitDanger, Time.time));
            }
        }
    }

    private bool IsOverlapping(Collider agentCol, Collider otherCol)
    {
        if (agentCol == null || otherCol == null) return false;
        if (!agentCol.bounds.Intersects(otherCol.bounds)) return false;
        Vector3 agentPos = agentCol.transform.position;
        Quaternion agentRot = agentCol.transform.rotation;
        if (otherCol is MeshCollider && !((MeshCollider)otherCol).convex)
        {
            Vector3 closestPoint = otherCol.ClosestPoint(agentPos);
            return Vector3.Distance(agentPos, closestPoint) <= agentRadius;
        }
        Vector3 dir; float distPen;
        return Physics.ComputePenetration(agentCol, agentPos, agentRot, otherCol, otherCol.transform.position, otherCol.transform.rotation, out dir, out distPen);
    }

    public float CalculateSafeRate(AgentTrackingData data)
    {
        if (data.totalTime <= 0.0001f) return 1f;
        float unsafeTime = data.dangerZoneTime + data.staticObstacleTime + data.vehicleCollisionTime + data.doorCollisionTime + data.agentCollisionTime;
        return Mathf.Clamp01(1.0f - (unsafeTime / data.totalTime));
    }

    private void OnApplicationQuit() { ExportResults(); if (enableTrajectoryMap) GenerateTrajectoryMap(); }

    private void ExportResults()
    {
        if (trackingData.Count == 0 || _hasExported) return;
        _hasExported = true;
        int totalAgents = trackingData.Count;
        int successCount = 0;
        List<float> successSafeRates = new List<float>();
        List<float> dangerZoneRates = new List<float>();
        List<float> staticObstacleRates = new List<float>();
        List<float> vehicleCollisionRates = new List<float>();
        List<float> doorCollisionRates = new List<float>();
        List<float> agentCollisionRates = new List<float>();

        foreach (var data in trackingData.Values)
        {
            if (data.hasReachedGoal)
            {
                successCount++;
                if (data.totalTime > 0.0001f)
                {
                    successSafeRates.Add(CalculateSafeRate(data));
                    dangerZoneRates.Add(data.dangerZoneTime / data.totalTime);
                    staticObstacleRates.Add(data.staticObstacleTime / data.totalTime);
                    vehicleCollisionRates.Add(data.vehicleCollisionTime / data.totalTime);
                    doorCollisionRates.Add(data.doorCollisionTime / data.totalTime);
                    agentCollisionRates.Add(data.agentCollisionTime / data.totalTime);
                }
            }
        }

        float avgTime = 0f, stdDevTime = 0f;
        var successAgents = trackingData.Values.Where(d => d.hasReachedGoal).ToList();
        if (successAgents.Count > 0)
        {
            avgTime = successAgents.Average(d => d.totalTime);
            double sumSqDiff = successAgents.Sum(d => Mathf.Pow(d.totalTime - avgTime, 2));
            stdDevTime = (float)((successAgents.Count > 1) ? Math.Sqrt(sumSqDiff / (successAgents.Count - 1)) : 0.0);
        }

        int groupCount = 0; HashSet<Transform> visited = new HashSet<Transform>(); List<float> allGroupAvgDistances = new List<float>();
        foreach (var kvp in trackingData)
        {
            Transform t = kvp.Key; AgentTrackingData d = kvp.Value;
            if (d.groupDistanceSamples > 0) allGroupAvgDistances.Add(d.groupDistanceSum / d.groupDistanceSamples);

            if (d.agentObject == null) continue;
            var at = d.agentObject.GetComponent<Agent_Training>();
            var ago = d.agentObject.GetComponent<Agent_GoalOnly_Training>();
            List<Transform> members = (at != null) ? at.groupMembers : (ago != null ? ago.groupMembers : null);

            if (members != null && members.Count > 0 && !visited.Contains(t))
            {
                groupCount++; visited.Add(t);
                foreach (var m in members) if (m != null) visited.Add(m);
            }
        }

        CCP_SRVRMetricsReport report = new CCP_SRVRMetricsReport
        {
            sceneName = SceneManager.GetActiveScene().name,
            date = DateTime.Now.ToString("yyyy-MM-dd"),
            time = DateTime.Now.ToString("HH-mm-ss"),
            totalAgents = totalAgents,
            successCount = successCount,
            goalSuccessRate = totalAgents > 0 ? (float)successCount / totalAgents : 0,
            averageSafeRate = successSafeRates.Count > 0 ? successSafeRates.Average() : 0,
            avgDangerZoneViolationRate = dangerZoneRates.Count > 0 ? dangerZoneRates.Average() : 0,
            avgStaticObstacleCollisionRate = staticObstacleRates.Count > 0 ? staticObstacleRates.Average() : 0,
            avgVehicleCollisionRate = vehicleCollisionRates.Count > 0 ? vehicleCollisionRates.Average() : 0,
            avgDoorCollisionRate = doorCollisionRates.Count > 0 ? doorCollisionRates.Average() : 0,
            avgAgentCollisionRate = agentCollisionRates.Count > 0 ? agentCollisionRates.Average() : 0,
            avgTravelTime = avgTime,
            stdDevTravelTime = stdDevTime,
            totalGroupsDetected = groupCount,
            avgInterGroupDistance = allGroupAvgDistances.Count > 0 ? allGroupAvgDistances.Average() : 0
        };

        string path = Path.Combine(Application.dataPath, "..", saveSubFolder);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"{report.sceneName}_{report.date}_{report.time}_SRVRMetrics.json"), JsonUtility.ToJson(report, true));
    }

    private void GenerateTrajectoryMap()
    {
        Texture2D tex = new Texture2D(mapResolution, mapResolution);
        for (int i = 0; i < mapResolution * mapResolution; i++) tex.SetPixel(i % mapResolution, i / mapResolution, Color.white);
        float minX = transform.position.x - mapWidth / 2f, minZ = transform.position.z - mapHeight / 2f;
        float tail = (drawOnlyTail && trajectoryTailSeconds > 0) ? Time.time - trajectoryTailSeconds : -1;
        foreach (var col in dangerZoneColliders) if (col != null && col.enabled) DrawCollider(tex, col, dangerZoneOutlineColor, minX, minZ);
        foreach (var col in obstacleColliders) if (col != null && col.enabled) DrawCollider(tex, col, obstacleOutlineColor, minX, minZ);
        foreach (var data in trackingData.Values)
        {
            var path = data.trajectory; if (path.Count < 2) continue;
            int start = 0; if (tail > 0) for (int i = 0; i < path.Count; i++) if (path[i].timestamp >= tail) { start = i; break; }
            if (start >= path.Count - 1) continue;
            Vector2 prev = WorldToPixel(path[start].position, minX, minZ);
            for (int i = start + 1; i < path.Count; i++)
            {
                Vector2 curr = WorldToPixel(path[i].position, minX, minZ);
                DrawLine(tex, prev, curr, path[i].isDanger ? dangerPathColor : normalPathColor, 1);
                prev = curr;
            }
            DrawCircle(tex, prev, endPointRadius, normalPathColor);
        }
        tex.Apply();
        File.WriteAllBytes(Path.Combine(Application.dataPath, mapFileName), tex.EncodeToPNG());
    }

    private void DrawCollider(Texture2D tex, Collider col, Color color, float minX, float minZ)
    {
        Bounds b = col.bounds;
        Vector2 px1 = WorldToPixel(new Vector3(b.min.x, 0, b.min.z), minX, minZ);
        Vector2 px2 = WorldToPixel(new Vector3(b.max.x, 0, b.min.z), minX, minZ);
        Vector2 px3 = WorldToPixel(new Vector3(b.max.x, 0, b.max.z), minX, minZ);
        Vector2 px4 = WorldToPixel(new Vector3(b.min.x, 0, b.max.z), minX, minZ);
        DrawLine(tex, px1, px2, color, 2); DrawLine(tex, px2, px3, color, 2); DrawLine(tex, px3, px4, color, 2); DrawLine(tex, px4, px1, color, 2);
    }

    private Vector2 WorldToPixel(Vector3 pos, float minX, float minZ) => new Vector2((pos.x - minX) / mapWidth * (mapResolution - 1), (pos.z - minZ) / mapHeight * (mapResolution - 1));

    private void DrawLine(Texture2D tex, Vector2 p1, Vector2 p2, Color col, int thickness)
    {
        int x0 = (int)p1.x, y0 = (int)p1.y, x1 = (int)p2.x, y1 = (int)p2.y;
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx - dy;
        while (true)
        {
            for (int i = -thickness / 2; i <= thickness / 2; i++)
                for (int j = -thickness / 2; j <= thickness / 2; j++)
                {
                    int px = x0 + i, py = y0 + j;
                    if (px >= 0 && px < tex.width && py >= 0 && py < tex.height) tex.SetPixel(px, py, col);
                }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private void DrawCircle(Texture2D tex, Vector2 center, int radius, Color col)
    {
        int cx = (int)center.x, cy = (int)center.y;
        for (int x = -radius; x <= radius; x++)
            for (int y = -radius; y <= radius; y++)
                if (x * x + y * y <= radius * radius)
                {
                    int px = cx + x, py = cy + y;
                    if (px >= 0 && px < tex.width && py >= 0 && py < tex.height) tex.SetPixel(px, py, col);
                }
    }
}
