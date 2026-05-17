using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.Network;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkManager customizado: spawn por raça, banco de prefabs de
    /// projétil por tipo de arma, gerenciamento de pendingSpawns.
    ///
    /// === MUDANÇAS DESTA VERSÃO ===
    ///
    ///   1. WARNING EM PENDING SPAWN DUPLICADO:
    ///      Se um connectionId tiver pending spawn sobrescrito (cenário
    ///      raríssimo de reciclagem de connId pelo Mirror entre reconnects
    ///      muito rápidos), logamos warning para facilitar debug — antes
    ///      sobrescrevíamos silenciosamente.
    ///
    ///   2. BUSCA DE PROJECTILE PREFABS (mantido):
    ///      BuildProjectileLookup constrói lookup por WeaponType uma única
    ///      vez na inicialização.
    ///
    ///   3. SPAWN POR RAÇA (mantido).
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        [Header("Spawn por Raça")]
        [Tooltip("Pontos de spawn para cada raça (Human, Elf, Orc, etc).")]
        [SerializeField] private RaceSpawnEntry[] raceSpawnPoints;

        [Header("Projéteis por tipo de arma")]
        [Tooltip("Prefabs de projétil registrados por tipo de arma (Bow, Staff, etc).")]
        [SerializeField] private ProjectilePrefabEntry[] projectilePrefabs;

        // ── Estado interno ─────────────────────────────────────────────────
        private readonly Dictionary<int, PendingSpawn>      _pendingSpawns      = new();
        private readonly Dictionary<WeaponType, GameObject> _projectileLookup   = new();

        [System.Serializable]
        public class RaceSpawnEntry
        {
            public CharacterRace Race;
            public Transform     SpawnPoint;
        }

        [System.Serializable]
        public class ProjectilePrefabEntry
        {
            public WeaponType  WeaponType;
            public GameObject  Prefab;
        }

        private struct PendingSpawn
        {
            public string Username;
            public string CharacterId;
        }

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        public override void Awake()
        {
            base.Awake();
            BuildProjectileLookup();
        }

        private void BuildProjectileLookup()
        {
            _projectileLookup.Clear();
            if (projectilePrefabs == null) return;

            foreach (var entry in projectilePrefabs)
            {
                if (entry.Prefab == null) continue;
                if (entry.Prefab.GetComponent<Projectile>() == null)
                {
                    Debug.LogError($"[RPGNetworkManager] Prefab '{entry.Prefab.name}' " +
                                   $"para {entry.WeaponType} não tem componente Projectile.");
                    continue;
                }
                _projectileLookup[entry.WeaponType] = entry.Prefab;
            }

            Debug.Log($"[RPGNetworkManager] {_projectileLookup.Count} prefabs de projétil registrados.");
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public GameObject GetProjectilePrefab(WeaponType type)
        {
            return _projectileLookup.TryGetValue(type, out var prefab) ? prefab : null;
        }

        public Vector3 GetSpawnPositionForRace(CharacterRace race, CharacterData fallbackData = null)
        {
            if (raceSpawnPoints != null)
            {
                foreach (var entry in raceSpawnPoints)
                {
                    if (entry.Race == race && entry.SpawnPoint != null)
                        return entry.SpawnPoint.position;
                }
            }

            if (fallbackData != null)
            {
                Vector3 saved = new Vector3(fallbackData.PosX, fallbackData.PosY, fallbackData.PosZ);
                if (saved.sqrMagnitude > 0.01f) return saved;
            }

            return Vector3.zero;
        }

        // ══════════════════════════════════════════════════════════════════
        // Server callbacks
        // ══════════════════════════════════════════════════════════════════

        public override void OnStartServer()
        {
            base.OnStartServer();
            // ServerAuthManager agora é MonoBehaviour (não NetworkBehaviour),
            // então o Mirror não chama mais OnStartServer nele automaticamente.
            // Registramos os hooks manualmente aqui.
            ServerAuthManager.Instance?.RegisterServerHooks();
        }

        public override void OnStopServer()
        {
            ServerAuthManager.Instance?.UnregisterServerHooks();
            base.OnStopServer();
        }

        public void SpawnPlayerForConnection(int connectionId, string username, string characterId)
        {
            // Aviso se pending spawn já existia — cenário raro de reciclagem
            // de connectionId pelo Mirror em reconnects muito rápidos.
            if (_pendingSpawns.ContainsKey(connectionId))
            {
                var existing = _pendingSpawns[connectionId];
                Debug.LogWarning(
                    $"[RPGNetworkManager] Pending spawn sobrescrito para conn {connectionId}: " +
                    $"'{existing.Username}/{existing.CharacterId}' → '{username}/{characterId}'. " +
                    $"Possível reciclagem de connId em reconnect rápido.");
            }

            _pendingSpawns[connectionId] = new PendingSpawn
            {
                Username    = username,
                CharacterId = characterId
            };
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            if (!_pendingSpawns.TryGetValue(conn.connectionId, out var pending))
            {
                Debug.LogError($"[RPGNetworkManager] AddPlayer sem pending spawn (conn={conn.connectionId}).");
                conn.Disconnect();
                return;
            }
            _pendingSpawns.Remove(conn.connectionId);

            var charData = Managers.DatabaseManager.Instance
                ?.LoadCharacterForAccount(pending.CharacterId, pending.Username);

            if (charData == null)
            {
                Debug.LogError($"[RPGNetworkManager] Não consegui carregar character " +
                               $"'{pending.CharacterId}' para '{pending.Username}'.");
                conn.Disconnect();
                return;
            }

            Vector3 spawnPos = GetSpawnPositionForRace(charData.Race, charData);

            if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                spawnPos = hit.position;

            var go = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, go);

            var netPlayer = go.GetComponent<NetworkPlayer>();
            netPlayer?.ServerInitialize(charData, pending.Username);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _pendingSpawns.Remove(conn.connectionId);

            if (conn.identity != null)
            {
                var netPlayer = conn.identity.GetComponent<NetworkPlayer>();
                netPlayer?.ServerSaveCharacter();
            }

            base.OnServerDisconnect(conn);
        }
    }
}