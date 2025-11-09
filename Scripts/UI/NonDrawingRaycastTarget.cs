using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public class NonDrawingRaycastTarget : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vh) { vh.Clear(); } // non disegna nulla
}
