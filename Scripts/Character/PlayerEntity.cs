using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using RPG.Data;

namespace RPG.Character
{
    /// <summary>
    /// Representação local (cliente) do estado do jogador.
    ///
    /// === MUDANÇAS DESTA VERSÃO (correção de duplicação) ===
    ///
    ///   1. CONSTANTES DE AGENT MOVIDAS PARA GameConstants.Movement:
    ///      Antes, AGENT_ACCELERATION/ANGULAR_SPEED/STOP_DIST/MIN_SPEED/MAX_SPEED
    ///      estavam duplicados aqui, em NetworkPlayer.cs e em
    ///      NetworkPlayerController.cs — todos com comentários "manter em sync
    ///      com..." (sinal claro de drift inevitável). Agora vêm de
    ///      GameConstants.Movement, fonte única de verdade.
    ///
    ///   2. CACHE DE CÂMERA RESILIENTE (mantido):
    ///      MainCamera confia no operador `==` overloaded do Unity para
    ///      detectar câmeras destruídas (== null cobre tanto unassigned quanto
    ///      Destroy() pendente).
    ///
    ///   3. CLEANUP DE ALVO EM MORTE/RESPAWN (mantido):
    ///      OnServerDeath e OnServerRespawn limpam CurrentTarget se ele
    ///      ficou inválido.
    ///
    ///   4. EVENTO OnTargetChanged (mantido):
    ///      Notifica observers quando CurrentTarget muda.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        public static readonly HashSet<PlayerEntity> All = new HashSet<PlayerEntity>();

        // ── Estado autoritativo ────────────────────────────────────────────
        public CharacterData Data  { get; private set; }
        public DerivedStats  Stats { get; private set; }

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }

        public bool IsInitialized => Data != null && Stats != null;
        public bool IsDead        => CurrentHP <= 0f;

        // ── Eventos para a UI ──────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;
        public event Action               OnInitialized;
        public event Action<ITargetable>  OnTargetChanged;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        private Camera _cachedCamera;
        /// <summary>
        /// Câmera principal cacheada. Usa o operador `==` overloaded do Unity
        /// (que checa destruição) para refazer o cache se necessário.
        /// </summary>
        public Camera MainCamera
        {
            get
            {
                if (_cachedCamera == null)
                    _cachedCamera = Camera.main;
                return _cachedCamera;
            }
        }

        public ITargetable CurrentTarget { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _cachedCamera = Camera.main;
        }

        private void OnEnable()  => All.Add(this);
        private void OnDisable() => All.Remove(this);

        // ── Inicialização ──────────────────────────────────────────────────

        public void InitializeFromServer(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogError("[PlayerEntity] InitializeFromServer: data nulo.");
                return;
            }

            Data  = data;
            Stats = data.GetDerivedStats();

            CurrentHP = Mathf.Clamp(data.CurrentHP, 0f, Stats.MaxHP);
            CurrentMP = Mathf.Clamp(data.CurrentMP, 0f, Stats.MaxMP);

            ConfigureAgent();

            OnInitialized?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Atualizações vindas do servidor ────────────────────────────────

        public void SetHPFromServer(float hp, float maxHp)
        {
            if (!IsInitialized) return;

            bool wasDead = IsDead;

            if (!Mathf.Approximately(Stats.MaxHP, maxHp))
            {
                var updated = Stats.Clone();
                updated.MaxHP = maxHp;
                Stats = updated;
            }

            CurrentHP = Mathf.Clamp(hp, 0f, maxHp);
            OnHPChanged?.Invoke(CurrentHP, maxHp);

            bool nowDead = IsDead;
            if (nowDead != wasDead)
            {
                if (nowDead && _agent != null && _agent.isOnNavMesh)
                    _agent.ResetPath();
                OnDeathChanged?.Invoke(nowDead);
            }
        }

        public void SetMPFromServer(float mp, float maxMp)
        {
            if (!IsInitialized) return;

            if (!Mathf.Approximately(Stats.MaxMP, maxMp))
            {
                var updated = Stats.Clone();
                updated.MaxMP = maxMp;
                Stats = updated;
            }

            CurrentMP = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void RefreshStatsFromServer(float maxHp, float maxMp)
        {
            if (!IsInitialized) return;

            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = Mathf.Min(CurrentHP, maxHp);
            CurrentMP = Mathf.Min(CurrentMP, maxMp);

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void FullRefreshStatsFromData()
        {
            if (!IsInitialized || Data == null) return;

            Stats = Data.GetDerivedStats();

            ConfigureAgent();

            CurrentHP = Mathf.Min(CurrentHP, Stats.MaxHP);
            CurrentMP = Mathf.Min(CurrentMP, Stats.MaxMP);

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public void UpdateDataFromServer(int level, long exp, long expToNext,
                                         int freePoints,
                                         int allocSTR, int allocAGI, int allocVIT,
                                         int allocDEX, int allocINT, int allocLUK)
        {
            if (Data == null) return;
            Data.Level                 = level;
            Data.Experience            = exp;
            Data.ExperienceToNextLevel = expToNext;
            Data.FreeAttributePoints   = freePoints;
            Data.AllocatedSTR          = allocSTR;
            Data.AllocatedAGI          = allocAGI;
            Data.AllocatedVIT          = allocVIT;
            Data.AllocatedDEX          = allocDEX;
            Data.AllocatedINT          = allocINT;
            Data.AllocatedLUK          = allocLUK;
        }

        // ── Morte e Respawn ────────────────────────────────────────────────

        public void OnServerDeath()
        {
            CurrentHP = 0f;
            if (_agent != null && _agent.isOnNavMesh)
                _agent.ResetPath();

            // Limpa alvo ao morrer — evita que sistemas continuem operando
            // contra um target stale após o respawn.
            if (CurrentTarget != null)
                ClearTarget();

            OnHPChanged?.Invoke(0f, Stats?.MaxHP ?? 1f);
            OnDeathChanged?.Invoke(true);
        }

        public void OnServerRespawn(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!IsInitialized) return;

            transform.position = position;
            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(position);

            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = hp;
            CurrentMP = mp;

            // Garante alvo limpo no respawn (belt-and-suspenders).
            if (CurrentTarget != null)
                ClearTarget();

            OnDeathChanged?.Invoke(false);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        // ── Movimento ──────────────────────────────────────────────────────

        /// <summary>
        /// SetDestination puro, sem ResetPath, sem velocity = 0.
        /// O agent substitui o path em curso sem parar.
        /// </summary>
        public void MoveToConfirmed(Vector3 destination)
        {
            if (IsDead || _agent == null || !_agent.isOnNavMesh) return;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

        /// <summary>
        /// Para o movimento de forma suave: limpa o path.
        /// NÃO zera velocity — o brake natural lida com desaceleração.
        /// </summary>
        public void StopMovement()
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.ResetPath();
        }

        public bool HasReachedDestination()
        {
            if (_agent == null) return true;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ── Alvo ──────────────────────────────────────────────────────────

        public void SetTarget(ITargetable target)
        {
            if (CurrentTarget == target) return;

            CurrentTarget?.OnDeselected();
            CurrentTarget = target;
            CurrentTarget?.OnSelected();

            OnTargetChanged?.Invoke(target);
        }

        public void ClearTarget()
        {
            if (CurrentTarget == null) return;

            CurrentTarget.OnDeselected();
            CurrentTarget = null;

            OnTargetChanged?.Invoke(null);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Configuração profissional do NavMeshAgent para movimento fluido.
        /// Lê valores de GameConstants.Movement (fonte única de verdade).
        /// </summary>
        private void ConfigureAgent()
        {
            if (_agent == null || Stats == null) return;

            _agent.speed            = Mathf.Clamp(
                Stats.MoveSpeed,
                GameConstants.Movement.PLAYER_ENTITY_MIN_SPEED,
                GameConstants.Movement.PLAYER_ENTITY_MAX_SPEED);
            _agent.acceleration     = GameConstants.Movement.AGENT_ACCELERATION;
            _agent.angularSpeed     = GameConstants.Movement.AGENT_ANGULAR_SPEED;
            _agent.autoBraking      = GameConstants.Movement.AGENT_AUTO_BRAKING;
            _agent.stoppingDistance = GameConstants.Movement.AGENT_STOPPING_DISTANCE;
        }
    }
}
