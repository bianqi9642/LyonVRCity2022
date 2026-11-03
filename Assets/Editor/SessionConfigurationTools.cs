// SessionConfigurationTools.cs
// Editor helpers for the external session configuration (session_config.json). Read-only: nothing here creates, edits or deletes a configuration file or
// touches the scene.
//   Tools > Session Configuration > Reveal External Config Folder   - opens Application.persistentDataPath (where session_config.json must be placed).
//   Tools > Session Configuration > Validate External session_config.json - runs the same parser/validator as the runtime against the real file and the
//                                                                    group counts of the open scene, and shows the result (no state is applied).
// All comments in English per project convention.

using System.IO;
using UnityEditor;
using UnityEngine;

public static class SessionConfigurationTools
{
    const string Menu = "Tools/Session Configuration/";

    static string ResolvePath(out string fileName)
    {
        var sc = Object.FindObjectOfType<ExternalSessionController>(true);
        fileName = sc != null && !string.IsNullOrEmpty(sc.externalFileName) ? sc.externalFileName : "session_config.json";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    [MenuItem(Menu + "Reveal External Config Folder")]
    static void Reveal()
    {
        string file = ResolvePath(out _);
        string dir = Application.persistentDataPath;
        Debug.Log("[SessionConfigurationTools] External configuration folder: " + dir + "\nExpected file: " + file + " (exists: " + File.Exists(file) + ")");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);   // the application data folder itself; no configuration file is created
        EditorUtility.RevealInFinder(File.Exists(file) ? file : dir);
    }

    [MenuItem(Menu + "Validate External session_config.json")]
    static void Validate()
    {
        string path = ResolvePath(out _);
        var limits = new SessionConfigCore.Limits { Part3GroupCount = ExperimentManager.Part3GroupCount };
        var em = Object.FindObjectOfType<ExperimentManager>(true);
        if (em != null)
        {
            limits.Part2GroupCount = em.part2Groups != null ? em.part2Groups.Length : 0;
            limits.BaselinePart3Destination1Max = em.part3Destination1MaxSeconds;
            limits.BaselinePart3TotalMax = em.part3TotalMaxSeconds;
        }
        SessionConfigSnapshot snap = SessionConfigCore.ResolveExternalFromFile(path, limits, AgentProfileSnapshot.Fnv1a64Hex);
        string extra = null;
        if (snap.IsValid && snap.PartNumber == 3 && ExperimentManager.TryGetPart3GroupZones(snap.Part3GroupIndex, out string st, out string d1, out string d2))
            extra = "Start Zone: " + st + "\nD1 Zone: " + d1 + "\nD2 Zone: " + d2;
        string text = SessionConfigCore.FormatSummary(snap, extra == null ? null : extra.Split('\n'))
                      + (em == null ? "\n\n(No ExperimentManager in the open scene: the Part 2 group count could not be checked.)" : "");
        if (snap.IsValid) Debug.Log("[SessionConfigurationTools] " + text); else Debug.LogError("[SessionConfigurationTools] " + text);
        EditorUtility.DisplayDialog("Session configuration: " + (snap.IsValid ? "VALID" : "INVALID"), text, "OK");
    }
}
