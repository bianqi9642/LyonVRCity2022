using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using AIPackage;
using Dialogue_Package;

namespace Dialogue_Package
{
    public class DialogueManager : MonoBehaviour
    {
        public Sentence currentSentence;
        public Sentence defaultSentence;
        public Transform player;

        NpcAI npcAI;
        CombatAI combatAI;
        NavMeshAgent agent;

        Rotate rotate;
        AimRig aimRig;

        // UnityEvent must be serializable type if you want to assign in inspector.
        public UnityEvent<Sentence> onDialogue_Close;
        public bool turnNpc = true;

        void Awake()
        {
            npcAI = GetComponent<NpcAI>();
            combatAI = GetComponent<CombatAI>();
            agent = GetComponent<NavMeshAgent>();

            rotate = GetComponent<Rotate>();
            aimRig = GetComponent<AimRig>();
        }

        private void OnEnable()
        {
            // Defensive checks
            if (npcAI != null) npcAI.enabled = false;
            if (combatAI != null) combatAI.enabled = false;
            if (agent != null) agent.SetDestination(transform.position);

            if (rotate != null && player != null)
            {
                rotate.RotateTo(player);
            }
            else if (rotate == null)
            {
                Debug.LogWarning("DialogueManager: Rotate component missing.");
            }
            else
            {
                Debug.LogWarning("DialogueManager: player Transform is null.");
            }

            if (aimRig != null && player != null)
            {
                aimRig.RigToTarget(player);
            }
            else if (aimRig == null)
            {
                Debug.LogWarning("DialogueManager: AimRig component missing.");
            }
        }

        protected virtual void OnDisable()
        {
            if (rotate != null) rotate.enabled = false;
            if (aimRig != null) aimRig.RemoveRig();

            EnableAI();

            // Safe-invoke to avoid NullReferenceException if no listeners are attached
            onDialogue_Close?.Invoke(currentSentence);

            // Reset to default sentence if available
            if (defaultSentence != null)
                currentSentence = defaultSentence;
        }

        void EnableAI()
        {
            if (turnNpc)
            {
                if (npcAI != null) npcAI.enabled = true;
            }
            else
            {
                if (combatAI != null) combatAI.enabled = true;
            }
        }

        public string GetNpcText()
        {
            return currentSentence != null ? currentSentence.npcText : null;
        }

        public Sentence GetNextSentence()
        {
            return currentSentence != null ? currentSentence.nextSentence : null;
        }

        public List<Sentence> GetListOfChoices()
        {
            return currentSentence != null ? currentSentence.choices : null;
        }

        public void SetQuestSentence(Sentence sentence)
        {
            currentSentence = sentence;
            defaultSentence = sentence;
        }
    }
}
