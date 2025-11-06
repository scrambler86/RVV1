using UnityEngine;
using UnityEngine.UI;

public class InventoryUIController : MonoBehaviour
{
    [Header("Animazione Inventario")]
    public RectTransform inventoryPanel;
    public float showY = 100f;
    public float hideY = -200f;
    public float animationSpeed = 6f;

    [Header("Slot dinamici")]
    public RectTransform leftSlotContainer;        // Contenitore sinistro
    public RectTransform rightSlotContainer;       // Contenitore destro
    public GameObject inventorySlotPrefab;         // Prefab dello slot
    public int slotCountPerSide = 60;              // Slot per lato

    private bool isVisible = false;
    private Vector2 targetPosition;

    void Start()
    {
        if (inventoryPanel != null)
        {
            inventoryPanel.anchorMin = new Vector2(0.5f, 0f);
            inventoryPanel.anchorMax = new Vector2(0.5f, 0f);
            inventoryPanel.pivot = new Vector2(0.5f, 0f);

            targetPosition = new Vector2(0, hideY);
            inventoryPanel.anchoredPosition = targetPosition;
        }

        GenerateSlots(leftSlotContainer, 0);
        GenerateSlots(rightSlotContainer, slotCountPerSide);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T))
            ToggleInventory();

        if (inventoryPanel != null)
        {
            Vector2 current = inventoryPanel.anchoredPosition;
            current.y = Mathf.Lerp(current.y, targetPosition.y, Time.deltaTime * animationSpeed);
            inventoryPanel.anchoredPosition = current;
        }
    }

    public void ToggleInventory()
    {
        isVisible = !isVisible;
        targetPosition = new Vector2(0, isVisible ? showY : hideY);
    }

    private void GenerateSlots(RectTransform container, int startIndex)
    {
        if (container == null || inventorySlotPrefab == null)
        {
            Debug.LogWarning("⛔ Uno dei contenitori o il prefab non è assegnato.");
            return;
        }

        for (int i = 0; i < slotCountPerSide; i++)
        {
            GameObject slot = Instantiate(inventorySlotPrefab, container);
            slot.name = $"Slot_{startIndex + i}";

            var icon = slot.transform.Find("Icon");
            if (icon != null)
                icon.GetComponent<Image>().enabled = false;
        }
    }
}
