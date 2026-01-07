using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;

public class Visuals : MonoBehaviour
{
    Color bodyColor;
    TrailRenderer trail;
    MeshRenderer body;
    Monitor_Training manager;
    Agent_Training agent;
    Agent_GoalOnly_Training agentGoalOnly;

    // Start is called before the first frame update
    void Start()
    {
        GameObject env = GameObject.Find("Environment");
        if (env != null)
        {
            this.manager = env.GetComponent<Monitor_Training>();
        }

        this.agent = this.gameObject.GetComponent<Agent_Training>();
        this.agentGoalOnly = this.gameObject.GetComponent<Agent_GoalOnly_Training>();
        this.trail = transform.GetComponent<TrailRenderer>();
        this.body = transform.GetChild(0).GetComponent<MeshRenderer>();
        this.bodyColor = this.body.material.color;
    }

    // Update is called once per frame
    void Update()
    {
        if (this.manager == null) return;

        if (this.manager.coloredWeights)
        {
            float normalized_collision = 0;
            float normalized_goal = 0;
            float normalized_group = 0;
            float normalized_interact = 0;

            if (this.agent != null)
            {
                normalized_collision = this.agent.normalizeInRange(this.agent.collWeight, this.manager.collMin, this.manager.collMax);
                normalized_goal = this.agent.normalizeInRange(this.agent.goalWeight, this.manager.goalMin, this.manager.goalMax);
                normalized_group = this.agent.normalizeInRange(this.agent.groupWeight, this.manager.groupMin, this.manager.groupMax);
                normalized_interact = this.agent.normalizeInRange(this.agent.interWeight, this.manager.interMin, this.manager.interMax);
            }
            else if (this.agentGoalOnly != null)
            {
                normalized_collision = this.agentGoalOnly.normalizeInRange(this.agentGoalOnly.collWeight, this.manager.collMin, this.manager.collMax);
                normalized_goal = this.agentGoalOnly.normalizeInRange(this.agentGoalOnly.goalWeight, this.manager.goalMin, this.manager.goalMax);
                normalized_group = this.agentGoalOnly.normalizeInRange(this.agentGoalOnly.groupWeight, this.manager.groupMin, this.manager.groupMax);
                normalized_interact = this.agentGoalOnly.normalizeInRange(this.agentGoalOnly.interWeight, this.manager.interMin, this.manager.interMax);
            }

            Color color = new Color(normalized_goal, normalized_interact, normalized_group, Mathf.Clamp(normalized_collision * 0.5f + 0.5f, 0.5f, 1f));
            this.body.material.color = color;
            this.trail.startColor = color;
            this.trail.endColor = color;
        }
        else
        {
            this.body.material.color = this.bodyColor;
            this.trail.startColor = this.bodyColor;
            this.trail.endColor = this.bodyColor;
        }
    }
}
