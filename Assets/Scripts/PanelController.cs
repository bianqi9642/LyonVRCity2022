using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// PanelController:
/// - Supports either UnityEngine.UI.Text (legacy) or TextMeshProUGUI for the message.
/// - When Setup(...) is called, it will only overwrite the existing message IF
///   the provided message string is not null or empty. This allows template text
///   (set in the inspector) to persist if UIManager wants to use it.
/// - The confirm/cancel buttons are wired at runtime; by default the instance is destroyed after confirm.
/// </summary>
public class PanelController : MonoBehaviour
{
    [Header("Message Text (assign ONE of these)")]
    [Tooltip("Standard Unity UI Text (legacy UI). Leave null if using TextMeshPro.")]
    public Text messageTextUI;

    [Tooltip("TextMeshProUGUI message text. Leave null if using standard UI Text.")]
    public TextMeshProUGUI messageTextTMP;

    [Header("Buttons")]
    [Tooltip("Confirm / Start / Teleport button.")]
    public Button confirmButton;

    [Tooltip("Optional cancel button.")]
    public Button cancelButton;

    // runtime callbacks
    private Action onConfirmAction;
    private Action onCancelAction;

    /// <summary>
    /// Set the panel message and wire confirm / cancel callbacks.
    /// IMPORTANT: If 'message' is null or empty, the current (designer-set) text is kept.
    /// </summary>
    public void Setup(string message, Action onConfirm, bool showCancel = false)
    {
        // Only overwrite existing text if a non-empty message is provided.
        if (!string.IsNullOrEmpty(message))
        {
            if (messageTextUI != null)
            {
                messageTextUI.text = message;
            }
            else if (messageTextTMP != null)
            {
                messageTextTMP.text = message;
            }
            else
            {
                Debug.LogWarning("PanelController: No message text component assigned (Text or TextMeshProUGUI).");
            }
        }
        // else: keep whatever text is currently in the template (inspector text)

        onConfirmAction = onConfirm;

        // Wire confirm button
        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveAllListeners();
            confirmButton.onClick.AddListener(() =>
            {
                onConfirmAction?.Invoke();
                Destroy(gameObject);
            });
        }
        else
        {
            // If there is no confirm button, immediately invoke confirm
            onConfirmAction?.Invoke();
            Destroy(gameObject);
        }

        // Wire optional cancel button
        if (cancelButton != null)
        {
            cancelButton.gameObject.SetActive(showCancel);
            cancelButton.onClick.RemoveAllListeners();
            cancelButton.onClick.AddListener(() =>
            {
                onCancelAction?.Invoke();
                Destroy(gameObject);
            });
        }
    }

    /// <summary>
    /// Programmatically trigger confirm (used by other scripts or XR events).
    /// </summary>
    public void InvokeConfirm()
    {
        onConfirmAction?.Invoke();
        Destroy(gameObject);
    }
}
