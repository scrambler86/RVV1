using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class InventoryScrollSnap : MonoBehaviour, IScrollHandler, IBeginDragHandler, IEndDragHandler
{
    public ScrollRect scrollRect;         // ScrollRect principale
    public float cellHeight = 62f;        // Altezza di una cella (incluso spacing)
    public int totalRows = 10;            // Numero totale di righe nello scroll
    public int visibleRows = 4;           // Quante righe sono visibili

    private float scrollStep;
    private bool isDragging = false;

    private float lastScrollPosition;
    private float snapDelay = 0.4f; // Tempo di inattività prima dello snap
    private float timeSinceLastChange = 0f;

    void Start()
    {
        scrollStep = 1f / (totalRows - visibleRows); // Normalized step
    }

    void Update()
    {
        float currentScroll = scrollRect.verticalNormalizedPosition;

        if (!Mathf.Approximately(currentScroll, lastScrollPosition))
        {
            lastScrollPosition = currentScroll;
            timeSinceLastChange = 0f;
        }
        else
        {
            timeSinceLastChange += Time.deltaTime;

            if (timeSinceLastChange >= snapDelay && !isDragging)
            {
                SnapToClosestStep();
                timeSinceLastChange = 0f;
            }
        }
    }

    public void OnScroll(PointerEventData eventData)
    {
        float delta = eventData.scrollDelta.y;

        int direction = delta > 0 ? 1 : -1;
        float target = scrollRect.verticalNormalizedPosition + direction * scrollStep;
        scrollRect.verticalNormalizedPosition = Mathf.Clamp01(RoundToStep(target));
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        isDragging = true;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        isDragging = false;
        SnapToClosestStep();
    }

    private void SnapToClosestStep()
    {
        float pos = scrollRect.verticalNormalizedPosition;
        scrollRect.verticalNormalizedPosition = RoundToStep(pos);
    }

    private float RoundToStep(float value)
    {
        float steps = Mathf.Round(value / scrollStep);
        return Mathf.Clamp01(steps * scrollStep);
    }
}
