using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

[System.Serializable]
public class VisibleAgentInfo
{
    public int id;
    public Vector3 relativePosition;
    public float distance;
    public string zone;
    public string movementDirection; // "North", "South", "East", "West"
    public string movementPrediction; // "start moving", "stop moving", "keep moving", "stopped"
    public Vector3 agentVelocity;
    public string collisionPrediction; // "Collide", "Pass"


    public VisibleAgentInfo(int id, Vector3 relativePosition, float distance, string zone, string movementDirection, string movementPrediction, string collisionPrediction)
    {
        this.id = id;
        this.relativePosition = relativePosition;
        this.distance = distance;
        this.zone = zone;
        this.movementDirection = movementDirection;
        this.movementPrediction = movementPrediction;
        this.collisionPrediction = collisionPrediction;
    }
}

[RequireComponent(typeof(AgentController))]
public class AgentState : MonoBehaviour
{
    private Dictionary<int, List<Vector3>> agentPositionHistory = new();
    private const int PositionHistoryFrameCount = 5;

    // --- Inspector References ---
    [Header("Component References")]
    public Camera FoVCamera; // Assign the child camera with TrackViewCapture

    [Header("Settings")]
    public bool showStatusText = true; // Toggle for showing the 3D text
    public int TimeStepInterval = 1; // Interval for updating the state
    public float detectionRadius = 12f; // Radius to detect other agents
    public LayerMask detectionLayer; // Layers to consider for detection (agents, obstacles)

    // --- Public State Information ---
    [Header("Agent State (Live)")]
    public int AgentId;
    public int TimeStep;
    public Vector2 Action; // Velocity (x, z)
    public Vector3 GoalPosition;
    public float GoalDistance;
    public string GroupInfo = "x";
    public string FovImagePath;
    public List<VisibleAgentInfo> VisibleAgents = new();

    // --- Private Components ---
    private AgentController agentController;
    private AgentManager agentManager;
    public FoVCapture fovCapture;
    private TextMesh statusText;

    // --- Internal State ---
    private string csvPath;
    private StringBuilder csvBuilder = new();
    private int internalTimeStep = 0;
    public float lastSavedFrame = -1f; // 마지막으로 저장한 시점의 currentFrame
    public bool isSaveData = false;
    private Transform headTransform;
    private bool obstacleInLeft, obstacleInCenter, obstacleInRight;

    void Start()
    {
        // --- Get Core Components ---
        agentController = GetComponent<AgentController>();
        agentManager = FindObjectOfType<AgentManager>();

        if (FoVCamera != null)
        {
            fovCapture = FoVCamera.GetComponent<FoVCapture>();
        }

        if (agentController == null || agentManager == null)
        {
            Debug.LogError("AgentState script requires AgentController and AgentManager to be present.", this);
            enabled = false;
            return;
        }
        if (fovCapture == null)
        {
            Debug.LogWarning("FoVCapture component not found on the assigned FoVCamera. Image capture will be disabled.", this);
        }

        // --- Initialize State ---
        headTransform = transform.Find("Root/Hips/Spine_01/Spine_02/Spine_03/Neck/Head");
        if (agentController.positions != null && agentController.positions.Count > 0)
        {
            GoalPosition = agentController.positions.Last();
        }

        // --- Create 3D Text for Visualization (if enabled) ---
        if (showStatusText)
        {
            SetupStatusText();
        }

        // Assign group info using data from AgentManager
        AssignGroupInfo();
    }

    void SetupStatusText()
    {
        GameObject textObject = new("StatusText");
        textObject.transform.SetParent(transform, false);
        textObject.transform.SetLocalPositionAndRotation(
            new Vector3(0, 2.5f, 0),
            Quaternion.Euler(0, 180f, 0)
        );
        statusText = textObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.MiddleCenter;
        statusText.alignment = TextAlignment.Center;
        statusText.fontSize = 20;
        statusText.characterSize = 0.1f;
        statusText.color = Color.black;
    }

    string PredictCollision(Vector3 myPos, Vector3 myVel, Vector3 otherPos, Vector3 otherVel, float agentRadius, float maxPredictionTime)
    {
        Vector3 relativePosition = otherPos - myPos;
        Vector3 relativeVelocity = otherVel - myVel;

        if (relativeVelocity.sqrMagnitude < 0.001f)
        {
            return "Pass";
        }

        float timeToClosestApproach = -Vector3.Dot(relativePosition, relativeVelocity) / relativeVelocity.sqrMagnitude;

        if (timeToClosestApproach < 0)
        {
            return "Pass";
        }

        // Ignore collisions too far in the future
        if (timeToClosestApproach > maxPredictionTime)
        {
            return "Pass";
        }

        float distanceAtClosestApproach = (relativePosition + relativeVelocity * timeToClosestApproach).magnitude;

        if (distanceAtClosestApproach < (agentRadius * 2))
        {
            return "Collide";
        }
        else
        {
            return "Pass";
        }
    }

    public void DetectVisibleAgents()
    {
        VisibleAgents.Clear();
        obstacleInLeft = obstacleInCenter = obstacleInRight = false;

        if (FoVCamera == null) return;

        Collider[] hitColliders = Physics.OverlapSphere(transform.position, detectionRadius, detectionLayer);

        if (hitColliders.Length <= 1) return;

        Vector3 eyeLevelOffset = new(0, 1.7f, 0);

        foreach (var hitCollider in hitColliders)
        {
            if (hitCollider.gameObject == gameObject) continue;

            // Check if this collider is an AgentState
            bool isAgent = hitCollider.TryGetComponent<AgentState>(out var otherAgent);

            Vector3 rayOrigin = FoVCamera.transform.position;
            Vector3 targetPoint = hitCollider.bounds.center;
            Vector3 directionToTarget = targetPoint - rayOrigin;

            float angle = Vector3.Angle(FoVCamera.transform.forward, directionToTarget);

            if (angle < FoVCamera.fieldOfView / 2f)
            {
                float signedAngle = Vector3.SignedAngle(FoVCamera.transform.forward, directionToTarget, FoVCamera.transform.up);
                float zoneBoundary = FoVCamera.fieldOfView / 6f;
                string zone; // This 'zone' variable is used for both agents and obstacles

                if (signedAngle >= -zoneBoundary && signedAngle <= zoneBoundary)
                {
                    zone = "Center";
                    if (!isAgent) obstacleInCenter = true;
                }
                else if (signedAngle < -zoneBoundary)
                {
                    zone = "Left";
                    if (!isAgent) obstacleInLeft = true;
                }
                else
                {
                    zone = "Right";
                    if (!isAgent) obstacleInRight = true;
                }

                if (isAgent)
                {
                    float agentDistance = Vector3.Distance(transform.position, otherAgent.transform.position);

                    Vector3 agentTargetPoint = otherAgent.transform.position + eyeLevelOffset;
                    Vector3 directionToAgent = agentTargetPoint - rayOrigin;
                    float raycastDistance = directionToAgent.magnitude;

                    bool isVisible = false;
                    if (Physics.Raycast(rayOrigin, directionToAgent.normalized, out RaycastHit hit, raycastDistance, detectionLayer))
                    {
                        if (hit.transform.IsChildOf(otherAgent.transform) || hit.transform == otherAgent.transform)
                        {
                            isVisible = true;
                            Debug.DrawRay(rayOrigin, directionToAgent.normalized * raycastDistance, Color.green);
                        }
                        else
                        {
                            Debug.DrawRay(rayOrigin, directionToAgent.normalized * hit.distance, Color.red);
                        }
                    }
                    else
                    {
                        isVisible = true;
                        Debug.DrawRay(rayOrigin, directionToAgent.normalized * raycastDistance, Color.green);
                    }

                    if (isVisible)
                    {
                        string movementDirection = "Unknown";
                        string movementPrediction = "Unknown";
                        string collisionPrediction = "None";
                        Vector3 agentVelocity = Vector3.zero;

                        if (!agentPositionHistory.ContainsKey(otherAgent.AgentId))
                        {
                            agentPositionHistory[otherAgent.AgentId] = new List<Vector3>();
                        }
                        List<Vector3> history = agentPositionHistory[otherAgent.AgentId];
                        history.Add(otherAgent.transform.position);
                        if (history.Count > PositionHistoryFrameCount)
                        {
                            history.RemoveAt(0);
                        }

                        if (history.Count == PositionHistoryFrameCount)
                        {
                            Vector3 startPosition = history[0];
                            Vector3 endPosition = history[^1];
                            Vector3 movementVector = endPosition - startPosition;
                            agentVelocity = movementVector / (PositionHistoryFrameCount * Time.fixedDeltaTime);

                            Vector3 relativeMovement = transform.InverseTransformDirection(movementVector.normalized);
                            if (Mathf.Abs(relativeMovement.x) > Mathf.Abs(relativeMovement.z))
                            {
                                movementDirection = relativeMovement.x > 0 ? "East" : "West";
                            }
                            else
                            {
                                movementDirection = relativeMovement.z > 0 ? "North" : "South";
                            }

                            float speed = agentVelocity.magnitude;
                            const float stopThreshold = 0.1f;

                            Vector3 previousMovement = (history[^2] - history[0]) / ((PositionHistoryFrameCount - 1) * Time.fixedDeltaTime);
                            float previousSpeed = previousMovement.magnitude;

                            if (speed < stopThreshold && previousSpeed >= stopThreshold)
                            {
                                movementPrediction = "stop moving";
                            }
                            else if (speed >= stopThreshold && previousSpeed < stopThreshold)
                            {
                                movementPrediction = "start moving";
                            }
                            else if (speed >= stopThreshold)
                            {
                                movementPrediction = "keep moving";
                            }
                            else
                            {
                                movementPrediction = "stopped";
                            }

                            Vector3 myVelocity = new(Action.x, 0, Action.y);
                            collisionPrediction = PredictCollision(transform.position, myVelocity, otherAgent.transform.position, agentVelocity, 0.22f, 5.0f);
                        }
                        else if (history.Count > 1) // Handle partial history for early predictions
                        {
                            Vector3 startPosition = history[0];
                            Vector3 endPosition = history[^1];
                            Vector3 movementVector = endPosition - startPosition;
                            agentVelocity = movementVector / ((history.Count - 1) * Time.fixedDeltaTime);

                            // --- Calculate movementDirection ---
                            Vector3 relativeMovement = transform.InverseTransformDirection(movementVector.normalized);
                            if (Mathf.Abs(relativeMovement.x) > Mathf.Abs(relativeMovement.z))
                            {
                                movementDirection = relativeMovement.x > 0 ? "East" : "West";
                            }
                            else
                            {
                                movementDirection = relativeMovement.z > 0 ? "North" : "South";
                            }

                            // --- Calculate movementPrediction (simplified) ---
                            float speed = agentVelocity.magnitude;
                            const float stopThreshold = 0.1f;
                            if (speed >= stopThreshold)
                            {
                                movementPrediction = "keep moving";
                            }
                            else
                            {
                                movementPrediction = "stopped";
                            }

                            // --- Calculate collisionPrediction ---
                            Vector3 myVelocity = new(Action.x, 0, Action.y);
                            collisionPrediction = PredictCollision(transform.position, myVelocity, otherAgent.transform.position, agentVelocity, 0.22f, 5.0f);
                        }

                        Vector3 relativePosition = transform.InverseTransformPoint(otherAgent.transform.position);
                        VisibleAgents.Add(new VisibleAgentInfo(otherAgent.AgentId, relativePosition, agentDistance, zone, movementDirection, movementPrediction, collisionPrediction));
                    }
                }
            }
        }
    }

    public void RecordState(Vector2 action, int currentFrame)
    {
        TimeStep = internalTimeStep++;
        lastSavedFrame = currentFrame;

        Action = action;

        GoalDistance = CalculateRemainingDistance();

        if (showStatusText)
        {
            UpdateStatusText();
        }

        if (isSaveData && TimeStep > 0)
        {
            if (string.IsNullOrEmpty(csvPath))
            {
                string directory = "Assets/Zara01_State";
                Directory.CreateDirectory(directory);
                csvPath = Path.Combine(directory, $"zara01_{AgentId}.csv");
                string header = "TimeStep,Action_X,Action_Z," +
                                "Position_X,Position_Y,Position_Z," +
                                "BodyRot_X,BodyRot_Y,BodyRot_Z,BodyRot_W," +
                                "HeadRot_X,HeadRot_Y,HeadRot_Z,HeadRot_W," +
                                "GoalPosition_X,GoalPosition_Y,GoalPosition_Z,GoalDistance," +
                                "GroupInfo,FovImagePath,VisibleAgents\n";
                File.WriteAllText(csvPath, header);
            }

            DetectVisibleAgents();

            if (fovCapture != null)
            {
                fovCapture.Capture(AgentId, TimeStep);
                FovImagePath = fovCapture.LastSavedImagePath;
            }
            else
            {
                FovImagePath = "N/A";
            }
            WriteStateToCsv();
        }
    }

    void UpdateStatusText()
    {
        if (statusText == null) return;

        if (statusText.gameObject.activeSelf != showStatusText)
        {
            statusText.gameObject.SetActive(showStatusText);
        }

        if (showStatusText)
        {
            string labelText = $"ID: {AgentId}\n" +
                                $"Time: {TimeStep}\n" +
                                $"Action: ({Action.x:F2}, {Action.y:F2})\n" +
                                $"Pos: ({transform.position.x:F2}, {transform.position.z:F2})\n" +
                                $"Goal Dist: {GoalDistance:F2}\n" +
                                $"Group: {GroupInfo}";
            statusText.text = labelText;
        }
    }

    void AssignGroupInfo()
    {
        if (AgentManager.groupData != null && AgentManager.groupData.ContainsKey(AgentId))
        {
            GroupInfo = string.Join(";", AgentManager.groupData[AgentId].Where(id => id != AgentId));
        }
        else
        {
            GroupInfo = "x";
        }
    }

    float CalculateRemainingDistance()

    {

        return Vector3.Distance(transform.position, GoalPosition);

    }



    private string GetDistanceString(float distance)

    {

        if (distance < 3f) return "Very Close";

        if (distance < 6f) return "Close";

        if (distance < 9f) return "Far";

        return "Very Far";

    }



    private string GenerateVisibleAgentsSentence()
    {
        if ((VisibleAgents == null || VisibleAgents.Count == 0) && !obstacleInLeft && !obstacleInCenter && !obstacleInRight)
        {
            return "There are no people or obstacles around.";
        }

        var agentsByZone = VisibleAgents.GroupBy(a => a.zone).ToDictionary(g => g.Key, g => g.ToList());
        var sentenceParts = new List<string>();

        if (agentsByZone.TryGetValue("Center", out var centerAgents) && centerAgents.Count > 0)
        {
            var description = new StringBuilder("There ");
            if (centerAgents.Count > 1)
                description.Append("are several people ahead of me in the Center: ");
            else
                description.Append("is one person ahead of me in the Center: ");

            var agentDescriptions = centerAgents.Select(agent =>
            {
                string distanceString = GetDistanceString(agent.distance);
                return $"- One at a {distanceString} distance, moving {agent.movementDirection}, predicting {agent.collisionPrediction.ToLower()}";
            });
            description.Append(string.Join(", ", agentDescriptions));
            description.Append(".");
            sentenceParts.Add(description.ToString());
        }

        if (agentsByZone.TryGetValue("Left", out var leftAgents) && leftAgents.Count > 0)
        {
            var description = new StringBuilder("To my Left, there ");
            if (leftAgents.Count > 1)
                description.Append("are several people: ");
            else
                description.Append("is one person: ");

            var agentDescriptions = leftAgents.Select(agent =>
            {
                string distanceString = GetDistanceString(agent.distance);
                return $"- One at a {distanceString} distance, moving {agent.movementDirection}, predicting {agent.collisionPrediction.ToLower()}";
            });
            description.Append(string.Join(", ", agentDescriptions));
            description.Append(".");
            sentenceParts.Add(description.ToString());
        }

        if (agentsByZone.TryGetValue("Right", out var rightAgents) && rightAgents.Count > 0)
        {
            var description = new StringBuilder("To my Right, there ");
            if (rightAgents.Count > 1)
                description.Append("are several people: ");
            else
                description.Append("is one person: ");

            var agentDescriptions = rightAgents.Select(agent =>
            {
                string distanceString = GetDistanceString(agent.distance);
                return $"- One at a {distanceString} distance, moving {agent.movementDirection}, predicting {agent.collisionPrediction.ToLower()}";
            });
            description.Append(string.Join(", ", agentDescriptions));
            description.Append(".");
            sentenceParts.Add(description.ToString());
        }

        var obstacleZones = new List<string>();
        if (obstacleInLeft) obstacleZones.Add("Left");
        if (obstacleInCenter) obstacleZones.Add("Center");
        if (obstacleInRight) obstacleZones.Add("Right");

        if (obstacleZones.Count > 0)
        {
            sentenceParts.Add($"There is also an obstacle on the {string.Join("/", obstacleZones)}.");
        }

        return string.Join(" ", sentenceParts);
    }

    void WriteStateToCsv()
    {
        csvBuilder.Clear();
        // TimeStep
        csvBuilder.Append(TimeStep).Append(",");
        // Action
        csvBuilder.Append(Action.x.ToString("F4")).Append(",");
        csvBuilder.Append(Action.y.ToString("F4")).Append(",");
        // Position
        csvBuilder.Append(transform.position.x.ToString("F4")).Append(",");
        csvBuilder.Append(transform.position.y.ToString("F4")).Append(",");
        csvBuilder.Append(transform.position.z.ToString("F4")).Append(",");
        // Body Rotation
        csvBuilder.Append(transform.rotation.x.ToString("F4")).Append(",");
        csvBuilder.Append(transform.rotation.y.ToString("F4")).Append(",");
        csvBuilder.Append(transform.rotation.z.ToString("F4")).Append(",");
        csvBuilder.Append(transform.rotation.w.ToString("F4")).Append(",");
        // Head Rotation
        if (headTransform != null)
        {
            Quaternion headRot = headTransform.rotation;
            csvBuilder.Append(headRot.x.ToString("F4")).Append(",");
            csvBuilder.Append(headRot.y.ToString("F4")).Append(",");
            csvBuilder.Append(headRot.z.ToString("F4")).Append(",");
            csvBuilder.Append(headRot.w.ToString("F4")).Append(",");
        }
        else
        {
            csvBuilder.Append("0,0,0,1,"); // Default value (Quaternion.identity)
        }
        // GoalPosition
        csvBuilder.Append(GoalPosition.x.ToString("F2")).Append(",");
        csvBuilder.Append(GoalPosition.y.ToString("F2")).Append(",");
        csvBuilder.Append(GoalPosition.z.ToString("F2")).Append(",");
        // GoalDistance
        csvBuilder.Append(GoalDistance.ToString("F2")).Append(",");
        // GroupInfo
        csvBuilder.Append(GroupInfo).Append(",");
        // FovImagePath
        csvBuilder.Append(FovImagePath ?? "N/A").Append(",");

        // VisibleAgents in sentence format
        string sentence = GenerateVisibleAgentsSentence();
        csvBuilder.Append("\"").Append(sentence).Append("\"");

        csvBuilder.Append("\n");

        File.AppendAllText(csvPath, csvBuilder.ToString());
    }



}