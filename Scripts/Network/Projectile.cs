using UnityEngine;
using Mirror;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// Projétil server-authoritative.
    ///
    /// === MUDANÇAS DESTA VERSÃO (UX de miss em ranged) ===
    ///
    ///   1. FLAG _isMiss:
    ///      Quando true, o projétil voa normalmente mas no impacto mostra
    ///      "MISS" em vez de aplicar dano. Necessário para feedback consistente
    ///      em ataques ranged que erram (arqueiros, magos): o player vê a
    ///      flecha viajar, e o miss aparece no momento certo.
    ///
    ///   2. _shooterNetId:
    ///      Armazena o netId do atacante para creditar dano corretamente
    ///      no damageLog do alvo (ServerTakeProjectileDamage). Garante que
    ///      kills à distância dão XP ao caster certo.
    ///
    ///   3. ServerInitialize REASSINATURA:
    ///      Agora recebe (target, speed, damage, crit, shooterNetId, isMiss).
    ///      Callers existentes precisam ser atualizados — apenas
    ///      NetworkMonsterEntity.SpawnAttackProjectile.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class Projectile : NetworkBehaviour
    {
        [Header("Configuração")]
        [Tooltip("Velocidade angular máxima (deg/s) para seguir o alvo.")]
        [SerializeField] private float maxTurnRate = 360f;

        [Tooltip("Distância de impacto (m). Quando chegar a essa distância do alvo, aplica dano/miss.")]
        [SerializeField] private float impactDistance = 0.6f;

        [Tooltip("Tempo máximo de vida em segundos. Auto-destroi se não acertar.")]
        [SerializeField] private float maxLifetime = 6f;

        [Tooltip("Efeito visual ao impacto (opcional, instanciado client-side).")]
        [SerializeField] private GameObject hitVfxPrefab;

        // ── SyncVars para sync visual do projétil em movimento ─────────────
        [SyncVar] private uint    _targetNetId;
        [SyncVar] private Vector3 _initialDirection;

        // Estado lógico só no servidor
        private float            _speed;
        private float            _damage;
        private bool             _crit;
        private float            _spawnTime;
        private NetworkBehaviour _serverTarget;
        private bool             _hitProcessed;
        private uint             _shooterNetId;
        private bool             _isMiss;

        // Cliente: failsafe
        private float _clientSpawnTime;

        // ══════════════════════════════════════════════════════════════════
        // API do servidor
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inicializa o projétil no servidor. DEVE ser chamado IMEDIATAMENTE
        /// após NetworkServer.Spawn(prefab).
        ///
        /// Parâmetros:
        ///   - target:       alvo do projétil (NetworkMonsterEntity ou NetworkPlayer)
        ///   - speed:        velocidade em m/s
        ///   - damage:       dano JÁ CALCULADO (ignorado se isMiss=true)
        ///   - crit:         se foi crítico (afeta visual)
        ///   - shooterNetId: netId do atacante, para creditar XP em kills à distância
        ///   - isMiss:       se true, voa mas mostra MISS no impacto sem aplicar dano
        /// </summary>
        [Server]
        public void ServerInitialize(NetworkBehaviour target, float speed,
                                     float damage, bool crit,
                                     uint shooterNetId, bool isMiss)
        {
            _serverTarget = target;
            _speed        = Mathf.Max(1f, speed);
            _damage       = Mathf.Max(0f, damage);
            _crit         = crit;
            _spawnTime    = Time.time;
            _hitProcessed = false;
            _shooterNetId = shooterNetId;
            _isMiss       = isMiss;
            _targetNetId  = target != null && target.netIdentity != null
                ? target.netIdentity.netId
                : 0u;

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

            // Cliente: failsafe se o servidor não destruiu o projétil
            if (Time.time - _clientSpawnTime > maxLifetime + 0.5f)
                gameObject.SetActive(false);
        }

        [Server]
        private void ServerUpdate()
        {
            if (Time.time - _spawnTime > maxLifetime)
            {
                NetworkServer.Destroy(gameObject);
                return;
            }

            Vector3 desiredDir = _initialDirection;

            // Homing leve: se o alvo ainda existe e está vivo, atualiza direção
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

            // Rotação clamped (homing suave)
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

            if (_isMiss)
            {
                // Projétil errou — mostra MISS no impacto, sem dano
                if (_serverTarget is NetworkMonsterEntity monster && !monster.IsDead)
                    monster.ServerTakeProjectileMiss();
                // (PvP futuro: NetworkPlayer.ServerTakeProjectileMiss equivalente)

                RpcOnImpact(transform.position);
                NetworkServer.Destroy(gameObject);
                return;
            }

            // HIT — aplica dano ao tipo correto de alvo
            if (_serverTarget is NetworkMonsterEntity hitMonster && !hitMonster.IsDead)
            {
                hitMonster.ServerTakeProjectileDamage(_damage, _crit, _shooterNetId);
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
