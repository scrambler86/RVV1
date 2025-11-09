using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RectTransform))]
public class UIMedalDraggable : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Header("Dati")]
    public string masteryType = "Mastery"; // es. "Warrior", "Magery", ...
    public Image icon;
    public bool interactable = true;

    [Header("Drag Layer")]
    public RectTransform dragLayerOverride; // facoltativo (un layer alto nel Canvas)

    Canvas _canvas;
    CanvasGroup _cg;
    RectTransform _rt;
    RectTransform _originalParent;
    Vector2 _originalAnchored;
    public UIMasterySlot currentSlot { get; set; }

    void Awake()
    {
        _rt = transform as RectTransform;
        if (!icon) TryGetComponent(out icon);
        _cg = GetComponent<CanvasGroup>();
        if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        _canvas = GetComponentInParent<Canvas>();
    }

    public void OnBeginDrag(PointerEventData e)
    {
        if (!interactable) return;

        _originalParent = _rt.parent as RectTransform;
        _originalAnchored = _rt.anchoredPosition;

        _cg.blocksRaycasts = false; // lascia passare i drop
        RectTransform layer = dragLayerOverride ? dragLayerOverride : (_canvas ? _canvas.transform as RectTransform : _originalParent);
        _rt.SetParent(layer, false);
        _rt.SetAsLastSibling();
    }

    public void OnDrag(PointerEventData e)
    {
        if (!interactable || _canvas == null) return;
        _rt.anchoredPosition += e.delta / _canvas.scaleFactor;
    }

    public void OnEndDrag(PointerEventData e)
    {
        if (!interactable) return;
        _cg.blocksRaycasts = true;

        if (currentSlot != null) return; // lo slot ha già chiamato Attach
        ReturnToOrigin();
    }

    public void ReturnToOrigin()
    {
        _rt.SetParent(_originalParent, false);
        _rt.anchoredPosition = _originalAnchored;
        _rt.localScale = Vector3.one;
    }
}
