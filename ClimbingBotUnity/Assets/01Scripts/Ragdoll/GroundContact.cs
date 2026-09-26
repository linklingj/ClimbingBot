using UnityEngine;
using Unity.MLAgents;

/// <summary>
/// This class contains logic for locomotion agents with joints which might make contact with the ground.
/// By attaching this as a component to those joints, their contact with the ground can be used as either
/// an observation for that agent, and/or a means of punishing the agent for making undesirable contact.
/// </summary>
[DisallowMultipleComponent]
public class GroundContact : MonoBehaviour
{
    [HideInInspector] public Agent agent;

    // Off in ClimberRagdoll.prefab, and it has to stay off. Agent.EndEpisode() resets synchronously,
    // so calling it from OnCollisionEnter runs the whole episode reset inside PhysX's contact
    // callback -- where DestroyImmediate is illegal, which is every reset this project does.
    // ClimbingAgent ends episodes from FixedUpdate instead (target reached / hips dropped / MaxStep).
    [Header("Ground Check")] public bool agentDoneOnGroundContact; // Whether to reset agent on ground contact.
    // Also off: this pays with SetReward, which *overwrites* the step's reward rather than adding to
    // it, so one contact erases ClimbingAgent's progress shaping for that step.
    public bool penalizeGroundContact; // Whether to penalize on contact.
    public float groundContactPenalty; // Penalty amount (ex: -1).
    public bool touchingGround;
    const string k_Ground = "ground"; // Tag of ground object.

    /// <summary>
    /// Check for collision with ground, and optionally penalize agent.
    /// </summary>
    void OnCollisionEnter(Collision col)
    {
        if (!col.transform.CompareTag(k_Ground))
        {
            return;
        }

        touchingGround = true;

        // The ragdoll runs without a controller too (manual testing now, AR playback later),
        // so reward and episode signalling only apply when an agent is actually attached.
        if (agent == null)
        {
            return;
        }

        if (penalizeGroundContact)
        {
            agent.SetReward(groundContactPenalty);
        }

        if (agentDoneOnGroundContact)
        {
            agent.EndEpisode();
        }
    }

    /// <summary>
    /// Check for end of ground collision and reset flag appropriately.
    /// </summary>
    void OnCollisionExit(Collision other)
    {
        if (other.transform.CompareTag(k_Ground))
        {
            touchingGround = false;
        }
    }
}