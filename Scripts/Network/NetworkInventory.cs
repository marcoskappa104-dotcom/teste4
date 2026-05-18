using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Combat;
using RPG.UI;

namespace RPG.Network
{
    /// <summary>
    /// Inventário do jogador. Server-authoritative.
    ///
    /// === MUDANÇAS DESTA VERSÃO (segurança em swap) ===
    ///
    ///   1. ROLLBACK SEGURO EM TrySwapFromInventory:
    ///      A versão anterior, em caso de falha CRÍTICA de rollback, fazia
    ///      Slots.Add direto bypassando ServerAddItem. Isso era uma porta
    ///      para duplicação de itens se o ItemDatabase estivesse corrompido.
    ///      Agora preferimos PERDER o item (raríssimo) a duplicá-lo. Log
    ///      crítico permanece para análise do operador.
    ///
    ///   2. MENSAGEM CLARA QUANDO ItemDatabase INDISPONÍVEL:
    ///      Antes, se ItemDatabase.Instance fosse null, o jogador recebia
    ///      "Este item não pode ser equipado" — confuso. Agora diferencia
    ///      "banco de itens não disponível" de "item inválido para slot".
    ///
    ///   3. EARLY-RETURN EM Cmds SE INVENTÁRIO NÃO INICIALIZADO:
    ///      Adicionado check de _netPlayer != null já existia; agora também
    ///      validamos que ItemDatabase.Instance existe (ele é singleton mas
    ///      pode não estar pronto no primeiro frame após carga de cena).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInventory : NetworkBehaviour
    {
        public const int MAX_INVENTORY_SLOTS = 60;
        public const int GEM_SLOT_COUNT      = 4;

        // ── Sincronização ──────────────────────────────────────────────────
        public readonly SyncList<InventorySlotData> Slots         = new SyncList<InventorySlotData>();
        public readonly SyncList<EquippedItemData>  EquippedItems = new SyncList<EquippedItemData>();

        [SyncVar(hook = nameof(OnGemSlotQChanged))] public string GemSlotQ = "";
        [SyncVar(hook = nameof(OnGemSlotWChanged))] public string GemSlotW = "";
        [SyncVar(hook = nameof(OnGemSlotEChanged))] public string GemSlotE = "";
        [SyncVar(hook = nameof(OnGemSlotRChanged))] public string GemSlotR = "";

        // ── Eventos (cliente) ──────────────────────────────────────────────
        public event Action OnInventoryChanged;
        public event Action OnGemLoadoutChanged;
        public event Action OnEquipmentChanged;

        // ── Estado do servidor ─────────────────────────────────────────────
        private int           _nextSlotIndex;
        private NetworkPlayer _netPlayer;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _netPlayer = GetComponent<NetworkPlayer>();
        }

        public override void OnStartClient()
        {
            Slots.Callback         += OnSlotsChangedClient;
            EquippedItems.Callback += OnEquippedItemsChangedClient;
        }

        public override void OnStopClient()
        {
            Slots.Callback         -= OnSlotsChangedClient;
            EquippedItems.Callback -= OnEquippedItemsChangedClient;
        }

        public override void OnStartLocalPlayer()
        {
            StartCoroutine(BindUIDelayed());
        }

        private IEnumerator BindUIDelayed()
        {
            yield return null;
            yield return null;

            InventoryUI.Instance?.BindInventory(this);
            PowerGemUI.Instance?.BindInventory(this);
        }

        // ── Hooks ──────────────────────────────────────────────────────────

        private void OnSlotsChangedClient(SyncList<InventorySlotData>.Operation op,
                                          int index, InventorySlotData oldItem, InventorySlotData newItem)
            => OnInventoryChanged?.Invoke();

        private void OnEquippedItemsChangedClient(SyncList<EquippedItemData>.Operation op,
                                                  int index, EquippedItemData oldItem, EquippedItemData newItem)
            => OnEquipmentChanged?.Invoke();

        private void OnGemSlotQChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotWChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotEChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotRChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — API do servidor
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public int ServerAddItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            if (quantity <= 0) return -1;

            var db = ItemDatabase.Instance;
            if (db == null || !db.Contains(itemId))
            {
                Debug.LogWarning($"[NetworkInventory] Item '{itemId}' não existe no banco.");
                return -1;
            }

            var item = db.GetItem(itemId);
            if (item == null) return -1;

            quantity = Mathf.Clamp(quantity, 1, ItemData.MAX_STACK_HARD_CAP * MAX_INVENTORY_SLOTS);

            if (!item.IsStackable)
                return AddNonStackable(item, quantity);

            return AddStackable(item, quantity);
        }

        [Server]
        private int AddNonStackable(ItemData item, int quantity)
        {
            int firstAffected = -1;

            for (int i = 0; i < quantity; i++)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    _netPlayer?.RpcShowMessageToOwner("Inventário cheio!");
                    return firstAffected;
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = 1
                };
                Slots.Add(slot);

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            return firstAffected;
        }

        /// <summary>
        /// Adiciona quantidade de um item stackable. Estratégia:
        ///   1. Topa stacks existentes do mesmo ItemId.
        ///   2. Cria novos slots para o restante.
        ///   3. Se inventário lota no meio, ACEITA PARCIAL e avisa o jogador
        ///      (é melhor UX que rollback total — perder farm é frustrante).
        /// </summary>
        [Server]
        private int AddStackable(ItemData item, int quantity)
        {
            int maxStack     = item.EffectiveMaxStack;
            int remaining    = quantity;
            int firstAffected = -1;

            // Fase 1: topar stacks existentes
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                var slot = Slots[i];
                if (slot.ItemId != item.ItemId) continue;
                if (slot.Quantity >= maxStack) continue;

                int room  = maxStack - slot.Quantity;
                int toAdd = Mathf.Min(room, remaining);

                slot.Quantity += toAdd;
                Slots[i]       = slot;

                remaining -= toAdd;

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            // Fase 2: criar novos stacks
            while (remaining > 0)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    _netPlayer?.RpcShowMessageToOwner(
                        $"Inventário cheio! Coletou {quantity - remaining}/{quantity} {item.DisplayName}.");
                    return firstAffected;
                }

                int amountForNewSlot = Mathf.Min(maxStack, remaining);

                var newSlot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = amountForNewSlot
                };
                Slots.Add(newSlot);

                remaining -= amountForNewSlot;

                if (firstAffected < 0) firstAffected = newSlot.SlotIndex;
            }

            return firstAffected;
        }

        [Server]
        public bool ServerRemoveSlot(int slotIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        [Server]
        public bool ServerRemoveItemById(string itemId)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ItemId == itemId)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public bool HasItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return true;
            return false;
        }

        public int FindSlotByItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return slot.SlotIndex;
            return -1;
        }

        [Server]
        public void ServerLoadFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            Slots.Clear();
            _nextSlotIndex = 0;

            var rows = db.LoadInventory(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                if (ItemDatabase.Instance != null && !ItemDatabase.Instance.Contains(row.ItemId))
                {
                    Debug.LogWarning($"[NetworkInventory] Item '{row.ItemId}' do banco não está no ItemDatabase — ignorado.");
                    continue;
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = row.SlotIndex >= 0 ? row.SlotIndex : _nextSlotIndex,
                    ItemId    = row.ItemId,
                    Quantity  = Mathf.Max(1, row.Quantity)
                };
                Slots.Add(slot);
            }

            if (Slots.Count > 0)
                _nextSlotIndex = Slots.Max(s => s.SlotIndex) + 1;
        }

        [Server]
        public void ServerLoadGemLoadout(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            var loadout = db.LoadGemLoadout(characterId);
            GemSlotQ = ValidateLoadedGemId(loadout.SlotQ);
            GemSlotW = ValidateLoadedGemId(loadout.SlotW);
            GemSlotE = ValidateLoadedGemId(loadout.SlotE);
            GemSlotR = ValidateLoadedGemId(loadout.SlotR);
        }

        [Server]
        private static string ValidateLoadedGemId(string gemId)
        {
            if (string.IsNullOrEmpty(gemId)) return "";
            var db = ItemDatabase.Instance;
            if (db == null) return gemId;
            var item = db.GetItem(gemId);
            if (item == null || !item.IsPowerGem)
            {
                Debug.LogWarning($"[NetworkInventory] Gem '{gemId}' inválida no banco — slot limpo.");
                return "";
            }
            return gemId;
        }

        [Server]
        public void ServerLoadEquippedFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            EquippedItems.Clear();

            var rows = db.LoadEquipped(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                var itemData = ItemDatabase.Instance?.GetItem(row.ItemId);
                if (itemData == null || !itemData.IsEquipment)
                {
                    Debug.LogWarning($"[NetworkInventory] Equipped item '{row.ItemId}' inválido — ignorado.");
                    continue;
                }

                EquippedItems.Add(new EquippedItemData
                {
                    Slot          = (byte)row.Slot,
                    ItemId        = row.ItemId,
                    Durability    = row.Durability,
                    MaxDurability = row.MaxDurability
                });
            }
        }

        [Server]
        public void ServerSaveAll(string characterId, string username)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            db.SaveInventory(characterId, username, new List<InventorySlotData>(Slots));
            db.SaveGemLoadout(characterId, new PowerGemLoadout
            {
                SlotQ = GemSlotQ ?? "", SlotW = GemSlotW ?? "",
                SlotE = GemSlotE ?? "", SlotR = GemSlotR ?? ""
            });
            db.SaveEquipped(characterId, new List<EquippedItemData>(EquippedItems));
        }

        // ══════════════════════════════════════════════════════════════════
        // SWAP HELPER — agora com rollback que NUNCA duplica
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Remove o item de entrada do inventário e devolve o item antigo
        /// (se houver). Em caso de falha, faz rollback SEM DUPLICAR.
        ///
        /// CONTRATO:
        ///   - Sucesso: inventório consumiu newItemId e (se aplicável) ganhou oldItemId.
        ///   - Falha: inventário volta ao estado original OU perde o newItem
        ///     (se o rollback do oldItem falhar). NUNCA duplica.
        /// </summary>
        [Server]
        private bool TrySwapFromInventory(int inventorySlotIndex, string newItemId,
                                          string oldItemId, out string failReason)
        {
            failReason = null;

            // Snapshot do slot antes de remover, para rollback se necessário
            if (!TryGetInventorySlot(inventorySlotIndex, out var originalSlot))
            {
                failReason = "Item desapareceu do inventário.";
                return false;
            }

            // 1. Remove o item de entrada
            if (!ServerRemoveSlot(inventorySlotIndex))
            {
                failReason = "Item desapareceu do inventário.";
                Debug.LogError($"[NetworkInventory] TrySwapFromInventory: " +
                               $"remove({inventorySlotIndex}) falhou inesperadamente.");
                return false;
            }

            // 2. Tenta devolver o item antigo (se havia)
            if (!string.IsNullOrEmpty(oldItemId))
            {
                int returnedSlot = ServerAddItem(oldItemId, 1);
                if (returnedSlot < 0)
                {
                    // Falha ao devolver o antigo: tenta restaurar o estado original
                    // colocando o NEW item de volta no inventário
                    int rollback = ServerAddItem(newItemId, originalSlot.Quantity);
                    if (rollback >= 0)
                    {
                        // Rollback completo bem-sucedido
                        failReason = "Sem espaço no inventário para o item antigo.";
                        return false;
                    }

                    // CASO CRÍTICO: nem o old nem o new cabem no inventário
                    // (inventário cheio + ItemDatabase corrompido).
                    // PREFERIMOS PERDER O ITEM A DUPLICAR. Log crítico para diagnóstico.
                    Debug.LogError($"[NetworkInventory] ROLLBACK CRÍTICO IRREVERSÍVEL: " +
                                   $"perda de item '{newItemId}' (qty={originalSlot.Quantity}) " +
                                   $"e falha ao devolver '{oldItemId}'. " +
                                   $"Player: {_netPlayer?.CharacterName ?? "?"}. " +
                                   $"Investigar ItemDatabase e capacidade de inventário.\n" +
                                   $"{Environment.StackTrace}");

                    _netPlayer?.RpcShowMessageToOwner(
                        "Erro de inventário. Reporte ao administrador (item perdido).");

                    failReason = "Erro crítico — operação cancelada.";
                    return false;
                }
            }

            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — leitura
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private int ServerFindEquippedIndex(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return i;
            return -1;
        }

        [Server]
        public string ServerGetEquipped(EquipmentSlot slot)
        {
            int idx = ServerFindEquippedIndex(slot);
            return idx >= 0 ? EquippedItems[idx].ItemId : "";
        }

        public string GetEquipped(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return EquippedItems[i].ItemId;
            return "";
        }

        public bool IsSlotOccupied(EquipmentSlot slot) => !string.IsNullOrEmpty(GetEquipped(slot));

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, targetSlotByte);
        }

        [Command]
        public void CmdAutoEquip(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, (byte)EquipmentSlot.None);
        }

        [Command]
        public void CmdUnequipItem(byte slotByte)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            EquipmentSlot slot = (EquipmentSlot)slotByte;

            if (slot == EquipmentSlot.None || !EquipmentSlotEx.IsActive(slot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            int idx = ServerFindEquippedIndex(slot);
            if (idx < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Slot já está vazio.");
                return;
            }

            string itemId = EquippedItems[idx].ItemId;
            if (string.IsNullOrEmpty(itemId))
            {
                EquippedItems.RemoveAt(idx);
                _netPlayer.ServerOnEquipmentChanged();
                return;
            }

            int returnedSlot = ServerAddItem(itemId, 1);
            if (returnedSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            EquippedItems.RemoveAt(idx);
            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private void ServerEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (_netPlayer == null || _netPlayer.Dead) return;

            // Check explícito do ItemDatabase para dar mensagem clara
            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível. Tente novamente em instantes.");
                Debug.LogError("[NetworkInventory] ServerEquipItem: ItemDatabase.Instance é null.");
                return;
            }

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Item não encontrado no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null)
            {
                _netPlayer.RpcShowMessageToOwner("Item inválido (não está no banco de dados).");
                Debug.LogWarning($"[NetworkInventory] Item '{foundSlot.ItemId}' não encontrado no ItemDatabase.");
                return;
            }

            if (!itemData.IsEquipment)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não pode ser equipado.");
                return;
            }

            EquipmentSlot itemSlot   = itemData.EquipSlot;
            EquipmentSlot targetSlot = (EquipmentSlot)targetSlotByte;

            if (targetSlot == EquipmentSlot.None)
                targetSlot = ResolveAutoEquipSlot(itemSlot);

            if (!EquipmentSlotEx.IsActive(targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot de equipamento inválido.");
                return;
            }

            if (!EquipmentSlotEx.CanItemFitInSlot(itemSlot, targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner(
                    $"Este item não vai no slot {EquipmentSlotEx.DisplayName(targetSlot)}.");
                return;
            }

            if (!ServerValidateRequirements(itemData, out string reason))
            {
                _netPlayer.RpcShowMessageToOwner(reason);
                return;
            }

            int    existingIdx = ServerFindEquippedIndex(targetSlot);
            string oldItemId   = "";
            if (existingIdx >= 0)
                oldItemId = EquippedItems[existingIdx].ItemId;

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldItemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            if (existingIdx >= 0)
                EquippedItems.RemoveAt(existingIdx);

            int maxDur = Mathf.Max(0, itemData.MaxDurability);
            EquippedItems.Add(new EquippedItemData
            {
                Slot          = (byte)targetSlot,
                ItemId        = itemData.ItemId,
                Durability    = maxDur > 0 ? maxDur : -1,
                MaxDurability = maxDur
            });

            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private bool TryGetInventorySlot(int slotIndex, out InventorySlotData found)
        {
            foreach (var s in Slots)
            {
                if (s.SlotIndex == slotIndex)
                {
                    found = s;
                    return true;
                }
            }
            found = default;
            return false;
        }

        [Server]
        private bool TryGetInventorySlotWithListIndex(int slotIndex,
            out InventorySlotData found, out int listIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    found     = Slots[i];
                    listIndex = i;
                    return true;
                }
            }
            found     = default;
            listIndex = -1;
            return false;
        }

        [Server]
        private bool ServerValidateRequirements(ItemData item, out string failReason)
        {
            failReason = null;
            if (item?.Requirements == null) return true;

            CharacterRace race  = _netPlayer.GetRaceEnum();
            var           bonus = StatsCalculator.GetRaceBonus(race);

            int totalSTR = _netPlayer.BaseSTR + bonus.STR + _netPlayer.AllocatedSTR;
            int totalAGI = _netPlayer.BaseAGI + bonus.AGI + _netPlayer.AllocatedAGI;
            int totalVIT = _netPlayer.BaseVIT + bonus.VIT + _netPlayer.AllocatedVIT;
            int totalDEX = _netPlayer.BaseDEX + bonus.DEX + _netPlayer.AllocatedDEX;
            int totalINT = _netPlayer.BaseINT + bonus.INT + _netPlayer.AllocatedINT;
            int totalLUK = _netPlayer.BaseLUK + bonus.LUK + _netPlayer.AllocatedLUK;

            return item.Requirements.Check(
                _netPlayer.Level,
                totalSTR, totalAGI, totalVIT, totalDEX, totalINT, totalLUK,
                race, out failReason);
        }

        [Server]
        private EquipmentSlot ResolveAutoEquipSlot(EquipmentSlot itemSlot)
        {
            if (EquipmentSlotEx.IsRing(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring1))) return EquipmentSlot.Ring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring2))) return EquipmentSlot.Ring2;
                return EquipmentSlot.Ring1;
            }

            if (EquipmentSlotEx.IsEarring(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring1))) return EquipmentSlot.Earring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring2))) return EquipmentSlot.Earring2;
                return EquipmentSlot.Earring1;
            }

            return itemSlot;
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS DO PODER — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipGem(int skillSlotIndex, int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot de joia inválido.");
                return;
            }

            // Check explícito do ItemDatabase
            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível. Tente novamente em instantes.");
                Debug.LogError("[NetworkInventory] CmdEquipGem: ItemDatabase.Instance é null.");
                return;
            }

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Joia não encontrada no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null)
            {
                _netPlayer.RpcShowMessageToOwner("Joia inválida (não está no banco de dados).");
                return;
            }

            if (!itemData.IsPowerGem)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não é uma Joia do Poder.");
                return;
            }

            string oldGemId = GetGemItemId(skillSlotIndex);

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldGemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            ServerSetGemSlot(skillSlotIndex, itemData.ItemId);
        }

        [Command]
        public void CmdUnequipGem(int skillSlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return;

            int newSlot = ServerAddItem(gemId, 1);
            if (newSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            ServerSetGemSlot(skillSlotIndex, "");
        }

        [Server]
        private void ServerSetGemSlot(int index, string itemId)
        {
            switch (index)
            {
                case 0: GemSlotQ = itemId ?? ""; break;
                case 1: GemSlotW = itemId ?? ""; break;
                case 2: GemSlotE = itemId ?? ""; break;
                case 3: GemSlotR = itemId ?? ""; break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — Commands diversos
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!ServerRemoveSlot(inventorySlotIndex))
                _netPlayer.RpcShowMessageToOwner("Item não encontrado.");
        }

        [Command]
        public void CmdUseConsumable(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível.");
                Debug.LogError("[NetworkInventory] CmdUseConsumable: ItemDatabase.Instance nulo.");
                return;
            }

            if (!TryGetInventorySlotWithListIndex(inventorySlotIndex,
                    out var foundSlot, out int listIndex))
                return;

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsConsumable) return;

            float heal = SanitizeBuff(itemData.HealAmount);
            float mana = SanitizeBuff(itemData.ManaAmount);

            if (!CanConsumableHaveEffect(heal, mana, out string rejectMsg))
            {
                _netPlayer.RpcShowMessageToOwner(rejectMsg);
                return;
            }

            if (heal > 0f) _netPlayer.ServerApplyHeal(heal);
            if (mana > 0f) _netPlayer.ServerRestoreMP(mana);

            ServerConsumeOneFromSlot(listIndex, foundSlot);
        }

        [Server]
        private bool CanConsumableHaveEffect(float heal, float mana, out string rejectMsg)
        {
            bool restoresHP = heal > 0f;
            bool restoresMP = mana > 0f;

            if (!restoresHP && !restoresMP)
            {
                rejectMsg = "Este item não tem efeito.";
                return false;
            }

            bool hpFull = _netPlayer.CurrentHP >= _netPlayer.MaxHP - 0.01f;
            bool mpFull = _netPlayer.CurrentMP >= _netPlayer.MaxMP - 0.01f;

            if (restoresHP && !restoresMP && hpFull)
            {
                rejectMsg = "Você já está com HP máximo!";
                return false;
            }

            if (!restoresHP && restoresMP && mpFull)
            {
                rejectMsg = "Você já está com MP máximo!";
                return false;
            }

            if (restoresHP && restoresMP && hpFull && mpFull)
            {
                rejectMsg = "HP e MP já estão no máximo!";
                return false;
            }

            rejectMsg = null;
            return true;
        }

        [Server]
        private void ServerConsumeOneFromSlot(int listIndex, InventorySlotData slot)
        {
            if (listIndex < 0 || listIndex >= Slots.Count) return;

            if (slot.Quantity > 1)
            {
                slot.Quantity -= 1;
                Slots[listIndex] = slot;
            }
            else
            {
                Slots.RemoveAt(listIndex);
            }
        }

        private static float SanitizeBuff(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp(value, 0f, GameConstants.Combat.MAX_HP);
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS — Leitura
        // ══════════════════════════════════════════════════════════════════

        public string GetGemItemId(int skillSlotIndex) => skillSlotIndex switch
        {
            0 => GemSlotQ ?? "",
            1 => GemSlotW ?? "",
            2 => GemSlotE ?? "",
            3 => GemSlotR ?? "",
            _ => ""
        };

        public SkillData GetEquippedSkill(int skillSlotIndex)
        {
            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return null;
            return ItemDatabase.Instance?.GetItem(gemId)?.EmbeddedSkill;
        }

        public int EquippedGemCount()
        {
            int count = 0;
            for (int i = 0; i < GEM_SLOT_COUNT; i++)
                if (!string.IsNullOrEmpty(GetGemItemId(i))) count++;
            return count;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTO — Agregação de bônus
        // ══════════════════════════════════════════════════════════════════

        public EquipmentBonuses BuildEquipmentBonuses()
            => EquipmentSlotEx.AggregateBonuses(EquippedItems);

        public int  EquippedItemCount() => EquippedItems.Count;
        public int  FreeSlotCount()     => Mathf.Max(0, MAX_INVENTORY_SLOTS - Slots.Count);
        public bool IsFull()            => Slots.Count >= MAX_INVENTORY_SLOTS;

        public int GetTotalQuantity(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int total = 0;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) total += slot.Quantity;
            return total;
        }
    }
}
