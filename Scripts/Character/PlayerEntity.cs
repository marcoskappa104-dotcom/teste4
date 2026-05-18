using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using RPG.Data;

namespace RPG.Character
{
    /// <summary>
    /// Representação local (cliente) do estado do jogador.
    ///
    /// === MUDANÇAS DESTA VERSÃO (correções críticas) ===
    ///
    ///   1. BUG CORRIGIDO EM ClearTarget:
    ///      A versão anterior tinha lógica INVERTIDA para detectar alvos
    ///      destruídos. Quando CurrentTarget era um UnityEngine.Object
    ///      destruído, o operador == overloaded retornava true (igual a null),
    ///      então hadTarget=false, e a segunda condição checava obj!=null
    ///      que TAMBÉM era false — o evento nunca disparava nesse caso.
    ///      Agora detectamos alvo destruído via try/catch + cleanup garantido.
    ///
    ///   2. SetTarget EDGE CASE:
    ///      Comparação `CurrentTarget == target` podia retornar true mesmo
    ///      com alvo destruído (operador ==), bloqueando re-seleção legítima.
    ///      Agora consideramos "destruído" como diferente de qualquer alvo novo.
    ///
    ///   3. CAMERA CACHE EM SCENE CHANGE:
    ///      Inscreve-se em sceneLoaded para invalidar cache da Camera.main
    ///      automaticamente quando há troca de cena (raro, mas defensivo).
    ///
    ///   4. NULL-GUARD EM Stats (mantido do refactor anterior).
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

        private void OnEnable()
        {
            All.Add(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            All.Remove(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Invalida cache; será re-buscado no próximo acesso
            _cachedCamera = null;
        }

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
            if (Stats == null) return;

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

        // ══════════════════════════════════════════════════════════════════
        // Target — com correção do bug crítico de detecção de alvo destruído
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifica se um ITargetable se tornou inválido (destruído pelo Unity).
        /// Necessário porque o operador == do UnityEngine.Object é overloaded:
        /// ele retorna `true` quando o objeto foi destruído mesmo que a
        /// referência C# ainda exista. Para nossa lógica de evento, queremos
        /// detectar isso explicitamente.
        /// </summary>
        private static bool IsTargetUnityDestroyed(ITargetable t)
        {
            if (t == null) return false;
            if (t is UnityEngine.Object obj) return obj == null;
            return false;
        }

        public void SetTarget(ITargetable target)
        {
            // Se CurrentTarget está destruído, qualquer novo target é diferente
            bool currentIsDead = IsTargetUnityDestroyed(CurrentTarget);

            if (!currentIsDead && CurrentTarget == target) return;

            // Tenta deselecionar o antigo (pode estar destruído)
            if (!currentIsDead)
            {
                try { CurrentTarget?.OnDeselected(); }
                catch (MissingReferenceException) { /* destruído entre check e call */ }
            }

            CurrentTarget = target;

            try { CurrentTarget?.OnSelected(); }
            catch (MissingReferenceException)
            {
                // O alvo foi destruído entre SetTarget e OnSelected — descarta
                CurrentTarget = null;
                OnTargetChanged?.Invoke(null);
                return;
            }

            OnTargetChanged?.Invoke(target);
        }

        public void ClearTarget()
        {
            // Considera "tinha alvo" se a referência C# não é null, INDEPENDENTE
            // de o objeto Unity estar destruído. Assim garantimos que o evento
            // dispara mesmo quando o monstro/jogador foi destruído.
            bool hadTarget = !ReferenceEquals(CurrentTarget, null);
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
