using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class AutoFadeOnTrigger : MonoBehaviour
{
    public float fadeDuration = 0.5f;
    public float transparentAlpha = 0.3f;

    private List<Renderer> renderers = new List<Renderer>();
    private Dictionary<Renderer, Material> originalMaterials = new Dictionary<Renderer, Material>();
    private Dictionary<Renderer, Coroutine> fadeCoroutines = new Dictionary<Renderer, Coroutine>();

    void Start()
    {
        // Trova automaticamente tutti i renderers del genitore
        Renderer[] found = GetComponentsInParent<Renderer>();
        foreach (var r in found)
        {
            Material instance = new Material(r.material);
            originalMaterials[r] = instance;
            r.material = instance;
            renderers.Add(r);
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            foreach (var rend in renderers)
            {
                StartFade(rend, transparentAlpha);
            }
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            foreach (var rend in renderers)
            {
                StartFade(rend, 1f);
            }
        }
    }

    void StartFade(Renderer rend, float targetAlpha)
    {
        if (fadeCoroutines.ContainsKey(rend))
            StopCoroutine(fadeCoroutines[rend]);

        fadeCoroutines[rend] = StartCoroutine(FadeMaterial(rend.material, targetAlpha));
    }

    IEnumerator FadeMaterial(Material mat, float targetAlpha)
    {
        float startAlpha = mat.color.a;
        float t = 0;

        Color baseColor = mat.color;
        mat.SetFloat("_Surface", 1); // Assicura URP Transparent

        while (t < fadeDuration)
        {
            float blend = t / fadeDuration;
            float alpha = Mathf.Lerp(startAlpha, targetAlpha, blend);
            mat.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            t += Time.deltaTime;
            yield return null;
        }

        mat.color = new Color(baseColor.r, baseColor.g, baseColor.b, targetAlpha);
    }
}
