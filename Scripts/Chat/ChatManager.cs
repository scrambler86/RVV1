using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using FishNet.Object;
using System.Text;
using System;
using System.Collections.Generic;

/// <summary>
/// Chat locale + broadcast rete via FishNet.
/// Nota: questa versione parla con PlayerControllerCore (nuovo controller locale),
/// non con PlayerControllerFishNet.
/// </summary>
public class ChatManager : NetworkBehaviour
{
    [Header("Chat References")]
    public GameObject inputFieldObject;
    public TMP_InputField inputField;
    public Transform messageContent;
    public GameObject messagePrefab;

    public static bool IsChatWriting = false;

    private string playerName = "Unknown";

    // scrollback / history ui
    private int maxMessages = 50;
    private readonly List<GameObject> spawnedMessages = new List<GameObject>();

    // stato apertura/chiusura chat
    private bool chatOpen = false;

    // anti-spam
    private readonly Queue<float> messageTimestamps = new Queue<float>();
    private bool isMuted = false;
    private bool warningGiven = false;
    private float muteEndTime = 0f;
    private float lastSpamTime = -10f;

    // tuning anti-spam
    private int maxChars = 200;
    private float spamWindowSeconds = 8f;
    private int warningThreshold = 5;
    private int muteThreshold = 10;
    private float muteDurationSeconds = 30f;

    private void Start()
    {
        // nome visibile in chat
        playerName = "Player_" + UnityEngine.Random.Range(1000, 9999);

        // chat nascosta all'inizio
        if (inputFieldObject != null)
            inputFieldObject.SetActive(false);

        // callback quando premi invio dentro la TMP_InputField
        if (inputField != null)
            inputField.onSubmit.AddListener(OnSubmit);
    }

    private void Update()
    {
        // toggle chat con TAB (cambia pure tasto se vuoi)
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            ToggleChat();
            return;
        }

        // se sto scrivendo in chat, non faccio altro (es: niente input di gioco)
        if (IsChatWriting)
            return;
    }

    /// <summary>
    /// Apre/chiude la chat e gestisce anche il blocco movimento del player locale.
    /// </summary>
    private void ToggleChat()
    {
        chatOpen = !chatOpen;

        if (inputFieldObject != null)
            inputFieldObject.SetActive(chatOpen);

        if (chatOpen)
        {
            // --- apro la chat ---
            if (inputField != null)
            {
                inputField.text = "";
                inputField.ActivateInputField();
                inputField.Select();
            }

            IsChatWriting = true;

            // blocco il movimento locale subito
            PlayerControllerCore localPlayer = FindObjectOfType<PlayerControllerCore>();
            if (localPlayer != null)
                localPlayer.StopMovement();
        }
        else
        {
            // --- chiudo la chat ---
            if (inputField != null)
                inputField.DeactivateInputField();

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);

            IsChatWriting = false;
        }
    }

    /// <summary>
    /// Chiamata quando premi invio nella TMP_InputField.
    /// </summary>
    private void OnSubmit(string text)
    {
        // se la chat è chiusa o vuota → chiudo/esco
        if (!chatOpen || string.IsNullOrWhiteSpace(text))
        {
            ToggleChat();
            return;
        }

        // mutato per spam?
        if (isMuted)
        {
            string muteMsg = $"You are muted. You can chat again in {(int)(muteEndTime - Time.time)} seconds.";
            LocalSystemMessage(muteMsg);

            // reset campo input ma resto in chat aperta
            if (inputField != null)
            {
                inputField.text = "";
                inputField.ActivateInputField();
                inputField.Select();
            }
            return;
        }

        // sanificazione messaggio
        text = FilterMessage(text);
        if (text.Length > maxChars)
            text = text.Substring(0, maxChars);

        // controllo anti-spam
        if (!CheckSpam())
        {
            if (inputField != null)
            {
                inputField.text = "";
                inputField.ActivateInputField();
                inputField.Select();
            }
            return;
        }

        // format finale
        string time = DateTime.Now.ToString("HH:mm");
        string fullMessage = $"[{time}] <b>{playerName}</b>: {text}";

        // invia al server come byte[]
        byte[] encodedMessage = Encoding.UTF8.GetBytes(fullMessage);
        SendMessageToServer(encodedMessage);

        // pulisco il campo ma tengo aperta la chat
        if (inputField != null)
        {
            inputField.text = "";
            inputField.ActivateInputField();
            inputField.Select();
        }
    }

    // =========================
    // ANTI-SPAM / MUTE LOGIC
    // =========================
    private bool CheckSpam()
    {
        float now = Time.time;
        messageTimestamps.Enqueue(now);

        // butta via timestamp vecchi fuori finestra
        while (messageTimestamps.Count > 0 &&
               now - messageTimestamps.Peek() > spamWindowSeconds)
        {
            messageTimestamps.Dequeue();
        }

        // resetta warning se è passato abbastanza tempo dall'ultimo picco
        if (now - lastSpamTime > spamWindowSeconds)
            warningGiven = false;

        // se supero soglia mute → muto
        if (messageTimestamps.Count >= muteThreshold)
        {
            MutePlayer();
            return false;
        }
        // se supero soglia warning → avviso
        else if (messageTimestamps.Count >= warningThreshold && !warningGiven)
        {
            warningGiven = true;
            lastSpamTime = now;
            LocalSystemMessage("<color=red><b>Warning: You are spamming! You will be muted if you continue.</b></color>");
        }

        return true;
    }

    private void MutePlayer()
    {
        isMuted = true;
        muteEndTime = Time.time + muteDurationSeconds;
        LocalSystemMessage("<color=red><b>You have been muted for 30 seconds due to spamming.</b></color>");
        Invoke(nameof(UnmutePlayer), muteDurationSeconds);
    }

    private void UnmutePlayer()
    {
        isMuted = false;
        warningGiven = false;
    }

    private string FilterMessage(string input)
    {
        // filtra caratteri troppo strani / injection html ecc.
        StringBuilder sb = new StringBuilder();
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c) || " .,!?-:;()[]{}'\"/_@#$%^&*+".Contains(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    // =========================
    // NETWORK (FishNet)
    // =========================
    [ServerRpc(RequireOwnership = false)]
    private void SendMessageToServer(byte[] encodedMessage)
    {
        // lato server → manda a tutti gli observer
        BroadcastMessageToClients(encodedMessage);
    }

    [ObserversRpc]
    private void BroadcastMessageToClients(byte[] encodedMessage)
    {
        AddMessage(encodedMessage);
    }

    // =========================
    // UI / MESSAGGI
    // =========================
    private void AddMessage(byte[] encodedMessage)
    {
        string decodedMessage = Encoding.UTF8.GetString(encodedMessage);

        if (messagePrefab == null || messageContent == null)
        {
            Debug.LogWarning("ChatManager: messagePrefab/messageContent non settati.");
            return;
        }

        GameObject newMessage = Instantiate(messagePrefab, messageContent);
        TMP_Text text = newMessage.GetComponent<TMP_Text>();
        if (text != null)
            text.text = decodedMessage;

        ManageScrollback(newMessage);
    }

    private void LocalSystemMessage(string systemText)
    {
        if (messagePrefab == null || messageContent == null)
        {
            Debug.LogWarning("ChatManager: messagePrefab/messageContent non settati.");
            return;
        }

        GameObject newMessage = Instantiate(messagePrefab, messageContent);
        TMP_Text text = newMessage.GetComponent<TMP_Text>();
        if (text != null)
            text.text = $"<color=red><b>{systemText}</b></color>";

        ManageScrollback(newMessage);
    }

    private void ManageScrollback(GameObject newMessage)
    {
        spawnedMessages.Add(newMessage);

        // se supero maxMessages → distruggo le più vecchie
        while (spawnedMessages.Count > maxMessages)
        {
            GameObject oldMessage = spawnedMessages[0];
            spawnedMessages.RemoveAt(0);
            if (oldMessage != null)
                Destroy(oldMessage);
        }

        // forza scroll in basso
        Canvas.ForceUpdateCanvases();
        var scrollRect = messageContent.GetComponentInParent<ScrollRect>();
        if (scrollRect != null)
            scrollRect.verticalNormalizedPosition = 0f;
    }
}
