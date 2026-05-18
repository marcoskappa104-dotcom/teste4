using UnityEngine;
using Mirror;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// Projétil server-authoritative. Spawn pelo servidor após validar o ataque
    /// ranged; viaja em direção ao alvo; ao impacto, servidor aplica o dano e
    /// destrói o projétil.
    ///
    /// === DESIGN ===
    ///   - O DANO já foi calculado quando o projétil nasceu. Isso garante que
    ///     o resultado é coerente com os stats do atacante naquele instante,
    ///     mesmo se ele morrer enquanto o projétil voa.
    ///   - O HOMING é leve: o projétil ajusta a direção a cada frame para seguir
    ///     o alvo, mas com velocidade angular limitada. Se o alvo morrer ou
    ///     for muito longe, segue em linha reta e morre por timeout.
    ///   - Não usa colliders/Rigidbody — distância manual é mais determinística
    ///     em multiplayer e mais barata.
    ///   - O ShooterNetId é sincronizado para que NetworkMonsterEntity possa
    ///     creditar XP corretamente no impacto (não no disparo).
    ///
    /// === MUDANÇAS DESTA VERSÃO ===
    ///
    ///   1. SHOOTER NET ID CARREGADO NO PROJÉTIL:
    ///      Para que o XP seja creditado SOMENTE quando o dano é aplicado
    ///      (e não quando o projétil é disparado), o atacante precisa ser
    ///      identificável no momento do impacto. ShooterNetId é SyncVar.
    ///
    ///   2. ROTAÇÃO DEFENSIVA NO CLIENTE:
    ///      OnStartClient setava transform.rotation = LookRotation(_initialDirection)
    ///      sem garantir que _initialDirection era não-zero. Quando ServerInitialize
    ///      é chamado APÓS NetworkServer.Spawn (caso normal), o cliente pode
    ///      receber o Spawn antes da SyncVar estar populada → Vector3.zero
    ///      → warning "look rotation viewing vector is zero".
    ///      Agora checamos sqrMagnitude antes de usar.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class Projectile : NetworkBehaviour
    {
        [Header("Configuração")]
        [Tooltip("Velocidade angular máxima (deg/s) para seguir o alvo.")]
        [SerializeField] private float maxTurnRate = 360f;

        [Tooltip("Distância de impacto (m). Quando chegar a essa distância do alvo, aplica dano.")]
        [SerializeField] private float impactDistance = 0.6f;

        [Tooltip("Tempo máximo de vida em segundos. Auto-destroi se não acertar.")]
        [SerializeField] private float maxLifetime = 6f;

        [Tooltip("Efeito visual ao impacto (opcional, instanciado client-side).")]
        [SerializeField] private GameObject hitVfxPrefab;

        // ── Dados de runtime (apenas servidor escreve; SyncVars para client follow) ──

        [SyncVar] private uint    _targetNetId;
        [SyncVar] private uint    _shooterNetId;
        [SyncVar] private Vector3 _initialDirection;

        // Estado lógico só no servidor
        private float                _speed;
        private float                _damage;
        private bool                 _crit;
        private float                _spawnTime;
        private NetworkBehaviour     _serverTarget;
        private bool                 _hitProcessed;

        // No cliente: fallback se o NetworkTransform falhar
        private float _clientSpawnTime;

        // ══════════════════════════════════════════════════════════════════
        // API do servidor
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inicializa o projétil no servidor. DEVE ser chamado IMEDIATAMENTE
        /// após NetworkServer.Spawn(prefab).
        ///
        /// shooterNetId é usado para creditar XP no momento do impacto
        /// (não do disparo).
        /// </summary>
        [Server]
        public void ServerInitialize(NetworkBehaviour target, uint shooterNetId,
                                     float speed, float damage, bool crit)
        {
            _serverTarget     = target;
            _shooterNetId     = shooterNetId;
            _speed            = Mathf.Max(1f, speed);
            _damage           = Mathf.Max(0f, damage);
            _crit             = crit;
            _spawnTime        = Time.time;
            _hitProcessed     = false;
            _targetNetId      = target != null && target.netIdentity != null ? target.netIdentity.netId : 0u;

            // Direção inicial em direção ao alvo (ou frente do projétil se alvo nulo)
            if (target != null)
            {
                Vector3 dir = (target.transform.position - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                {
                    _initialDirection = dir.normalized;
                    transform.rotation = Quaternion.LookRotation(_initialDirection);
                }
                else
                {
                    _initialDirection = transform.forward;
                }
            }
            else
            {
                _initialDirection = transform.forward;
            }
        }

        public override void OnStartClient()
        {
            _clientSpawnTime = Time.time;

            // Defensivo: _initialDirection pode estar zerado se a SyncVar
            // ainda não tiver propagado (raro mas possível). Só aplica
            // rotação se direção é não-zero.
            if (_initialDirection.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(_initialDirection);
        }

        // ══════════════════════════════════════════════════════════════════
        // Update
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (isServer)
            {
                ServerUpdate();
                return;
            }

            // Cliente: failsafe se o servidor não destruiu por algum motivo
            if (Time.time - _clientSpawnTime > maxLifetime + 0.5f)
                gameObject.SetActive(false);
        }

        [Server]
        private void ServerUpdate()
        {
            // Timeout
            if (Time.time - _spawnTime > maxLifetime)
            {
                NetworkServer.Destroy(gameObject);
                return;
            }

            Vector3 desiredDir = _initialDirection;

            // Homing leve
            if (_serverTarget != null && !TargetIsDeadOrGone(_serverTarget))
            {
                Vector3 toTarget = _serverTarget.transform.position - transform.position;
                toTarget.y = 0f;
                float sqr = toTarget.sqrMagnitude;

                if (sqr > 0.001f)
                {
                    desiredDir = toTarget.normalized;

                    if (sqr <= impactDistance * impactDistance)
                    {
                        ApplyImpact();
                        return;
                    }
                }
            }

            // Rotação clamped
            Vector3 currentForward = transform.forward;
            currentForward.y = 0f;
            if (currentForward.sqrMagnitude > 0.001f)
            {
                currentForward.Normalize();
                float angle = Vector3.Angle(currentForward, desiredDir);
                float maxStep = maxTurnRate * Time.deltaTime;
                if (angle > maxStep)
                {
                    Vector3 cross = Vector3.Cross(currentForward, desiredDir);
                    float sign    = Mathf.Sign(cross.y);
                    Quaternion q  = Quaternion.AngleAxis(maxStep * sign, Vector3.up);
                    desiredDir    = q * currentForward;
                }
                transform.rotation = Quaternion.LookRotation(desiredDir);
            }

            // Movimento
            transform.position += transform.forward * (_speed * Time.deltaTime);
        }

        [Server]
        private bool TargetIsDeadOrGone(NetworkBehaviour nb)
        {
            if (nb == null) return true;
            if (nb is ITargetable t) return t.IsDead;
            return false;
        }

        [Server]
        private void ApplyImpact()
        {
            if (_hitProcessed) return;
            _hitProcessed = true;

            // Aplica dano dependendo do tipo de alvo, passando o shooter netId
            // para que o XP seja creditado corretamente.
            if (_serverTarget is NetworkMonsterEntity monster && !monster.IsDead)
            {
                monster.ServerTakeProjectileDamage(_shooterNetId, _damage, _crit);
            }
            else if (_serverTarget is NetworkPlayer player && !player.Dead)
            {
                // Reservado para PvP futuro
                player.ServerApplyDamageWithFeedback(_damage);
            }

            RpcOnImpact(transform.position);
            NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // VFX no cliente
        // ══════════════════════════════════════════════════════════════════

        [ClientRpc]
        private void RpcOnImpact(Vector3 pos)
        {
            if (Application.isBatchMode) return;
            if (hitVfxPrefab != null)
            {
                var vfx = Instantiate(hitVfxPrefab, pos, Quaternion.identity);
                Destroy(vfx, 2f);
            }
        }
    }
}
