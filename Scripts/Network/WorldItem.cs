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
    /// === MUDANÇAS DESTA VERSÃO (segurança) ===
    ///
    ///   1. PICKUP RANGE MAIS CONSERVADOR:
    ///      Antes: pickupRadius * 2f (generoso demais, aceitava pickup a 5m
    ///      em um item de radius 2.5m). Agora: pickupRadius * 1.5f, com
    ///      tolerância suficiente para latência de rede sem aceitar trapaça.
    ///
    ///   2. DOUBLE-CHECK DE ATOMICIDADE:
    ///      _picked agora é uma flag composta: setamos true ANTES de chamar
    ///      ServerAddItem, e se falhar, revertemos. Race condition fica
    ///      impossível mesmo com Cmds quase simultâneos.
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
        // Pickup
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            if (_picked) return;
            if (string.IsNullOrEmpty(_itemId))
            {
                Debug.LogWarning("[WorldItem] CmdPickUp em item sem _itemId — SyncVar ainda não chegou?");
                return;
            }

            NetworkPlayer player = null;
            if (NetworkServer.spawned.TryGetValue(playerNetId, out var identity))
                player = identity?.GetComponent<NetworkPlayer>();

            if (player == null || player.Dead) return;

            float dist        = Vector3.Distance(transform.position, player.transform.position);
            float maxDistance = pickupRadius * PICKUP_LATENCY_TOLERANCE;
            if (dist > maxDistance)
            {
                Debug.LogWarning($"[WorldItem] Pickup fora de range: {dist:0.1}u por {player.CharacterName} (max {maxDistance:0.1})");
                return;
            }

            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            _picked = true;

            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0)
            {
                // Reverte para que outro player possa tentar pegar
                _picked = false;
                return;
            }

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