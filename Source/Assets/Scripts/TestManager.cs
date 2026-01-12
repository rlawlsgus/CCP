using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Unity.MLAgents; // Necessary for Agent reference

public class TestManager : MonoBehaviour
{
    public enum ScenarioType { Intersection, Intersection2, Hallway, Density }

    [Header("Settings")]
    public ScenarioType scenario = ScenarioType.Intersection;
    public GameObject agentPrefab;
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
        else if (scenario == ScenarioType.Intersection2)
        {
            // 1. X-axis Group (Start: x=15, z=0 -> Goal: x=0, z=-15) [Turn Left]
            SpawnGroup(new Vector3(15, 0, 0), new Vector3(0, 0, -15), Quaternion.Euler(0, -90, 0));

            // 2. Z-axis Group (Start: x=0, z=15 -> Goal: x=-15, z=0) [Turn Right]
            SpawnGroup(new Vector3(0, 0, 15), new Vector3(-15, 0, 0), Quaternion.Euler(0, 180, 0));
        }
        else if (scenario == ScenarioType.Hallway)
        {
            // 1. X-axis Right Group (Start: x=15 -> Goal: x=-15)
            SpawnGroup(new Vector3(15, 0, 0), new Vector3(-15, 0, 0), Quaternion.Euler(0, -90, 0));

            // 2. X-axis Left Group (Start: x=-15 -> Goal: x=15)
            SpawnGroup(new Vector3(-15, 0, 0), new Vector3(15, 0, 0), Quaternion.Euler(0, 90, 0));
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

            GameObject agent = Instantiate(agentPrefab, spawnPos, initialRot);
            agent.name = $"{agentPrefab.name}_{activeAgents.Count}";

            // 4. Setup Goal
            GameObject goalObj = new GameObject($"{agent.name}_Goal");
            goalObj.transform.position = goalPos;
            activeGoals.Add(goalObj.transform);

            // 5. Setup Agent (Support both types)
            SetupAgent(agent, goalObj.transform);

            activeAgents.Add(agent);
        }
    }

    void SpawnGroup(Vector3 centerPos, Vector3 targetPos, Quaternion initialRotation)
    {
        // 3x3 Grid
        int rows = 3;
        int cols = 3;

        // Calculate start point so centerPos is the center of the grid
        float startX = centerPos.x - (cols - 1) * gridSpacing * 0.5f;
        float startZ = centerPos.z - (rows - 1) * gridSpacing * 0.5f;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Vector3 spawnPos = new Vector3(
                    startX + c * gridSpacing,
                    0.05f, // Slightly above ground
                    startZ + r * gridSpacing
                );

                GameObject agent = Instantiate(agentPrefab, spawnPos, initialRotation);
                agent.name = $"{agentPrefab.name}_{activeAgents.Count}";

                // Goal Object
                // Apply relative offset from spawn center to target center
                Vector3 offset = spawnPos - centerPos;
                Vector3 finalGoalPos = targetPos + offset;

                GameObject goalObj = new GameObject($"{agent.name}_Goal");
                goalObj.transform.position = finalGoalPos;
                activeGoals.Add(goalObj.transform);

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
