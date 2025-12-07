using UnityEngine;
using System;

public class ExperimentManager : MonoBehaviour
{
    public event Action OnTrialStart;
    public event Action OnTrialEnd;

    public AgentProfile currentProfile;
    public SpawnerManager spawner;
    public InstructionPanel panel;

    public void StartTrial()
    {
        // configure spawner with the profile
        spawner.defaultProfile = currentProfile;
        OnTrialStart?.Invoke();
        panel.ShowInstruction("Trial started. Follow instructions...");
    }

    public void EndTrial()
    {
        OnTrialEnd?.Invoke();
        panel.ShowInstruction("Trial ended. Thank you.");
    }
}

public class InstructionPanel : MonoBehaviour
{
    // very small VR-friendly UI controller. Implement canvas text update or world-space text.
    public TMPro.TextMeshProUGUI text;

    public void ShowInstruction(string msg)
    {
        if (text != null) text.text = msg;
        // additionally show/hide panel or animate
    }
}
