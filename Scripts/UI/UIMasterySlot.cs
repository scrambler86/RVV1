using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections;

[DisallowMultipleComponent]
public class UIMasterySlot : MonoBehaviour, IDropHandler, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Rules")]
    [Tooltip("Lascia vuoto per accettare qualsiasi tipo. Deve combaciare con UIMedalDraggable.masteryType.")]
    public string acceptedType = "";

    [Header("Visuals")]
    public Image slotImage;
    public Color hoverColor = new Color(1f, 1f, 1f, 0.85f);
    public Color normalColor = Color.white;

    [Header("FX (UI spark)")]
    public Sprite sparkSprite;
    public float sparkDuration = 0.25f;
    public float sparkScale = 1.2f;

    [Header("Content")]
    public RectTransform contentRoot;

    // stato
    public UIMedalDraggable current { get; private set; }

    void Awake()
    {
        if (!slotImage) TryGetComponent(out slotImage);
        if (!contentRoot) contentRoot = transform as RectTransform;
        if (slotImage) normalColor = slotImage.color;
    }

    public bool IsOccupied => current != null;

    public void OnDrop(PointerEventData eventData)
    {
        if (eventData.pointerDrag == null) return;
        var drag = eventData.pointerDrag.GetComponent<UIMedalDraggable>();
        if (drag == null) return;

        if (!string.IsNullOrEmpty(acceptedType) && acceptedType != drag.masteryType)
            return;
        if (IsOccupied && current != drag)
            return;

        Attach(drag);
    }

    public void Attach(UIMedalDraggable drag)
    {
        drag.currentSlot?.Clear();

        current = drag;
        drag.currentSlot = this;

        var rt = drag.transform as RectTransform;
        rt.SetParent(contentRoot, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.localScale = Vector3.one;

        PlaySpark();
    }

    public void Clear() => current = null;

    void PlaySpark()
    {
        if (sparkSprite == null)
        {
            if (slotImage) StartCoroutine(CoPulse());
            return;
        }

        var go = new GameObject("Spark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        var img = go.GetComponent<Image>();
        img.sprite = sparkSprite;
        img.raycastTarget = false;

        rt.SetParent(contentRoot, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.localScale = Vector3.one * 0.6f;

        StartCoroutine(CoSpark(img, rt));
    }

    IEnumerator CoSpark(Image img, RectTransform rt)
    {
        float t = 0f;
        while (t < sparkDuration)
        {
            t += Time.unscaledDeltaTime;
            float k = t / sparkDuration;
            img.color = new Color(1f, 1f, 1f, 1f - k);
            float s = Mathf.Lerp(0.6f, sparkScale, 1f - (1f - k) * (1f - k));
            rt.localScale = Vector3.one * s;
            yield return null;
        }
        Destroy(rt.gameObject);
    }

    IEnumerator CoPulse()
    {
        float t = 0f, dur = 0.15f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Sin((t / dur) * Mathf.PI);
            slotImage.color = Color.Lerp(normalColor, hoverColor, k);
            yield return null;
        }
        slotImage.color = normalColor;
    }

    public void OnPointerEnter(PointerEventData e) { if (slotImage) slotImage.color = hoverColor; }
    public void OnPointerExit(PointerEventData e) { if (slotImage) slotImage.color = normalColor; }
}
