using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Unity.MLAgents; // Necessary for Agent reference

public class TestManager : MonoBehaviour
{
    public enum ScenarioType { Intersection, Intersection2, Hallway, Density }
    public enum AgentCount { Count_3 = 3, Count_6 = 6, Count_9 = 9, Count_12 = 12, Count_20 = 20 }

    [Header("Settings")]
    public ScenarioType scenario = ScenarioType.Intersection;
    public AgentCount agentCount = AgentCount.Count_9;
    public GameObject agentPrefab;
    public Transform agentRoot;
    public float gridSpacing = 2.0f; // Agent spacing

    [Header("Density Scenario Settings")]
    public int densitySpawnCount = 50;
    public float densityAreaSize = 30f;

    [Header("Runtime")]
    public List<GameObject> activeAgents = new List<GameObject>();
    public List<Transform> activeGoals = new List<Transform>();

    void Start()
    {
        if (agentPrefab == null)
        {
            Debug.LogError("Agent Prefab is missing in TestManager!");
            return;
        }

        SpawnScenario();
    }

    public void SpawnScenario()
    {
        // Cleanup existing agents and goals
        foreach (var agent in activeAgents)
        {
            if (agent != null) Destroy(agent);
        }
        foreach (var goal in activeGoals)
        {
            if (goal != null) Destroy(goal.gameObject);
        }
        activeAgents.Clear();
        activeGoals.Clear();

        if (scenario == ScenarioType.Intersection)
        {
            // 1. X-axis Group (Start: x=-15, z=0 -> Goal: x=15, z=0) [Straight]
            SpawnGroup(new Vector3(-15, 0, 0), new Vector3(15, 0, 0), Quaternion.Euler(0, 90, 0));

            // 2. Z-axis Group (Start: x=0, z=15 -> Goal: x=0, z=-15) [Straight]
            SpawnGroup(new Vector3(0, 0, 15), new Vector3(0, 0, -15), Quaternion.Euler(0, 180, 0));
        }
        else if (scenario == ScenarioType.Hallway)
        {
            // 1. X-axis Right Group (Start: x=15 -> Goal: x=-15)
            SpawnGroup(new Vector3(15, 0, 0), new Vector3(-15, 0, 0), Quaternion.Euler(0, -90, 0));

            // 2. X-axis Left Group (Start: x=-15 -> Goal: x=15)
            SpawnGroup(new Vector3(-15, 0, 0), new Vector3(15, 0, 0), Quaternion.Euler(0, 90, 0));
        }
        else if (scenario == ScenarioType.Intersection2)
        {
            // 1. West -> East
            SpawnGroup(new Vector3(-15, 0, 0), new Vector3(15, 0, 0), Quaternion.Euler(0, 90, 0), true);
            // 2. East -> West
            SpawnGroup(new Vector3(15, 0, 0), new Vector3(-15, 0, 0), Quaternion.Euler(0, -90, 0), true);
            // 3. North -> South
            SpawnGroup(new Vector3(0, 0, 15), new Vector3(0, 0, -15), Quaternion.Euler(0, 180, 0), true);
            // 4. South -> North
            SpawnGroup(new Vector3(0, 0, -15), new Vector3(0, 0, 15), Quaternion.Euler(0, 0, 0), true);
        }
        else if (scenario == ScenarioType.Density)
        {
            SpawnDensityScenario();
        }
    }

    void SpawnDensityScenario()
    {
        float halfSize = densityAreaSize * 0.5f;

        for (int i = 0; i < densitySpawnCount; i++)
        {
            // 1. Random Spawn Position with Min Distance Check
            Vector3 spawnPos = Vector3.zero;
            bool validPosition = false;
            int posAttempts = 0;

            while (!validPosition && posAttempts < 50)
            {
                spawnPos = new Vector3(
                    Random.Range(-halfSize, halfSize),
                    0.05f,
                    Random.Range(-halfSize, halfSize)
                );

                validPosition = true;
                foreach (var existingAgent in activeAgents)
                {
                    if (existingAgent == null) continue;
                    if (Vector3.Distance(spawnPos, existingAgent.transform.position) < 1.0f)
                    {
                        validPosition = false;
                        break;
                    }
                }
                posAttempts++;
            }

            if (!validPosition)
            {
                Debug.LogWarning($"Could not find valid spawn position for Agent {i} after {posAttempts} attempts.");
            }

            // 2. Random Goal Position
            Vector3 goalPos = new Vector3(
                Random.Range(-halfSize, halfSize),
                0.05f,
                Random.Range(-halfSize, halfSize)
            );

            // Ensure minimum distance (e.g. 5m) to avoid immediate finish
            int attempts = 0;
            while (Vector3.Distance(spawnPos, goalPos) < 5.0f && attempts < 10)
            {
                goalPos = new Vector3(
                   Random.Range(-halfSize, halfSize),
                   0.05f,
                   Random.Range(-halfSize, halfSize)
               );
                attempts++;
            }

            // 3. Instantiate
            // Look at goal initially
            Vector3 dir = goalPos - spawnPos;
            Quaternion initialRot = Quaternion.identity;
            if (dir != Vector3.zero) initialRot = Quaternion.LookRotation(dir);

            GameObject agent = Instantiate(agentPrefab, spawnPos, initialRot, agentRoot);
            agent.name = $"{agentPrefab.name}_{activeAgents.Count}";

            // 4. Setup Goal
            GameObject goalObj = new GameObject($"{agent.name}_Goal");
            if (agentRoot != null) goalObj.transform.SetParent(agentRoot);
            goalObj.transform.position = goalPos;
            activeGoals.Add(goalObj.transform);

            // 5. Setup Agent (Support both types)
            SetupAgent(agent, goalObj.transform);

            activeAgents.Add(agent);
        }
    }

    void SpawnGroup(Vector3 centerPos, Vector3 targetPos, Quaternion initialRotation, bool useRandomness = false)
    {
        // widthCount is fixed to 3 (across the direction of travel)
        // depthCount depends on the total agentCount
        int total = (int)agentCount;
        int widthCount = (total == 20) ? 4 : 3;
        int depthCount = total / widthCount;

        // 1. Generate all target grid offsets
        List<Vector3> targetOffsets = new List<Vector3>();
        for (int d = 0; d < depthCount; d++)
        {
            for (int w = 0; w < widthCount; w++)
            {
                float lx = (w - (widthCount - 1) * 0.5f) * gridSpacing;
                float lz = (d - (depthCount - 1) * 0.5f) * gridSpacing;
                targetOffsets.Add(new Vector3(lx, 0, lz));
            }
        }

        // 2. Shuffle target offsets if randomness is requested
        if (useRandomness)
        {
            for (int i = 0; i < targetOffsets.Count; i++)
            {
                Vector3 temp = targetOffsets[i];
                int randomIndex = Random.Range(i, targetOffsets.Count);
                targetOffsets[i] = targetOffsets[randomIndex];
                targetOffsets[randomIndex] = temp;
            }
        }

        int agentIndex = 0;

        for (int d = 0; d < depthCount; d++)
        {
            for (int w = 0; w < widthCount; w++)
            {
                // Local coordinates:
                // x is across the direction of travel (width)
                // z is along the direction of travel (depth)
                float localX = (w - (widthCount - 1) * 0.5f) * gridSpacing;
                float localZ = (d - (depthCount - 1) * 0.5f) * gridSpacing;

                // Add jitter to spawn position if requested
                if (useRandomness)
                {
                    localX += Random.Range(-gridSpacing * 0.3f, gridSpacing * 0.3f);
                    localZ += Random.Range(-gridSpacing * 0.3f, gridSpacing * 0.3f);
                }

                Vector3 localOffset = new Vector3(localX, 0, localZ);
                Vector3 worldOffset = initialRotation * localOffset;

                Vector3 spawnPos = centerPos + worldOffset;
                spawnPos.y = 0.05f;

                GameObject agent = Instantiate(agentPrefab, spawnPos, initialRotation, agentRoot);
                agent.name = $"{agentPrefab.name}_{activeAgents.Count}";

                // Goal Object
                // Apply relative offset from spawn center to target center
                Vector3 targetLocalOffset = targetOffsets[agentIndex];
                Vector3 targetWorldOffset = initialRotation * targetLocalOffset;

                Vector3 finalGoalPos = targetPos + targetWorldOffset;
                finalGoalPos.y = 0.05f;

                GameObject goalObj = new GameObject($"{agent.name}_Goal");
                if (agentRoot != null) goalObj.transform.SetParent(agentRoot);
                goalObj.transform.position = finalGoalPos;
                activeGoals.Add(goalObj.transform);

                agentIndex++;

                // Setup Agent
                SetupAgent(agent, goalObj.transform);

                activeAgents.Add(agent);
            }
        }
    }

    void SetupAgent(GameObject agent, Transform goalTransform)
    {
        var agentTraining = agent.GetComponent<Agent_Training>();
        var agentGoalOnly = agent.GetComponent<Agent_GoalOnly_Training>();

        if (agentTraining != null)
        {
            agentTraining.pdmMode = true;
            agentTraining.GoalTransform = goalTransform;
            // agentTraining.disableOnGoal = true; // Optional: based on preference
        }
        else if (agentGoalOnly != null)
        {
            agentGoalOnly.pdmMode = true;
            agentGoalOnly.GoalTransform = goalTransform;
            // agentGoalOnly.disableOnGoal = true; // Optional
        }
        else
        {
            Debug.LogWarning($"Agent {agent.name} does not have Agent_Training or Agent_GoalOnly_Training component!");
        }
    }
}
