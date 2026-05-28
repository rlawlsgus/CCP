using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;

public class SocialTaskCompletionRate : MonoBehaviour
{
    [Header("Agent Settings")]
    [Tooltip("Parent object containing all agents. Will auto-find 'Agents' if null.")]
    public Transform agentsRoot;
    [Tooltip("Interval in seconds to search for new agents.")]
    public float searchInterval = 0.5f;

    [Header("Environment Settings")]
    [Tooltip("The parent object containing all Danger Zone cubes. Will auto-find 'Danger Zones' if null.")]
    public Transform dangerZonesRoot;
    [Tooltip("The parent object containing all Obstacle cubes. Will auto-find 'Obstacles' if null.")]
    public Transform obstaclesRoot;

    [Header("Social Context Metrics")]
    [Tooltip("Measure nearest-agent distance and TTC(Time-To-Collision).")]
    public bool enableSocialContextMetrics = true;
    [Tooltip("Calculate distance/TTC on the XZ plane by ignoring the Y axis.")]
    public bool useXZPlaneForSocialMetrics = true;
    [Tooltip("Subtract approximate collider radii from nearest-agent distance. False means center-to-center distance.")]
    public bool subtractApproxAgentRadiusFromNearestDistance = false;
    [Tooltip("Collision radius used for TTC. If <= 0, the sum of both agents' approximate collider radii is used.")]
    public float ttcCollisionRadius = 0f;
    [Tooltip("Threshold used for Low-TTC Rate.")]
    public float lowTTCThreshold = 2.0f;
    [Tooltip("Minimum relative speed required to compute TTC. Smaller speeds are treated as no finite TTC.")]
    public float minRelativeSpeedForTTC = 0.05f;
    [Tooltip("TTC values above this are excluded from Finite TTC / Min TTC aggregation. If <= 0, no upper limit is used.")]
    public float maxTTCToRecord = 10.0f;

    [Header("Navigation Performance Metrics")]
    [Tooltip("Measure time-to-goal and path length.")]
    public bool enableNavigationPerformanceMetrics = true;
    [Tooltip("Calculate path length on the XZ plane by ignoring the Y axis.")]
    public bool useXZPlaneForPathLength = true;
    [Tooltip("If false, path length stops accumulating after success is detected.")]
    public bool continuePathLengthAfterGoal = false;

    public enum STENormalizationMode
    {
        // Paper-style STE: sqrt((sceneAvgTime / methodAvgTime) * (sceneAvgPath / methodAvgPath)).
        // Requires sceneAverageTimeToGoalForSTE and sceneAveragePathLengthForSTE to be > 0.
        SceneAverage,

        // Always compute a standalone inverse cost score: rawSTEScale / sqrt(time * pathLength).
        RawInverseCost,

        // Uses SceneAverage when scene averages are provided; otherwise falls back to RawInverseCost.
        AutoSceneAverageOrRawInverseCost
    }

    [Header("STE Settings")]
    [Tooltip("Add Spatio-Temporal Efficiency to the STCR summary.")]
    public bool enableSTEMetric = true;
    [Tooltip("Default avoids NA by falling back to RawInverseCost when scene-level averages are not provided.")]
    public STENormalizationMode steNormalizationMode = STENormalizationMode.AutoSceneAverageOrRawInverseCost;
    [Tooltip("Scene-level average time-to-goal over all evaluated methods. Used for paper-style STE when > 0.")]
    public float sceneAverageTimeToGoalForSTE = 0f;
    [Tooltip("Scene-level average path length over all evaluated methods. Used for paper-style STE when > 0.")]
    public float sceneAveragePathLengthForSTE = 0f;
    [Tooltip("Scale for RawInverseCost STE. Formula: scale / sqrt(avgTimeToGoal * avgPathLength).")]
    public float rawSTEScale = 100f;
    [Tooltip("Number of decimal places shown for STE.")]
    public int steDecimalPlaces = 3;

    [Header("Trajectory Map Settings")]
    public bool enableTrajectoryMap = true;
    public int mapResolution = 2048;
    public float mapWidth = 100f;
    public float mapHeight = 100f;
    public Color normalPathColor = Color.red;
    public Color dangerPathColor = Color.yellow;
    public Color dangerZoneOutlineColor = Color.blue;
    public Color obstacleOutlineColor = new Color(1f, 0.5f, 0f);
    public string mapFileName = "SocialTrajectoryMap.png";
    public float minRecordDistance = 0.1f;
    public int endPointRadius = 10;

    private List<BoxCollider> dangerZoneColliders = new List<BoxCollider>();
    private List<BoxCollider> obstacleColliders = new List<BoxCollider>();
    private float searchTimer = 0f;

    public struct TrajectoryPoint
    {
        public Vector3 position;
        public bool isDanger;

        public TrajectoryPoint(Vector3 pos, bool danger)
        {
            position = pos;
            isDanger = danger;
        }
    }

    public class AgentTrackingData
    {
        public float totalTime;
        public float dangerZoneTime;
        public float obstacleTime;
        public float agentCollisionTime;
        public Collider agentCollider;
        public bool hasReachedGoal;

        // Navigation performance
        public Vector3 previousPathMetricPosition;
        public bool hasPreviousPathMetricPosition;
        public float pathLength;
        public float pathLengthToGoal;
        public bool hasPathLengthToGoal;
        public float timeToGoal;
        public bool hasTimeToGoal;

        // Social context
        public Vector3 previousSocialMetricPosition;
        public Vector3 currentSocialMetricVelocity;
        public bool hasPreviousSocialMetricPosition;
        public float approxSocialRadius = 0.01f;

        public float nearestAgentDistanceSum;
        public float nearestAgentDistanceSampleTime;
        public float minNearestAgentDistance = Mathf.Infinity;
        public float lastNearestAgentDistance = Mathf.Infinity;

        public float ttcSum;
        public float ttcSampleTime;
        public float minTTC = Mathf.Infinity;
        public float lastTTC = Mathf.Infinity;
        public float lowTTCTime;

        public List<TrajectoryPoint> trajectory = new List<TrajectoryPoint>();
    }

    private Dictionary<Transform, AgentTrackingData> trackingData = new Dictionary<Transform, AgentTrackingData>();

    void Start()
    {
        // Auto-find Agents root if not assigned
        if (agentsRoot == null)
        {
            GameObject foundAgents = GameObject.Find("Agents");
            if (foundAgents != null)
            {
                agentsRoot = foundAgents.transform;
                Debug.Log($"[SocialTaskCompletionRate] Found agents root: '{agentsRoot.name}'.");
            }
            else
            {
                Debug.LogWarning("[SocialTaskCompletionRate] 'Agents' object not found in scene and not assigned.");
            }
        }

        // Auto-find Danger Zones if not assigned
        if (dangerZonesRoot == null)
        {
            GameObject foundObj = GameObject.Find("Danger Zones");
            if (foundObj != null)
            {
                dangerZonesRoot = foundObj.transform;
            }
        }

        if (dangerZonesRoot != null)
        {
            dangerZoneColliders = dangerZonesRoot.GetComponentsInChildren<BoxCollider>(true).ToList();
            Debug.Log($"[SocialTaskCompletionRate] Found {dangerZoneColliders.Count} danger zone colliders under '{dangerZonesRoot.name}'.");
        }
        else
        {
            Debug.LogWarning("[SocialTaskCompletionRate] 'Danger Zones' object not found in scene and not assigned.");
        }

        // Auto-find Obstacles if not assigned
        if (obstaclesRoot == null)
        {
            GameObject foundObj = GameObject.Find("Obstacles");
            if (foundObj != null)
            {
                obstaclesRoot = foundObj.transform;
            }
        }

        if (obstaclesRoot != null)
        {
            obstacleColliders = obstaclesRoot.GetComponentsInChildren<BoxCollider>(true).ToList();
            Debug.Log($"[SocialTaskCompletionRate] Found {obstacleColliders.Count} obstacle colliders under '{obstaclesRoot.name}'.");
        }
        else
        {
            Debug.LogWarning("[SocialTaskCompletionRate] 'Obstacles' object not found in scene and not assigned.");
        }

        SearchForAgents();
    }

    void Update()
    {
        searchTimer += Time.deltaTime;
        if (searchTimer >= searchInterval)
        {
            SearchForAgents();
            searchTimer = 0f;
        }

        UpdateAgentStats();
    }

    private void SearchForAgents()
    {
        if (agentsRoot == null) return;

        foreach (Transform child in agentsRoot)
        {
            if (child == null) continue;
            RegisterAgent(child);
        }
    }

    private void RegisterAgent(Transform agentTransform)
    {
        if (agentTransform == null) return;
        if (trackingData.ContainsKey(agentTransform)) return;

        Agent_Training agentScript = agentTransform.GetComponent<Agent_Training>();
        Agent_GoalOnly_Training goalOnlyScript = agentTransform.GetComponent<Agent_GoalOnly_Training>();

        if (agentScript == null && goalOnlyScript == null)
            return;

        Collider col = agentTransform.GetComponent<Collider>();
        if (col == null)
        {
            col = agentTransform.GetComponentInChildren<Collider>();
        }

        if (col != null)
        {
            AgentTrackingData newData = new AgentTrackingData();
            newData.agentCollider = col;
            trackingData.Add(agentTransform, newData);

            Debug.Log($"[SocialTaskCompletionRate] Registered agent: {agentTransform.name}");
        }
        else
        {
            Debug.LogWarning($"[SocialTaskCompletionRate] Collider not found for agent: {agentTransform.name}");
        }
    }

    private void UpdateAgentStats()
    {
        float dt = Time.deltaTime;

        if (enableSocialContextMetrics)
        {
            RefreshSocialKinematicState(dt);
        }

        foreach (var kvp in trackingData)
        {
            Transform agent = kvp.Key;
            AgentTrackingData data = kvp.Value;

            if (agent == null) continue;

            if (!agent.gameObject.activeInHierarchy)
            {
                // 비활성화된 agent도 reachedGoal flag가 이미 true라면 success로 기록한다.
                UpdateGoalState(agent, data);
                data.hasPreviousSocialMetricPosition = false;
                data.currentSocialMetricVelocity = Vector3.zero;
                data.hasPreviousPathMetricPosition = false;
                continue;
            }

            data.totalTime += dt;

            if (enableNavigationPerformanceMetrics)
            {
                UpdateNavigationPerformanceMetrics(agent, data);
            }

            UpdateGoalState(agent, data);

            bool inDangerZone = IsInDangerZone(data.agentCollider);
            bool collidingWithObstacle = IsCollidingWithObstacle(data.agentCollider);
            bool collidingWithAgent = IsCollidingWithAgent(data.agentCollider, agent);

            if (inDangerZone)
            {
                data.dangerZoneTime += dt;
                DebugDrawCollision(agent.position, Color.yellow, 0.5f);
            }

            if (collidingWithObstacle)
            {
                data.obstacleTime += dt;
                DebugDrawCollision(agent.position, Color.red, 0.6f);
            }

            if (collidingWithAgent)
            {
                data.agentCollisionTime += dt;
                DebugDrawCollision(agent.position, Color.magenta, 0.7f);
            }

            if (enableSocialContextMetrics)
            {
                UpdateSocialContextMetrics(agent, data, dt);
            }

            if (enableTrajectoryMap)
            {
                Vector3 currentPos = agent.position;

                if (data.trajectory.Count == 0 ||
                    Vector3.Distance(data.trajectory[data.trajectory.Count - 1].position, currentPos) >= minRecordDistance)
                {
                    data.trajectory.Add(new TrajectoryPoint(currentPos, inDangerZone));
                }
            }
        }
    }

    private void DebugDrawCollision(Vector3 center, Color color, float size)
    {
        Vector3 p = center + Vector3.up * 0.5f;
        Debug.DrawLine(p - Vector3.right * size, p + Vector3.right * size, color);
        Debug.DrawLine(p - Vector3.forward * size, p + Vector3.forward * size, color);
        Debug.DrawLine(p - Vector3.up * (size * 0.5f), p + Vector3.up * (size * 0.5f), color);
    }

    private void UpdateGoalState(Transform agent, AgentTrackingData data)
    {
        if (agent == null || data == null || data.hasReachedGoal) return;

        Agent_Training agentScript = agent.GetComponent<Agent_Training>();
        Agent_GoalOnly_Training goalOnlyScript = agent.GetComponent<Agent_GoalOnly_Training>();

        bool reachedGoal = false;

        if (agentScript != null && agentScript.reachedGoal)
        {
            reachedGoal = true;
        }
        else if (goalOnlyScript != null && goalOnlyScript.reachedGoal)
        {
            reachedGoal = true;
        }

        if (!reachedGoal) return;

        data.hasReachedGoal = true;

        if (enableNavigationPerformanceMetrics)
        {
            if (!data.hasTimeToGoal)
            {
                data.timeToGoal = data.totalTime;
                data.hasTimeToGoal = true;
            }

            if (!data.hasPathLengthToGoal)
            {
                data.pathLengthToGoal = data.pathLength;
                data.hasPathLengthToGoal = true;
            }
        }
    }

    private void UpdateNavigationPerformanceMetrics(Transform agent, AgentTrackingData data)
    {
        if (agent == null || data == null) return;

        Vector3 currentPosition = GetPathMetricPosition(agent.position);

        if (!data.hasPreviousPathMetricPosition)
        {
            data.previousPathMetricPosition = currentPosition;
            data.hasPreviousPathMetricPosition = true;
            return;
        }

        if (!data.hasReachedGoal || continuePathLengthAfterGoal)
        {
            data.pathLength += Vector3.Distance(data.previousPathMetricPosition, currentPosition);
        }

        data.previousPathMetricPosition = currentPosition;
    }

    private Vector3 GetPathMetricPosition(Vector3 worldPosition)
    {
        if (!useXZPlaneForPathLength)
        {
            return worldPosition;
        }

        return new Vector3(worldPosition.x, 0f, worldPosition.z);
    }

    private void RefreshSocialKinematicState(float dt)
    {
        if (dt <= 0.000001f) return;

        foreach (var kvp in trackingData)
        {
            Transform agent = kvp.Key;
            AgentTrackingData data = kvp.Value;

            if (agent == null || data == null)
            {
                continue;
            }

            if (!agent.gameObject.activeInHierarchy)
            {
                data.currentSocialMetricVelocity = Vector3.zero;
                data.hasPreviousSocialMetricPosition = false;
                continue;
            }

            Vector3 currentPosition = GetSocialMetricPosition(agent.position);

            if (data.hasPreviousSocialMetricPosition)
            {
                data.currentSocialMetricVelocity = (currentPosition - data.previousSocialMetricPosition) / dt;
            }
            else
            {
                data.currentSocialMetricVelocity = Vector3.zero;
                data.hasPreviousSocialMetricPosition = true;
            }

            data.previousSocialMetricPosition = currentPosition;
            data.approxSocialRadius = EstimateAgentSocialRadius(data);
        }
    }

    private void UpdateSocialContextMetrics(Transform currentAgent, AgentTrackingData data, float dt)
    {
        if (currentAgent == null || data == null || dt <= 0.000001f) return;

        float nearestDistance = ComputeNearestAgentDistance(currentAgent, data);
        data.lastNearestAgentDistance = nearestDistance;

        if (IsFiniteMetric(nearestDistance))
        {
            data.nearestAgentDistanceSum += nearestDistance * dt;
            data.nearestAgentDistanceSampleTime += dt;

            if (nearestDistance < data.minNearestAgentDistance)
            {
                data.minNearestAgentDistance = nearestDistance;
            }
        }

        float minTTC = ComputeMinimumTTC(currentAgent, data);
        data.lastTTC = minTTC;

        if (IsFiniteMetric(minTTC) && ShouldRecordTTC(minTTC))
        {
            data.ttcSum += minTTC * dt;
            data.ttcSampleTime += dt;

            if (minTTC < data.minTTC)
            {
                data.minTTC = minTTC;
            }
        }

        if (IsFiniteMetric(minTTC) && minTTC <= lowTTCThreshold)
        {
            data.lowTTCTime += dt;
        }
    }

    private Vector3 GetSocialMetricPosition(Vector3 worldPosition)
    {
        if (!useXZPlaneForSocialMetrics)
        {
            return worldPosition;
        }

        return new Vector3(worldPosition.x, 0f, worldPosition.z);
    }

    private float ComputeNearestAgentDistance(Transform currentAgent, AgentTrackingData currentData)
    {
        float nearestDistance = Mathf.Infinity;
        Vector3 currentPosition = GetSocialMetricPosition(currentAgent.position);

        foreach (var kvp in trackingData)
        {
            Transform otherAgent = kvp.Key;
            AgentTrackingData otherData = kvp.Value;

            if (otherAgent == null) continue;
            if (otherAgent == currentAgent) continue;
            if (!otherAgent.gameObject.activeInHierarchy) continue;

            Vector3 otherPosition = GetSocialMetricPosition(otherAgent.position);
            float distance = Vector3.Distance(currentPosition, otherPosition);

            if (subtractApproxAgentRadiusFromNearestDistance)
            {
                distance -= currentData.approxSocialRadius + otherData.approxSocialRadius;
                distance = Mathf.Max(0f, distance);
            }

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
            }
        }

        return nearestDistance;
    }

    private float ComputeMinimumTTC(Transform currentAgent, AgentTrackingData currentData)
    {
        float minTTC = Mathf.Infinity;
        Vector3 currentPosition = GetSocialMetricPosition(currentAgent.position);

        foreach (var kvp in trackingData)
        {
            Transform otherAgent = kvp.Key;
            AgentTrackingData otherData = kvp.Value;

            if (otherAgent == null) continue;
            if (otherAgent == currentAgent) continue;
            if (!otherAgent.gameObject.activeInHierarchy) continue;

            Vector3 otherPosition = GetSocialMetricPosition(otherAgent.position);

            if (TryComputePairTTC(currentPosition, currentData, otherPosition, otherData, out float pairTTC))
            {
                if (pairTTC < minTTC)
                {
                    minTTC = pairTTC;
                }
            }
        }

        return minTTC;
    }

    private bool TryComputePairTTC(
        Vector3 currentPosition,
        AgentTrackingData currentData,
        Vector3 otherPosition,
        AgentTrackingData otherData,
        out float ttc)
    {
        ttc = Mathf.Infinity;

        Vector3 relativePosition = otherPosition - currentPosition;
        Vector3 relativeVelocity = otherData.currentSocialMetricVelocity - currentData.currentSocialMetricVelocity;

        float relativeSpeedSq = relativeVelocity.sqrMagnitude;
        float minRelativeSpeedSq = minRelativeSpeedForTTC * minRelativeSpeedForTTC;

        if (relativeSpeedSq < minRelativeSpeedSq)
        {
            return false;
        }

        float radius = ttcCollisionRadius > 0f
            ? ttcCollisionRadius
            : Mathf.Max(0.01f, currentData.approxSocialRadius + otherData.approxSocialRadius);

        float a = relativeSpeedSq;
        float b = 2f * Vector3.Dot(relativePosition, relativeVelocity);
        float c = Vector3.Dot(relativePosition, relativePosition) - radius * radius;

        if (c <= 0f)
        {
            ttc = 0f;
            return true;
        }

        // Agents are moving away from each other, so no finite TTC is recorded.
        if (b >= 0f)
        {
            return false;
        }

        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
        {
            return false;
        }

        float sqrtDiscriminant = Mathf.Sqrt(discriminant);
        float firstHitTime = (-b - sqrtDiscriminant) / (2f * a);

        if (firstHitTime < 0f)
        {
            return false;
        }

        ttc = firstHitTime;
        return true;
    }

    private bool ShouldRecordTTC(float ttc)
    {
        if (!IsFiniteMetric(ttc)) return false;
        if (maxTTCToRecord <= 0f) return true;
        return ttc <= maxTTCToRecord;
    }

    private float EstimateAgentSocialRadius(AgentTrackingData data)
    {
        if (data == null || data.agentCollider == null)
        {
            return 0.01f;
        }

        Bounds bounds = data.agentCollider.bounds;

        if (useXZPlaneForSocialMetrics)
        {
            return Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, bounds.extents.z));
        }

        return Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)));
    }

    private bool IsInDangerZone(Collider agentCollider)
    {
        if (agentCollider == null) return false;

        foreach (var col in dangerZoneColliders)
        {
            if (col != null && col.enabled && CheckCollision(agentCollider, col))
                return true;
        }

        return false;
    }

    private bool IsCollidingWithObstacle(Collider agentCollider)
    {
        if (agentCollider == null) return false;

        foreach (var col in obstacleColliders)
        {
            if (col != null && col.enabled && CheckCollision(agentCollider, col))
                return true;
        }

        return false;
    }

    private bool IsCollidingWithAgent(Collider currentAgentCollider, Transform currentAgentTransform)
    {
        if (currentAgentCollider == null) return false;

        foreach (var kvp in trackingData)
        {
            Transform otherAgent = kvp.Key;
            AgentTrackingData otherData = kvp.Value;

            if (otherAgent == currentAgentTransform) continue;
            if (otherAgent == null || !otherAgent.gameObject.activeInHierarchy) continue;
            if (otherData.agentCollider == null) continue;

            if (CheckCollision(currentAgentCollider, otherData.agentCollider))
                return true;
        }

        return false;
    }

    private bool CheckCollision(Collider c1, Collider c2)
    {
        if (c1 == null || c2 == null) return false;
        if (!c1.bounds.Intersects(c2.bounds)) return false;

        Vector3 direction;
        float distance;
        return Physics.ComputePenetration(
            c1, c1.transform.position, c1.transform.rotation,
            c2, c2.transform.position, c2.transform.rotation,
            out direction, out distance);
    }

    public float CalculateRate(AgentTrackingData data)
    {
        if (data.totalTime <= 0.0001f) return 1f;

        float combinedUnsafeTime = data.dangerZoneTime + data.obstacleTime + data.agentCollisionTime;
        float rate = 1.0f - (combinedUnsafeTime / data.totalTime);
        return Mathf.Clamp01(rate);
    }

    public float CalculateAverageNearestAgentDistance(AgentTrackingData data)
    {
        if (data == null || data.nearestAgentDistanceSampleTime <= 0.0001f) return float.NaN;
        return data.nearestAgentDistanceSum / data.nearestAgentDistanceSampleTime;
    }

    public float CalculateAverageTTC(AgentTrackingData data)
    {
        if (data == null || data.ttcSampleTime <= 0.0001f) return float.NaN;
        return data.ttcSum / data.ttcSampleTime;
    }

    public float CalculateLowTTCRate(AgentTrackingData data)
    {
        if (data == null || data.totalTime <= 0.0001f) return 0f;
        return Mathf.Clamp01(data.lowTTCTime / data.totalTime);
    }

    private bool IsFiniteMetric(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private float CalculateMeanFiniteMetric(List<float> values)
    {
        if (values == null || values.Count == 0) return float.NaN;

        List<float> finiteValues = values
            .Where(v => IsFiniteMetric(v))
            .ToList();

        if (finiteValues.Count == 0) return float.NaN;
        return finiteValues.Average();
    }

    private float CalculateSTE(float averageTimeToGoal, float averagePathLength)
    {
        if (!enableSTEMetric) return float.NaN;
        if (!IsFiniteMetric(averageTimeToGoal) || !IsFiniteMetric(averagePathLength)) return float.NaN;
        if (averageTimeToGoal <= 0.0001f || averagePathLength <= 0.0001f) return float.NaN;

        bool hasSceneAverages =
            IsFiniteMetric(sceneAverageTimeToGoalForSTE) &&
            IsFiniteMetric(sceneAveragePathLengthForSTE) &&
            sceneAverageTimeToGoalForSTE > 0.0001f &&
            sceneAveragePathLengthForSTE > 0.0001f;

        if (steNormalizationMode == STENormalizationMode.SceneAverage)
        {
            if (!hasSceneAverages) return float.NaN;

            return Mathf.Sqrt(
                (sceneAverageTimeToGoalForSTE / averageTimeToGoal) *
                (sceneAveragePathLengthForSTE / averagePathLength));
        }

        if (steNormalizationMode == STENormalizationMode.AutoSceneAverageOrRawInverseCost && hasSceneAverages)
        {
            return Mathf.Sqrt(
                (sceneAverageTimeToGoalForSTE / averageTimeToGoal) *
                (sceneAveragePathLengthForSTE / averagePathLength));
        }

        float safeScale = rawSTEScale > 0.0001f ? rawSTEScale : 1f;
        return safeScale / Mathf.Sqrt(averageTimeToGoal * averagePathLength);
    }

    private string FormatSTE(float value)
    {
        if (!IsFiniteMetric(value)) return "NA";
        int decimals = Mathf.Clamp(steDecimalPlaces, 0, 6);
        return value.ToString("F" + decimals);
    }

    private string FormatAverageMetric(List<float> values, string suffix = "")
    {
        if (values == null || values.Count == 0) return "N/A";
        return $"{values.Average():F2}{suffix}";
    }

    private string FormatMinimumMetric(List<float> values, string suffix = "")
    {
        if (values == null || values.Count == 0) return "N/A";
        return $"{values.Min():F2}{suffix}";
    }

    private string FormatAveragePercent(List<float> values)
    {
        if (values == null || values.Count == 0) return "N/A";
        return $"{values.Average() * 100f:F2}%";
    }

    private void OnApplicationQuit()
    {
        int totalAgents = 0;
        int successCount = 0;

        List<float> allAgentCollisionRates = new List<float>();
        List<float> allObstacleRates = new List<float>();
        List<float> allDangerZoneViolationRates = new List<float>(); // Danger Zone Violation = dangerZoneTime / totalTime

        List<float> allTimeToGoalValues = new List<float>();
        List<float> allPathLengthValues = new List<float>();

        List<float> allAvgNearestAgentDistances = new List<float>();
        List<float> allAvgTTCs = new List<float>();
        List<float> allMinTTCs = new List<float>();
        List<float> allLowTTCRates = new List<float>();

        foreach (var kvp in trackingData)
        {
            Transform agent = kvp.Key;
            AgentTrackingData data = kvp.Value;

            if (agent == null || data == null) continue;

            // 종료 직전에 한 번 더 success flag를 확인한다.
            UpdateGoalState(agent, data);

            totalAgents++;

            if (data.hasReachedGoal)
            {
                successCount++;
            }

            if (enableNavigationPerformanceMetrics)
            {
                bool hasObservedNavigation = data.totalTime > 0.0001f || data.pathLength > 0.0001f || data.hasReachedGoal;
                if (hasObservedNavigation)
                {
                    // All 기준:
                    // - 성공 agent는 goal 도달 시점의 time/path를 사용
                    // - 미도달 agent는 관측 종료 시점까지의 active time/path를 사용
                    allTimeToGoalValues.Add(data.hasTimeToGoal ? data.timeToGoal : data.totalTime);
                    allPathLengthValues.Add(data.hasPathLengthToGoal ? data.pathLengthToGoal : data.pathLength);
                }
            }

            if (data.totalTime > 0.0001f)
            {
                float obstacleRate = data.obstacleTime / data.totalTime;
                float collisionRate = data.agentCollisionTime / data.totalTime;
                float dangerZoneViolationRate = data.dangerZoneTime / data.totalTime;

                allObstacleRates.Add(obstacleRate);
                allAgentCollisionRates.Add(collisionRate);
                allDangerZoneViolationRates.Add(dangerZoneViolationRate);

                if (enableSocialContextMetrics)
                {
                    float avgNearestDistance = CalculateAverageNearestAgentDistance(data);
                    if (IsFiniteMetric(avgNearestDistance))
                    {
                        allAvgNearestAgentDistances.Add(avgNearestDistance);
                    }

                    float avgTTC = CalculateAverageTTC(data);
                    if (IsFiniteMetric(avgTTC))
                    {
                        allAvgTTCs.Add(avgTTC);
                    }

                    if (IsFiniteMetric(data.minTTC))
                    {
                        allMinTTCs.Add(data.minTTC);
                    }

                    float lowTTCRate = CalculateLowTTCRate(data);
                    allLowTTCRates.Add(lowTTCRate);
                }
            }
        }

        float goalSuccessRate = totalAgents > 0 ? (float)successCount / totalAgents : 0f;
        float averageTimeToGoalForSTE = CalculateMeanFiniteMetric(allTimeToGoalValues);
        float averagePathLengthForSTE = CalculateMeanFiniteMetric(allPathLengthValues);
        float ste = CalculateSTE(averageTimeToGoalForSTE, averagePathLengthForSTE);

        string header =
            "agent collision | obstacle collision | GSR | Danger Zone Violation | " +
            "Time To Goal (All) | Path Length (All) | STE | Nearest Agent Distance | " +
            $"Finite TTC (≤{maxTTCToRecord:F1}) | Min TTC(s) | Low-TTC Rate (≤{lowTTCThreshold:F2}s)";

        string values =
            $"{FormatAveragePercent(allAgentCollisionRates)} | " +
            $"{FormatAveragePercent(allObstacleRates)} | " +
            $"{goalSuccessRate * 100f:F2}% | " +
            $"{FormatAveragePercent(allDangerZoneViolationRates)} | " +
            $"{FormatAverageMetric(allTimeToGoalValues, "s")} | " +
            $"{FormatAverageMetric(allPathLengthValues, "m")} | " +
            $"{FormatSTE(ste)} | " +
            $"{FormatAverageMetric(allAvgNearestAgentDistances, "m")} | " +
            $"{FormatAverageMetric(allAvgTTCs, "s")} | " +
            $"{FormatMinimumMetric(allMinTTCs, "s")} | " +
            $"{FormatAveragePercent(allLowTTCRates)}";

        Debug.Log($"[STCR Summary]\n{header}\n{values}");

        if (enableTrajectoryMap)
        {
            GenerateTrajectoryMap();
        }
    }

    private void GenerateTrajectoryMap()
    {
        Texture2D texture = new Texture2D(mapResolution, mapResolution);
        Color[] resetColor = new Color[mapResolution * mapResolution];
        for (int i = 0; i < resetColor.Length; i++) resetColor[i] = Color.white;
        texture.SetPixels(resetColor);

        float minX = -mapWidth / 2f;
        float minZ = -mapHeight / 2f;

        DrawColliders(texture, dangerZoneColliders, dangerZoneOutlineColor, minX, minZ);
        DrawColliders(texture, obstacleColliders, obstacleOutlineColor, minX, minZ);

        foreach (var kvp in trackingData)
        {
            List<TrajectoryPoint> path = kvp.Value.trajectory;
            if (path.Count < 2) continue;

            Vector2 prevPixel = WorldToPixel(path[0].position, minX, minZ);

            for (int i = 1; i < path.Count; i++)
            {
                Vector2 currentPixel = WorldToPixel(path[i].position, minX, minZ);
                Color color = path[i].isDanger ? dangerPathColor : normalPathColor;
                DrawLine(texture, prevPixel, currentPixel, color, 3);
                prevPixel = currentPixel;
            }

            DrawCircle(texture, prevPixel, endPointRadius, normalPathColor);
        }

        texture.Apply();
        byte[] bytes = texture.EncodeToPNG();
        string pathToFile = Path.Combine(Application.dataPath, mapFileName);
        File.WriteAllBytes(pathToFile, bytes);
        Debug.Log($"[SocialTaskCompletionRate] Trajectory map saved to: {pathToFile}");
    }

    private void DrawColliders(Texture2D texture, List<BoxCollider> colliders, Color color, float minX, float minZ)
    {
        foreach (var col in colliders)
        {
            if (col == null) continue;

            Transform t = col.transform;
            Vector3 center = col.center;
            Vector3 size = col.size;

            Vector3 p1 = t.TransformPoint(center + new Vector3(-size.x, -size.y, -size.z) * 0.5f);
            Vector3 p2 = t.TransformPoint(center + new Vector3(size.x, -size.y, -size.z) * 0.5f);
            Vector3 p3 = t.TransformPoint(center + new Vector3(size.x, -size.y, size.z) * 0.5f);
            Vector3 p4 = t.TransformPoint(center + new Vector3(-size.x, -size.y, size.z) * 0.5f);

            Vector2 px1 = WorldToPixel(p1, minX, minZ);
            Vector2 px2 = WorldToPixel(p2, minX, minZ);
            Vector2 px3 = WorldToPixel(p3, minX, minZ);
            Vector2 px4 = WorldToPixel(p4, minX, minZ);

            DrawLine(texture, px1, px2, color, 2);
            DrawLine(texture, px2, px3, color, 2);
            DrawLine(texture, px3, px4, color, 2);
            DrawLine(texture, px4, px1, color, 2);
        }
    }

    private Vector2 WorldToPixel(Vector3 worldPos, float minX, float minZ)
    {
        float u = (worldPos.x - minX) / mapWidth;
        float v = (worldPos.z - minZ) / mapHeight;
        return new Vector2(u * (mapResolution - 1), v * (mapResolution - 1));
    }

    private void DrawLine(Texture2D tex, Vector2 p1, Vector2 p2, Color col, int thickness)
    {
        int x0 = (int)p1.x;
        int y0 = (int)p1.y;
        int x1 = (int)p2.x;
        int y1 = (int)p2.y;

        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            DrawBrush(tex, x0, y0, col, thickness);

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private void DrawBrush(Texture2D tex, int x, int y, Color col, int thickness)
    {
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

    private void DrawCircle(Texture2D tex, Vector2 center, int radius, Color col)
    {
        int cx = (int)center.x;
        int cy = (int)center.y;

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                if (x * x + y * y <= radius * radius)
                {
                    int px = cx + x;
                    int py = cy + y;
                    if (px >= 0 && px < tex.width && py >= 0 && py < tex.height)
                    {
                        tex.SetPixel(px, py, col);
                    }
                }
            }
        }
    }
}