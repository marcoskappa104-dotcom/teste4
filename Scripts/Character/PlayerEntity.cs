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
    /// === MUDANÇAS DESTA VERSÃO (correções de robustez) ===
    ///
    ///   1. NULL-GUARD EM Stats ANTES DE Clone:
    ///      SetHPFromServer/SetMPFromServer chamavam Stats.Clone() sem
    ///      checar se Stats era null. Em casos extremos (RPC chegando
    ///      antes da inicialização completa), isso crashava com NullRef.
    ///      Agora retornamos cedo se Stats == null e IsInitialized é a
    ///      única porta de entrada confiável.
    ///
    ///   2. CAMERA CACHE MAIS DEFENSIVO:
    ///      MainCamera agora trata destruição via operador == do Unity,
    ///      mas também aceita re-cacheamento em runtime via troca de cena.
    ///
    ///   3. ClearTarget IDEMPOTENTE:
    ///      Antes, ClearTarget early-return se CurrentTarget == null, ok.
    ///      Mas se CurrentTarget é um UnityEngine.Object destruído, o
    ///      check `== null` retorna true (operador overloaded), então
    ///      o evento OnTargetChanged não disparava. Agora detectamos
    ///      essa situação e disparamos o evento mesmo assim.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        public static readonly HashSet<PlayerEntity> All = new HashSet<PlayerEntity>();

        // Configuração de NavMeshAgent — manter em sync com NetworkPlayer
        private const float AGENT_ACCELERATION   = 60f;
        private const float AGENT_ANGULAR_SPEED  = 720f;
        private const float AGENT_STOPPING_DIST  = 0.15f;
        private const float AGENT_MIN_SPEED      = 2f;
        private const float AGENT_MAX_SPEED      = 10f;

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

            if (Stats == null)
            {
                Debug.LogError("[PlayerEntity] InitializeFromServer: GetDerivedStats retornou null.");
                return;
            }

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
            if (Stats == null) return; // Defesa em profundidade

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
            if (Stats == null) return;

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
            if (Stats == null) return;

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

            var newStats = Data.GetDerivedStats();
            if (newStats == null) return;

            Stats = newStats;
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

            if (CurrentTarget != null)
                ClearTarget();

            OnHPChanged?.Invoke(0f, Stats?.MaxHP ?? 1f);
            OnDeathChanged?.Invoke(true);
        }

        public void OnServerRespawn(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats == null) return;

            transform.position = position;
            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(position);

            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = hp;
            CurrentMP = mp;

            if (CurrentTarget != null)
                ClearTarget();

            OnDeathChanged?.Invoke(false);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        // ── Movimento ──────────────────────────────────────────────────────

        public void MoveToConfirmed(Vector3 destination)
        {
            if (IsDead || _agent == null || !_agent.isOnNavMesh) return;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

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

            // Trata caso em que CurrentTarget é UnityEngine.Object destruído
            // (o == null já retorna true via operador overloaded, mas a
            // chamada de método pode crashar)
            try { CurrentTarget?.OnDeselected(); }
            catch (MissingReferenceException) { /* alvo destruído — ok */ }

            CurrentTarget = target;
            CurrentTarget?.OnSelected();

            OnTargetChanged?.Invoke(target);
        }

        public void ClearTarget()
        {
            // Detecta também alvos destruídos (operador == overloaded)
            bool hadTarget = CurrentTarget != null;
            if (!hadTarget && CurrentTarget is UnityEngine.Object obj && obj != null)
                hadTarget = true;

            if (!hadTarget) return;

            try { CurrentTarget?.OnDeselected(); }
            catch (MissingReferenceException) { /* alvo destruído — ok */ }

            CurrentTarget = null;
            OnTargetChanged?.Invoke(null);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ConfigureAgent()
        {
            if (_agent == null || Stats == null) return;

            _agent.speed            = Mathf.Clamp(Stats.MoveSpeed, AGENT_MIN_SPEED, AGENT_MAX_SPEED);
            _agent.acceleration     = AGENT_ACCELERATION;
            _agent.angularSpeed     = AGENT_ANGULAR_SPEED;
            _agent.autoBraking      = false;
            _agent.stoppingDistance = AGENT_STOPPING_DIST;
        }
    }
}