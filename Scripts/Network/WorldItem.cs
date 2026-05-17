using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Network;

namespace RPG.Network
{
    /// <summary>
    /// Item dropado no chão. Server-authoritative — só o servidor decide
    /// quem pega e quando despawna.
    ///
    /// === MUDANÇAS DESTA VERSÃO ===
    ///
    ///   COMENTÁRIO DE _picked AJUSTADO:
    ///   Versão anterior dizia "previne race condition entre dois Cmds
    ///   simultâneos". Tecnicamente impreciso — Mirror processa Cmds
    ///   sequencialmente por NetworkBehaviour, então duas Cmds para o
    ///   MESMO WorldItem nunca rodam em paralelo de verdade. A flag é
    ///   uma defesa contra uma sequência rápida de Cmds enfileirados
    ///   (player 1 pega, player 2 tenta pegar 1ms depois mas Cmd 2 foi
    ///   despachado antes do Destroy completar) — defesa em profundidade
    ///   honesta, não correção de race real.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [SyncVar] public string ItemId  = "";
        [SyncVar] public int    Quantity = 1;

        [Header("Visual")]
        [SerializeField] private TMPro.TMP_Text label;
        [SerializeField] private Renderer        meshRenderer;

        [Header("Despawn")]
        [SerializeField] private float despawnAfterSeconds = 60f;

        // Defesa em profundidade: Mirror serializa Cmds por objeto, então
        // duas CmdPickUp não rodam realmente em paralelo. Mas Cmds podem
        // já estar enfileiradas quando o primeiro pickup completa, e nesse
        // intervalo a flag impede dupla resolução antes do Destroy chegar.
        private bool _picked;

        public override void OnStartServer()
        {
            if (despawnAfterSeconds > 0f)
                Invoke(nameof(ServerDespawnIfNotPicked), despawnAfterSeconds);
        }

        public override void OnStartClient()
        {
            ApplyVisualsFromData();
        }

        private void ApplyVisualsFromData()
        {
            if (string.IsNullOrEmpty(ItemId)) return;
            var data = ItemDatabase.Instance?.GetItem(ItemId);
            if (data == null) return;

            if (label != null)
            {
                label.text = Quantity > 1
                    ? $"{data.DisplayName} x{Quantity}"
                    : data.DisplayName;
            }
        }

        [Server]
        public void ServerInitialize(string itemId, int quantity = 1)
        {
            ItemId   = itemId   ?? "";
            Quantity = Mathf.Max(1, quantity);
        }

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint pickerNetId)
        {
            // Defesa em profundidade — ver comentário em _picked acima
            if (_picked) return;

            if (!NetworkServer.spawned.TryGetValue(pickerNetId, out var identity)) return;
            if (identity == null) return;

            var picker = identity.GetComponent<NetworkPlayer>();
            if (picker == null || picker.Dead) return;

            float dist = Vector3.Distance(picker.transform.position, transform.position);
            if (dist > 5f)
            {
                Debug.LogWarning($"[Security] {picker.CharacterName} tentou pegar item " +
                                 $"a {dist:0.0}m de distância.");
                return;
            }

            if (string.IsNullOrEmpty(ItemId)) return;

            var inventory = picker.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            int slot = inventory.ServerAddItem(ItemId, Quantity);
            if (slot < 0)
            {
                picker.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            _picked = true;
            CancelInvoke(nameof(ServerDespawnIfNotPicked));
            NetworkServer.Destroy(gameObject);
        }

        [Server]
        private void ServerDespawnIfNotPicked()
        {
            if (_picked) return;
            _picked = true;
            NetworkServer.Destroy(gameObject);
        }
    }
}
