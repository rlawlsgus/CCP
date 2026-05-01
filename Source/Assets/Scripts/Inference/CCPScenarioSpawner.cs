using UnityEngine;
using System.Collections.Generic;
using System;

public class CCPScenarioSpawner : MonoBehaviour
{
    [Serializable]
    public class AgentScenarioData
    {
        public string name = "Agent";
        public Vector2 startPos; // 2D planar position
        public float startRotation; // 2D planar rotation (degrees)
        public Vector2 goalPos;  // 2D planar position
        public int groupId = 0;
        public Color debugColor = Color.cyan;
    }

    [Serializable]
    public class ScenarioExportData
    {
        public float worldHeight;
        public List<AgentScenarioData> agents;
    }

    [Header("Settings")]
    public GameObject agentPrefab;
    public Transform agentsRoot;
    [Tooltip("Drag and drop the scenario JSON file here.")]
    public TextAsset scenarioJsonFile;
    public bool spawnOnStart = true;
    public bool disableAgentsOnGoal = true;

    [Header("Runtime Info")]
    public List<GameObject> spawnedAgents = new List<GameObject>();

    private ScenarioExportData lastGizmoData;
    private TextAsset lastJsonFile;

    private void Start()
    {
        if (spawnOnStart)
        {
            SpawnFromScenario();
        }
    }

    void OnDrawGizmos()
    {
        if (Application.isPlaying || scenarioJsonFile == null) return;

        if (lastGizmoData == null || lastJsonFile != scenarioJsonFile)
        {
            try
            {
                lastGizmoData = JsonUtility.FromJson<ScenarioExportData>(scenarioJsonFile.text);
                lastJsonFile = scenarioJsonFile;
            }
            catch
            {
                return;
            }
        }

        if (lastGizmoData == null || lastGizmoData.agents == null) return;

        float y = lastGizmoData.worldHeight;
        foreach (var agent in lastGizmoData.agents)
        {
            Vector3 s = new Vector3(agent.startPos.x, y, agent.startPos.y);
            Vector3 g = new Vector3(agent.goalPos.x, y, agent.goalPos.y);

            Gizmos.color = agent.debugColor;
            Gizmos.DrawLine(s, g);
            Gizmos.DrawWireSphere(s, 0.3f);
            Gizmos.DrawLine(s, s + Vector3.up * 1.5f);

            Vector3 forward = Quaternion.Euler(0, agent.startRotation, 0) * Vector3.forward;
            Gizmos.DrawLine(s, s + forward * 0.8f);
            Gizmos.DrawWireSphere(s + forward * 0.8f, 0.05f);

            Color goalColor = agent.debugColor;
            goalColor.a = 0.5f;
            Gizmos.color = goalColor;
            Gizmos.DrawWireCube(g, new Vector3(0.5f, 0.1f, 0.5f));
            Gizmos.DrawLine(g, g + Vector3.up * 0.5f);
        }
    }

    [ContextMenu("Spawn From Scenario")]
    public void SpawnFromScenario()
    {
        if (scenarioJsonFile == null || agentPrefab == null) return;

        string json = scenarioJsonFile.text;
        ScenarioExportData data = JsonUtility.FromJson<ScenarioExportData>(json);
        if (data == null || data.agents == null) return;

        ClearExistingAgents();

        float y = data.worldHeight;
        GameObject goalRoot = new GameObject("Scenario_Goals_Runtime");
        if (agentsRoot != null) goalRoot.transform.SetParent(agentsRoot);

        Dictionary<int, List<GameObject>> groupGroups = new Dictionary<int, List<GameObject>>();

        for (int i = 0; i < data.agents.Count; i++)
        {
            var agentData = data.agents[i];
            Vector3 spawnPos = new Vector3(agentData.startPos.x, y, agentData.startPos.y);
            Vector3 goalPos = new Vector3(agentData.goalPos.x, y, agentData.goalPos.y);

            GameObject go = Instantiate(agentPrefab, spawnPos, Quaternion.Euler(0, agentData.startRotation, 0));
            go.name = $"{agentData.name}_{i}_Sim";
            if (agentsRoot != null) go.transform.SetParent(agentsRoot);

            InitializeAgent(go, spawnPos, goalPos, i);
            spawnedAgents.Add(go);

            GameObject goalObj = new GameObject($"{agentData.name}_{i}_Goal");
            goalObj.transform.position = goalPos;
            goalObj.transform.SetParent(goalRoot.transform);

            // Setting GoalTransform for CCP Agent
            var agentT = go.GetComponent<Agent_Training>();
            var agentGO = go.GetComponent<Agent_GoalOnly_Training>();
            if (agentT != null) agentT.GoalTransform = goalObj.transform;
            else if (agentGO != null) agentGO.GoalTransform = goalObj.transform;

            if (agentData.groupId != 0)
            {
                if (!groupGroups.ContainsKey(agentData.groupId)) groupGroups[agentData.groupId] = new List<GameObject>();
                groupGroups[agentData.groupId].Add(go);
            }
        }

        // Handle groups by adding to groupMembers list
        foreach (var group in groupGroups.Values)
        {
            if (group.Count <= 1) continue;
            foreach (var agentGo in group)
            {
                var at = agentGo.GetComponent<Agent_Training>();
                var ago = agentGo.GetComponent<Agent_GoalOnly_Training>();
                foreach (var member in group)
                {
                    if (agentGo == member) continue;
                    if (at != null) at.groupMembers.Add(member.transform);
                    else if (ago != null) ago.groupMembers.Add(member.transform);
                }
            }
        }

        Debug.Log($"[CCPScenarioSpawner] Successfully spawned {spawnedAgents.Count} agents. Groups: {groupGroups.Count}");
    }

    private void InitializeAgent(GameObject go, Vector3 spawnPos, Vector3 goalPos, int index)
    {
        var agentT = go.GetComponent<Agent_Training>();
        var agentGO = go.GetComponent<Agent_GoalOnly_Training>();

        if (agentT != null)
        {
            agentT.agentID = index;
            agentT.pdmMode = true; // Inference mode
            agentT.disableOnGoal = disableAgentsOnGoal;
            agentT.SetGoal(goalPos);
            agentT.groupMembers.Clear();
        }
        else if (agentGO != null)
        {
            agentGO.agentID = index;
            agentGO.pdmMode = true; // Inference mode
            agentGO.disableOnGoal = disableAgentsOnGoal;
            agentGO.SetGoal(goalPos);
            agentGO.groupMembers.Clear();
        }
    }

    [ContextMenu("Clear Agents")]
    public void ClearExistingAgents()
    {
        foreach (var agent in spawnedAgents)
        {
            if (agent != null)
            {
                if (Application.isPlaying) Destroy(agent);
                else DestroyImmediate(agent);
            }
        }
        spawnedAgents.Clear();

        if (agentsRoot != null)
        {
            List<GameObject> toDestroy = new List<GameObject>();
            foreach (Transform child in agentsRoot) toDestroy.Add(child.gameObject);
            foreach (var child in toDestroy)
            {
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }
    }
}
