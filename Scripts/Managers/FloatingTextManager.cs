using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// Pool de textos flutuantes para combate (dano, miss, heal, XP).
    /// Cliente-only — não usa Mirror.
    ///
    /// === MUDANÇAS DESTA VERSÃO ===
    ///
    ///   1. CAP DEFENSIVO NO POOL (MAX_POOL_SIZE):
    ///      Antes, o pool podia crescer indefinidamente se Show fosse
    ///      chamado em rajada (ex: AoE atingindo 30 mobs simultâneos com
    ///      cooldown que mantém textos flutuando). Cada texto não retornado
    ///      ao pool causava nova instanciação. Agora MAX_POOL_SIZE=200
    ///      impede instanciação além do limite — chamadas extras de Show
    ///      silenciosamente caem (UX não-crítica, melhor que travar o frame
    ///      criando 500 objetos de texto em 1s).
    ///
    ///   2. ENTRY PROCESS GRACIOSO (mantido):
    ///      Textos que terminam a animação voltam ao pool, não são
    ///      destruídos.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [Header("Configuração")]
        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private Transform  poolParent;

        [Tooltip("Quantos textos pré-alocar no Awake (preenchimento inicial).")]
        [SerializeField] private int prewarmCount = 16;

        /// <summary>
        /// Cap defensivo. Em combate AoE pesado, sem este cap, o pool
        /// poderia crescer para centenas de instâncias e não voltar
        /// abaixo desse pico até o fim da sessão. 200 é generoso o
        /// suficiente para qualquer cenário razoável de gameplay.
        /// </summary>
        private const int MAX_POOL_SIZE = 200;

        [Header("Animação")]
        [SerializeField] private float floatDuration = 1.0f;
        [SerializeField] private float floatHeight   = 1.5f;
        [SerializeField] private float fadeStartFrac = 0.5f;

        private struct PoolEntry
        {
            public GameObject Obj;
            public TMP_Text   Text;
            public Transform  Tx;
        }

        private readonly Queue<PoolEntry> _pool   = new Queue<PoolEntry>();
        private readonly List<ActiveText> _active = new List<ActiveText>();

        private struct ActiveText
        {
            public PoolEntry Entry;
            public Vector3   StartPos;
            public float     Elapsed;
            public Color     StartColor;
        }

        // ── Contagem total emitida (pool em uso + na fila) ─────────────────
        private int _totalEmitted;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (floatingTextPrefab == null)
            {
                Debug.LogError("[FloatingTextManager] floatingTextPrefab não configurado.");
                enabled = false;
                return;
            }

            if (poolParent == null) poolParent = transform;

            int prewarm = Mathf.Clamp(prewarmCount, 0, MAX_POOL_SIZE);
            for (int i = 0; i < prewarm; i++)
                _pool.Enqueue(CreateNew());
        }

        private void Update()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var entry = _active[i];
                entry.Elapsed += Time.deltaTime;

                if (entry.Elapsed >= floatDuration)
                {
                    Return(entry.Entry);
                    _active.RemoveAt(i);
                    continue;
                }

                float t       = entry.Elapsed / floatDuration;
                Vector3 newPos = entry.StartPos + Vector3.up * (floatHeight * t);
                entry.Entry.Tx.position = newPos;

                if (t > fadeStartFrac)
                {
                    float fadeT = (t - fadeStartFrac) / (1f - fadeStartFrac);
                    Color c     = entry.StartColor;
                    c.a         = Mathf.Lerp(1f, 0f, fadeT);
                    entry.Entry.Text.color = c;
                }

                _active[i] = entry;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public void Show(string text, Vector3 worldPosition, Color color)
        {
            if (!enabled) return;
            if (string.IsNullOrEmpty(text)) return;

            PoolEntry entry;
            if (_pool.Count > 0)
            {
                entry = _pool.Dequeue();
            }
            else if (_totalEmitted < MAX_POOL_SIZE)
            {
                entry = CreateNew();
            }
            else
            {
                // Cap atingido. Não criar mais. Em combate AoE muito pesado,
                // perder um floating text é UX aceitável — preferível a
                // congelar o frame criando dezenas de GameObjects.
                return;
            }

            entry.Tx.position    = worldPosition;
            entry.Text.text      = text;
            entry.Text.color     = color;
            entry.Obj.SetActive(true);

            _active.Add(new ActiveText
            {
                Entry      = entry,
                StartPos   = worldPosition,
                Elapsed    = 0f,
                StartColor = color
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private PoolEntry CreateNew()
        {
            var go     = Instantiate(floatingTextPrefab, poolParent);
            var text   = go.GetComponentInChildren<TMP_Text>();
            var entry  = new PoolEntry { Obj = go, Text = text, Tx = go.transform };
            go.SetActive(false);
            _totalEmitted++;
            return entry;
        }

        private void Return(PoolEntry entry)
        {
            entry.Obj.SetActive(false);
            _pool.Enqueue(entry);
        }
    }
}
