using UnityEngine;
using UnityEngine.EventSystems;

public class InventoryDragFlag : MonoBehaviour, IBeginDragHandler, IEndDragHandler
{
    public static bool IsDraggingUI { get; private set; }
    public void OnBeginDrag(PointerEventData eventData) => IsDraggingUI = true;
    public void OnEndDrag(PointerEventData eventData) => IsDraggingUI = false;
}
