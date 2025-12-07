using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIPackage
{
    /// <summary>
    /// AI_Stats extends Stats with NPC-specific values (movement, vision, weapon, etc).
    /// This version performs a safe, defensive assignment of the GameObject layer in Awake()
    /// to avoid exceptions during Instantiate() when an invalid layer name or index is used.
    /// </summary>
    public class AI_Stats : Stats
    {
        public float walkSpeed = 2;
        public float runSpeed = 4;

        public float visionRange = 10;
        public float visionAngle = 25f;
        
        public enum Weapon { melee };
        public Weapon assignedWeapon;

        public Job job;
        public Gender gender;

        public List<string> enemies;
        public List<string> protects;

        public GameObject weapon;

        // Make the layer name configurable in the inspector; default to "Npc"
        [Tooltip("Name of the layer to assign to instantiated NPCs. If the layer name does not exist, Default (0) will be used.")]
        public string layerName = "Npc";

        protected override void Awake()
        {
            base.Awake();

            // Defensive layer assignment:
            // - Try to resolve the layer by name using LayerMask.NameToLayer
            // - If the layer name is not defined in the project (NameToLayer returns -1),
            //   fall back to layer 0 (Default) and log a warning.
            // - Clamp the final index to [0,31] for extra safety.
            try
            {
                int idx = LayerMask.NameToLayer(layerName);

                if (idx < 0)
                {
                    // Requested layer name not found in project settings
                    Debug.LogWarning($"AI_Stats: Layer named '{layerName}' not found. Falling back to Default layer (0). " +
                                     "You can create this layer in Edit > Project Settings > Tags and Layers if desired.");
                    idx = 0; // Default layer
                }

                // Clamp for extra safety (Unity supports layers 0..31)
                idx = Mathf.Clamp(idx, 0, 31);

                gameObject.layer = idx;
            }
            catch (Exception ex)
            {
                // Very defensive: if any unexpected exception occurs, fallback to layer 0 and log.
                Debug.LogWarning($"AI_Stats: Exception when setting layer '{layerName}'. Falling back to Default layer (0). Exception: {ex.Message}");
                try
                {
                    gameObject.layer = 0;
                }
                catch
                {
                    // Swallow any exception here - setting layer should not crash instantiation.
                }
            }
        }
    }
}
