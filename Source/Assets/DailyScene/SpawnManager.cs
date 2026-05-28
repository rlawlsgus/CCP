using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

public class SpawnManager : MonoBehaviour
{
    public TextMeshProUGUI stateText;
    public Transform sidewalksParent;
    public GameObject agentPrefab; // Assign the CCP Agent Prefab here
    public Transform SpawnedAgentParent;

    [Header("General Settings")]
    public bool autoStart = false;

    [Header("Spawn Settings")]
    public string scenarioFileName = "SpawnScenario.txt";
    public bool disableAgentsOnGoal = true;
    public float yOffset = 0.0f;

    // Data classes matching the JSON structure
    [System.Serializable]
    public class ScenarioData
    {
        public int totalAgents;
        public List<TileSpawnInfo> spawns = new List<TileSpawnInfo>();
    }

    [System.Serializable]
    public class TileSpawnInfo
    {
        public int sidewalkIndex;
        public string type;
        public List<AgentInfo> agents = new List<AgentInfo>();
    }

    [System.Serializable]
    public class AgentInfo
    {
        public int spawnId;
        public string agentName;
        public float[] position;
        public float[] goalPosition;
        public int groupSize;
    }

    private void Start()
    {
        if (autoStart)
        {
            OnClickSpawn();
        }
    }

    public void OnClickSpawn()
    {
        string path = Path.Combine(Application.dataPath, "DailyScene", scenarioFileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"SpawnManager: Scenario file not found at {path}");
            return;
        }

        string fileContent = File.ReadAllText(path);

        // Find the start of the JSON object
        int jsonStartIndex = fileContent.IndexOf('{');
        if (jsonStartIndex == -1)
        {
            Debug.LogError("SpawnManager: No JSON object found in scenario file.");
            return;
        }

        string jsonContent = fileContent.Substring(jsonStartIndex);
        ScenarioData scenarioData = JsonConvert.DeserializeObject<ScenarioData>(jsonContent);

        if (scenarioData == null)
        {
            Debug.LogError("SpawnManager: Failed to parse scenario data.");
            return;
        }

        // 1. Clear existing agents
        foreach (Transform child in SpawnedAgentParent) Destroy(child.gameObject);

        int spawnedCount = 0;

        // 2. Iterate and Spawn
        foreach (var tileSpawn in scenarioData.spawns)
        {
            foreach (var agentInfo in tileSpawn.agents)
            {
                SpawnCCPAgent(agentInfo);
                spawnedCount++;
            }
        }

        if (stateText != null) stateText.text = $"Spawned {spawnedCount} CCP Agents from Scenario";
        Debug.Log($"Spawned {spawnedCount} CCP Agents from {scenarioFileName}");
    }

    void SpawnCCPAgent(AgentInfo info)
    {
        Vector3 spawnPos = new Vector3(info.position[0], info.position[1] + yOffset, info.position[2]);
        Vector3 goalPos = new Vector3(info.goalPosition[0], info.goalPosition[1] + yOffset, info.goalPosition[2]);

        // Create Agent
        GameObject agentObj = Instantiate(agentPrefab, spawnPos, Quaternion.identity);
        agentObj.name = info.agentName;
        if (SpawnedAgentParent != null) agentObj.transform.SetParent(SpawnedAgentParent);

        // Setup Goal Object
        GameObject goalObj = new GameObject($"{info.agentName}_Goal");
        goalObj.transform.position = goalPos;
        if (SpawnedAgentParent != null) goalObj.transform.SetParent(SpawnedAgentParent); // Keep hierarchy clean

        // Initialize CCP Agent Component
        // Check for Agent_Training or Agent_GoalOnly_Training
        Agent_Training agentTraining = agentObj.GetComponent<Agent_Training>();
        if (agentTraining != null)
        {
            InitializeAgent(agentTraining, goalObj.transform, spawnPos, goalPos);
        }
        else
        {
            Agent_GoalOnly_Training agentGoalOnly = agentObj.GetComponent<Agent_GoalOnly_Training>();
            if (agentGoalOnly != null)
            {
                InitializeAgent(agentGoalOnly, goalObj.transform, spawnPos, goalPos);
            }
            else
            {
                Debug.LogWarning($"SpawnManager: Agent {agentObj.name} does not have Agent_Training or Agent_GoalOnly_Training component.");
            }
        }
    }

    void InitializeAgent(Agent_Training agent, Transform goalTransform, Vector3 spawnPos, Vector3 goalPos)
    {
        agent.pdmMode = true;
        agent.GoalTransform = goalTransform;
        agent.disableOnGoal = disableAgentsOnGoal;
        agent.SetGoal(goalPos);
        agent.ForceReset(spawnPos, agent.transform.rotation, Vector3.zero);
    }

    void InitializeAgent(Agent_GoalOnly_Training agent, Transform goalTransform, Vector3 spawnPos, Vector3 goalPos)
    {
        agent.pdmMode = true;
        agent.GoalTransform = goalTransform;
        agent.disableOnGoal = disableAgentsOnGoal;
        agent.SetGoal(goalPos);
        agent.ForceReset(spawnPos, agent.transform.rotation, Vector3.zero);
    }
}