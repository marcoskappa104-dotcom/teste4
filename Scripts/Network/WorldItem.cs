using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// Item dropado no mundo. Sincronizado via Mirror.
    ///
    /// === MUDANÇAS DESTA VERSÃO (race condition em pickup) ===
    ///
    ///   1. CLAIM-FIRST PATTERN EM CmdPickUp:
    ///      A versão anterior validava range/inventário PRIMEIRO e só depois
    ///      setava _picked = true. Em Mirror, Cmds são serializados no main
    ///      thread, então não há race de threads, mas há um problema mais
    ///      sutil: ServerAddItem pode emitir RpcShowMessageToOwner que
    ///      processa eventos pendentes (raro mas possível em loops aninhados).
    ///      Se durante essa janela um segundo CmdPickUp do mesmo item entrar
    ///      no pipeline, ambos passariam pelo `if (_picked) return`.
    ///
    ///      Solução: reivindicar _picked IMEDIATAMENTE, revertendo apenas se
    ///      alguma validação falhar. Isso elimina qualquer ambiguidade.
    ///
    ///   2. ORDEM DE OPERAÇÕES EM SUCESSO:
    ///      StopAllCoroutines() agora roda ANTES do feedback Rpc. Antes,
    ///      o AutoDespawn podia teoricamente disparar entre o RpcFeedback e
    ///      o NetworkServer.Destroy (improvável mas defensivo).
    ///
    ///   3. LOGS DE SECURITY MAIS INFORMATIVOS:
    ///      Diferencia "item já pegou" (não loga) de "fora de range"
    ///      (loga com player + distância) para facilitar detecção de cheaters.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text nameLabel;
        [SerializeField] private GameObject     glowEffect;

        [Header("Visual Root (filho que recebe o bobbing)")]
        [SerializeField] private Transform visualRoot;

        [Header("Configuração")]
        [SerializeField] private float despawnTime  = 60f;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1.5f;
        [SerializeField] private float pickupRadius = 2.5f;

        // Multiplicador anti-cheat: aceita pickups com folga para latência,
        // mas não tão generoso a ponto de viabilizar trapaça óbvia.
        private const float PICKUP_LATENCY_TOLERANCE = 1.5f;

        [SyncVar(hook = nameof(OnItemIdChanged))] private string _itemId = "";

        public string ItemId => _itemId;

        private bool  _picked;
        private float _startLocalY;
        private bool  _hasVisualRoot;

        // ══════════════════════════════════════════════════════════════════
        // Server
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerInitialize(string itemId)
        {
            _itemId = itemId;
            StartCoroutine(AutoDespawn());
        }

        [Server]
        private IEnumerator AutoDespawn()
        {
            yield return new WaitForSeconds(despawnTime);
            if (!_picked && isServer)
                NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // Client
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _hasVisualRoot = visualRoot != null;
            if (!_hasVisualRoot)
            {
                Debug.LogWarning($"[WorldItem] '{name}': visualRoot não configurado no prefab. " +
                                 "Bobbing aplicado no transform raiz pode causar jitter em multiplayer.");
            }
        }

        public override void OnStartClient()
        {
            if (_hasVisualRoot)
                _startLocalY = visualRoot.localPosition.y;

            RefreshVisual(_itemId);
        }

        private void OnItemIdChanged(string oldId, string newId) => RefreshVisual(newId);

        private void RefreshVisual(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null) return;

            if (spriteRenderer != null && item.Icon != null)
                spriteRenderer.sprite = item.Icon;

            if (nameLabel != null)
            {
                nameLabel.text  = item.DisplayName;
                nameLabel.color = item.RarityColor;
            }

            if (glowEffect != null)
                glowEffect.SetActive(item.Rarity >= ItemRarity.Rare);
        }

        private void Update()
        {
            if (!isClient || !_hasVisualRoot) return;

            float newLocalY = _startLocalY
                + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            var localPos = visualRoot.localPosition;
            localPos.y = newLocalY;
            visualRoot.localPosition = localPos;
        }

        // ══════════════════════════════════════════════════════════════════
        // Pickup — CLAIM-FIRST PATTERN
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            // RACE PROTECTION: reivindica imediatamente. Se algum check falhar
            // depois, revertemos. Garante que dois Cmds simultâneos não passem
            // ambos pelas validações.
            if (_picked) return;

            if (string.IsNullOrEmpty(_itemId))
            {
                Debug.LogWarning("[WorldItem] CmdPickUp em item sem _itemId — SyncVar ainda não chegou?");
                return;
            }

            // Resolve player ANTES de reivindicar (caso netId inválido)
            NetworkPlayer player = null;
            if (NetworkServer.spawned.TryGetValue(playerNetId, out var identity))
                player = identity?.GetComponent<NetworkPlayer>();

            if (player == null || player.Dead) return;

            // RECLAMA o pickup. Daqui pra frente, qualquer falha precisa reverter.
            _picked = true;

            // Range check
            float dist        = Vector3.Distance(transform.position, player.transform.position);
            float maxDistance = pickupRadius * PICKUP_LATENCY_TOLERANCE;
            if (dist > maxDistance)
            {
                _picked = false;
                Debug.LogWarning($"[WorldItem] Pickup fora de range: {dist:0.1}u por {player.CharacterName} (max {maxDistance:0.1})");
                return;
            }

            // Inventory check
            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null)
            {
                _picked = false;
                return;
            }

            // Tenta adicionar — pode falhar por inventário cheio
            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0)
            {
                _picked = false;
                return;
            }

            // ── SUCESSO GARANTIDO DAQUI PRA BAIXO ─────────────────────────
            // Para o despawn coroutine ANTES do destroy para evitar double-destroy
            StopAllCoroutines();

            var    item     = ItemDatabase.Instance?.GetItem(_itemId);
            string itemName = item?.DisplayName ?? _itemId;
            Color  color    = item?.RarityColor ?? Color.white;
            RpcPickupFeedback(playerNetId, itemName, color);

            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        private void RpcPickupFeedback(uint playerNetId, string itemName, Color rarityColor)
        {
            if (Application.isBatchMode) return;

            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null) return;
            if (localPlayer.netId != playerNetId) return;

            FloatingTextManager.Instance?.Show(
                $"+ {itemName}", transform.position + Vector3.up, rarityColor);
            UIManager.Instance?.ShowMessage($"Coletou: {itemName}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, pickupRadius);
        }
#endif
    }
}
