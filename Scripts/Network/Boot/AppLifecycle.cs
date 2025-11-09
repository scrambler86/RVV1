using UnityEngine;
using FishNet;
using FishNet.Managing;

namespace NetBoot
{
    /// <summary>
    /// Gestisce lo shutdown ordinato di FishNet ed espone un flag globale quando l'app chiude.
    /// Questo script non rende persistente il GameObject a cui è collegato: crea invece
    /// un piccolo singleton separato (persistente) che si occupa solo del teardown.
    /// Metti lo script sul tuo GameObject Bootstrap come prima; non cambierà la gerarchia.
    /// </summary>
    public class AppLifecycle : MonoBehaviour
    {
        public static bool Quitting { get; private set; }

        [Tooltip("Se true, crea un GameObject separato e persistente che gestisce il teardown di FishNet.")]
        public bool createPersistentSingleton = true;

        void Awake()
        {
            if (createPersistentSingleton)
                EnsurePersistentTeardownHandler();

            Application.quitting += OnAppQuitting;
        }

        void OnDestroy()
        {
            Application.quitting -= OnAppQuitting;
        }

        void OnAppQuitting()
        {
            Quitting = true;
            StopFishNetIfRunning();
        }

        void StopFishNetIfRunning()
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null) return;

            try
            {
                if (nm.ServerManager != null && nm.ServerManager.Started)
                    nm.ServerManager.StopConnection(true);

                if (nm.ClientManager != null && nm.ClientManager.Started)
                    nm.ClientManager.StopConnection();
            }
            catch
            {
                // Ignora eccezioni di teardown
            }
        }

        // Crea (una sola volta) un GameObject persistente che gestisce il quitting
        void EnsurePersistentTeardownHandler()
        {
            // se esiste già un handler globale, non crearne un altro
            var existing = GameObject.Find("AppLifecycle.PersistentHandler");
            if (existing != null) return;

            var go = new GameObject("AppLifecycle.PersistentHandler");
            DontDestroyOnLoad(go);
            go.AddComponent<PersistentTeardownHandler>();
        }

        // MonoBehaviour minimale che rimane in scena durante tutta l'app e gestisce l'evento OnApplicationQuit
        private class PersistentTeardownHandler : MonoBehaviour
        {
            void Awake()
            {
                // nulla qui: la logica eseguita su Application.quitting nell'istanza AppLifecycle locale
                // per sicurezza registriamo anche qui l'handler globale.
                Application.quitting += OnAppQuitting;
            }

            void OnDestroy()
            {
                Application.quitting -= OnAppQuitting;
            }

            void OnAppQuitting()
            {
                Quitting = true;
                var nm = InstanceFinder.NetworkManager;
                if (nm != null)
                {
                    try
                    {
                        if (nm.ServerManager != null && nm.ServerManager.Started)
                            nm.ServerManager.StopConnection(true);

                        if (nm.ClientManager != null && nm.ClientManager.Started)
                            nm.ClientManager.StopConnection();
                    }
                    catch { }
                }
            }
        }
    }
}
