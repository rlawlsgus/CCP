using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;

public class DiversityEvaluator : MonoBehaviour
{
    [Header("Settings")]
    public List<GameObject> targetAgents; // Assign existing agents here
    public int trialsPerEpisode = 5;
    public string outputFileName = "DiversityScore.txt";

    private List<List<Vector3>> recordedPaths = new List<List<Vector3>>();
    private List<Vector3> currentPath = new List<Vector3>();
    
    // State
    private bool isRunning = false;

    void Start()
    {
        if (targetAgents == null || targetAgents.Count == 0)
        {
            // Auto-find if empty: Check for Agent_Training or Agent_GoalOnly_Training
            var foundTraining = FindObjectsOfType<Agent_Training>().Select(c => c.gameObject).ToList();
            var foundGoalOnly = FindObjectsOfType<Agent_GoalOnly_Training>().Select(c => c.gameObject).ToList();
            
            targetAgents = new List<GameObject>();
            targetAgents.AddRange(foundTraining);
            targetAgents.AddRange(foundGoalOnly);
            
            // Remove duplicates if any object has both components (unlikely but safe)
            targetAgents = targetAgents.Distinct().ToList();
        }

        if (targetAgents.Count == 0)
        {
            Debug.LogError("[DiversityEvaluator] No agents found or assigned!");
            return;
        }

        StartCoroutine(EvaluationLoop());
    }

    IEnumerator EvaluationLoop()
    {
        isRunning = true;
        List<float> agentDiversityScores = new List<float>();

        foreach (var agent in targetAgents)
        {
            if (agent == null) continue;

            Debug.Log($"[DiversityEvaluator] Evaluating Agent: {agent.name}");

            // 1. Store Initial State
            Vector3 startPos = agent.transform.position;
            Quaternion startRot = agent.transform.rotation;
            
            // 2. Identify Goal
            Transform goalTransform = null;
            var agentTraining = agent.GetComponent<Agent_Training>();
            var agentGoalOnly = agent.GetComponent<Agent_GoalOnly_Training>();

            if (agentTraining != null)
            {
                if (!agentTraining.pdmMode)
                {
                    Debug.LogWarning($"[DiversityEvaluator] Enabled 'pdmMode' on {agent.name} to use GoalTransform.");
                    agentTraining.pdmMode = true;
                }
                if (agentTraining.GoalTransform != null) goalTransform = agentTraining.GoalTransform;
            }
            else if (agentGoalOnly != null)
            {
                if (!agentGoalOnly.pdmMode)
                {
                    Debug.LogWarning($"[DiversityEvaluator] Enabled 'pdmMode' on {agent.name} to use GoalTransform.");
                    agentGoalOnly.pdmMode = true;
                }
                if (agentGoalOnly.GoalTransform != null) goalTransform = agentGoalOnly.GoalTransform;
            }

            if (goalTransform == null)
            {
                Debug.LogWarning($"[DiversityEvaluator] Agent {agent.name} has no goal assigned (Checked Agent_Training, Agent_GoalOnly_Training). Skipping.");
                continue;
            }

            recordedPaths.Clear();

            // 3. Run Trials
            for (int t = 0; t < trialsPerEpisode; t++)
            {
                Debug.Log($"   -> Trial {t + 1}/{trialsPerEpisode}");
                
                // Reset Agent
                agent.SetActive(false); // Disable to reset internal state if any
                agent.transform.position = startPos;
                agent.transform.rotation = startRot;
                yield return null; // Wait a frame
                agent.SetActive(true);

                // Hard Reset Physics and Logic
                if (agentTraining != null)
                {
                    agentTraining.ForceReset(startPos, startRot, Vector3.zero);
                    agentTraining.OnEpisodeBegin();
                }
                else if (agentGoalOnly != null)
                {
                    agentGoalOnly.ForceReset(startPos, startRot, Vector3.zero);
                    agentGoalOnly.OnEpisodeBegin();
                }

                currentPath.Clear();

                // Wait for Goal Reached
                bool goalReached = false;
                float timeout = 60.0f; // Max duration per trial
                float timer = 0f;

                while (!goalReached && timer < timeout)
                {
                    yield return null; // Wait frame
                    timer += Time.deltaTime;

                    if (agent == null) break;

                    // Record Path (Root or Transform)
                    Vector3 pos = agent.transform.Find("Root") != null ? 
                                  agent.transform.Find("Root").position : 
                                  agent.transform.position;
                    
                    if (currentPath.Count == 0 || Vector3.Distance(currentPath.Last(), pos) > 0.1f)
                    {
                        currentPath.Add(pos);
                    }

                    // Check Goal
                    bool reached = false;
                    float threshold = 1.0f; // Default threshold

                    if (Vector3.Distance(agent.transform.position, goalTransform.position) < threshold)
                    {
                        reached = true;
                    }
                    
                    if (reached) goalReached = true;
                }

                // Save path
                recordedPaths.Add(new List<Vector3>(currentPath));
                
                // Small delay between trials
                yield return new WaitForSeconds(0.5f);
            }

            // 4. Calculate Diversity for this Agent
            float diversity = CalculateAveragePairwiseDTW(recordedPaths);
            agentDiversityScores.Add(diversity);
            Debug.Log($"[DiversityEvaluator] Agent {agent.name} Diversity (Avg DTW): {diversity:F4}");
        }

        // Final Report
        float finalAvgDiv = agentDiversityScores.Count > 0 ? agentDiversityScores.Average() : 0f;
        string report = $"Final Average Diversity Score (over {agentDiversityScores.Count} agents, {trialsPerEpisode} trials each): {finalAvgDiv:F4}";
        Debug.Log(report);
        File.WriteAllText(Path.Combine(Application.dataPath, outputFileName), report);

        isRunning = false;
        
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // --- DTW Calculation ---

    float CalculateAveragePairwiseDTW(List<List<Vector3>> paths)
    {
        if (paths.Count < 2) return 0f;

        float totalDist = 0f;
        int pairCount = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = i + 1; j < paths.Count; j++)
            {
                totalDist += CalculateDTW(paths[i], paths[j]);
                pairCount++;
            }
        }

        return pairCount > 0 ? totalDist / pairCount : 0f;
    }

    float CalculateDTW(List<Vector3> path1, List<Vector3> path2)
    {
        int n = path1.Count;
        int m = path2.Count;
        
        if (n == 0 || m == 0) return 0f;

        float[,] dtw = new float[n + 1, m + 1];

        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= m; j++)
            {
                dtw[i, j] = float.PositiveInfinity;
            }
        }
        dtw[0, 0] = 0f;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                float cost = Vector3.Distance(path1[i - 1], path2[j - 1]);
                dtw[i, j] = cost + Mathf.Min(dtw[i - 1, j],    // Insertion
                                             dtw[i, j - 1],    // Deletion
                                             dtw[i - 1, j - 1] // Match
                                            );
            }
        }

        return dtw[n, m];
    }
}